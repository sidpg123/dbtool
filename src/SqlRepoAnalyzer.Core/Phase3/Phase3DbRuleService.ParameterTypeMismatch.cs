using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Core.Tsql;

namespace SqlRepoAnalyzer.Core.Phase3;

public static partial class Phase3DbRuleService
{
    private static readonly JsonSerializerOptions BindingJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static IEnumerable<Phase3RuleFinding> CheckParameterColumnTypeMismatch(
        IReadOnlyList<QueryRecord> queries,
        Dictionary<string, HashSet<string>> refsByTable,
        IReadOnlyDictionary<string, CatalogColumnMeta> columnCatalog,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();

        foreach (var q in queries)
        {
            if (string.IsNullOrWhiteSpace(q.SqlText) || string.IsNullOrWhiteSpace(q.QueryId))
                continue;

            var referencedTables = refsByTable
                .Where(kv => kv.Value.Contains(q.QueryId))
                .Select(kv => kv.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (referencedTables.Length == 0)
                continue;

            var cteNames = ExtractCteNames(q.SqlText);
            var parse = TsqlParser.Parse(q.SqlText);
            if (!parse.Success || parse.Fragment is null)
            {
                if (!string.IsNullOrWhiteSpace(q.ParameterBindingsJson) &&
                    TryDeserializeBindings(q.ParameterBindingsJson, out var bindOnly) &&
                    bindOnly.Any(static b => b.Index is int ix && ix > 0))
                {
                    findings.AddRange(
                        AnalyzeDollarPlaceholderHeuristic(q, referencedTables, columnCatalog, bindOnly, cteNames));
                }

                continue;
            }

            IReadOnlyList<BindingRow> bindings = Array.Empty<BindingRow>();
            if (!string.IsNullOrWhiteSpace(q.ParameterBindingsJson) &&
                TryDeserializeBindings(q.ParameterBindingsJson, out var parsedBindings))
                bindings = parsedBindings;

            var usage = new QueryColumnUsageVisitor();
            parse.Fragment.Accept(usage);

            var aliasToTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in usage.Tables)
            {
                var tableKey = $"{t.Schema}.{t.Name}";
                if (!referencedTables.Contains(tableKey, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(t.Alias))
                    aliasToTable[t.Alias] = tableKey;
                aliasToTable[t.Name] = tableKey;
            }

            var extractor = new ColumnParameterLiteralEqualityExtractor(
                referencedTables,
                aliasToTable,
                cteNames,
                bindings);
            parse.Fragment.Accept(extractor);

            foreach (var row in extractor.Rows)
            {
                if (!TryResolveColumnCatalogKey(row, referencedTables, columnCatalog, out var catalogKey, out var colMeta))
                    continue;

                var bindingCategory = ClassifyBinding(row, bindings);
                var literalCategory = ClassifyLiteralSide(row);
                var columnCategory = ClassifyColumnCategory(colMeta);

                string? reason = null;
                if (bindingCategory != TypeCategory.Unknown &&
                    TryMismatchReason(columnCategory, bindingCategory, "parameter", out var r1))
                    reason = r1;
                else if (literalCategory != TypeCategory.Unknown &&
                         TryMismatchReason(columnCategory, literalCategory, "literal", out var r2))
                    reason = r2;

                if (reason is null)
                    continue;

                findings.Add(new Phase3RuleFinding
                {
                    RuleId = "db.parameter_type_mismatch",
                    Status = "warn",
                    Severity = "warn",
                    Message =
                        $"Possible implicit conversion: column `{catalogKey}` compared to {row.ComparisonKind} that does not match catalog type `{colMeta.TypeName}` in query `{q.QueryId}`.",
                    Recommendation =
                        "Align parameter or literal types with the column (e.g. pass strings for varchar parameters, correct SqlDbType / TypeORM bindings) to avoid implicit conversions and sargability loss.",
                    AffectedObjects = new[] { catalogKey, q.QueryId },
                    QueryIds = new[] { q.QueryId },
                    Evidence = new Dictionary<string, object?>
                    {
                        ["column"] = catalogKey,
                        ["columnSqlType"] = colMeta.TypeName,
                        ["comparison"] = row.ComparisonKind,
                        ["details"] = reason,
                        ["fingerprint"] = q.Fingerprint
                    }
                });
            }

            if (bindings.Any(static b => b.Index is int ix && ix > 0))
                findings.AddRange(AnalyzeDollarPlaceholderHeuristic(q, referencedTables, columnCatalog, bindings, cteNames));
        }

        if (findings.Count > 0)
            return findings;

        return new[]
        {
            new Phase3RuleFinding
            {
                RuleId = "db.parameter_type_mismatch",
                Status = "pass",
                Severity = "info",
                Message =
                    "No parameter/literal vs column type mismatches were detected for scoped queries (or no binding metadata / comparable predicates were present).",
                QueryIds = allQueryIds
            }
        };
    }

    private static IEnumerable<Phase3RuleFinding> AnalyzeDollarPlaceholderHeuristic(
        QueryRecord q,
        IReadOnlyCollection<string> referencedTables,
        IReadOnlyDictionary<string, CatalogColumnMeta> columnCatalog,
        IReadOnlyList<BindingRow> bindings,
        HashSet<string> cteNames)
    {
        var findings = new List<Phase3RuleFinding>();
        var byIndex = bindings.Where(static b => b.Index is int ix && ix > 0).ToDictionary(b => b.Index!.Value, b => b);
        if (byIndex.Count == 0 || string.IsNullOrWhiteSpace(q.SqlText))
            return findings;

        foreach (Match m in DollarPlaceholderEq.Matches(q.SqlText))
        {
            var left = m.Groups[1].Value.Trim();
            var idx = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (!byIndex.TryGetValue(idx, out var bindRow))
                continue;

            if (!TrySplitColumnQualifier(left, referencedTables, cteNames, out var tableKey, out var columnName))
                continue;

            var catalogKey = $"{tableKey}.{columnName}";
            if (!columnCatalog.TryGetValue(catalogKey, out var colMeta))
                continue;

            var bindCat = ClassifyBindingRow(bindRow);
            if (bindCat == TypeCategory.Unknown)
                continue;

            if (!TryMismatchReason(ClassifyColumnCategory(colMeta), bindCat, $"placeholder ${idx}", out var reason))
                continue;

            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.parameter_type_mismatch",
                Status = "warn",
                Severity = "warn",
                Message =
                    $"Possible implicit conversion: `{catalogKey}` vs TypeORM binding index {idx} in query `{q.QueryId}`.",
                Recommendation =
                    "Align JS/TS values and PostgreSQL-style placeholders with SQL Server column types, or use typed parameters.",
                AffectedObjects = new[] { catalogKey, q.QueryId },
                QueryIds = new[] { q.QueryId },
                Evidence = new Dictionary<string, object?>
                {
                    ["column"] = catalogKey,
                    ["columnSqlType"] = colMeta.TypeName,
                    ["placeholderIndex"] = idx,
                    ["details"] = reason,
                    ["fingerprint"] = q.Fingerprint
                }
            });
        }

        return findings;
    }

    private static readonly Regex DollarPlaceholderEq = new(
        @"\b([\w\.\[\]]+)\s*=\s*\$(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool TrySplitColumnQualifier(
        string left,
        IReadOnlyCollection<string> referencedTables,
        HashSet<string> cteNames,
        out string tableKey,
        out string columnName)
    {
        tableKey = "";
        columnName = "";
        left = left.Trim('[', ']');
        var parts = left.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var col = parts[^1];
            var tbl = string.Join(".", parts.Take(parts.Length - 1));
            if (parts.Length >= 3)
            {
                var sch = parts[^3];
                var tname = parts[^2];
                var candidate = $"{sch}.{tname}";
                if (referencedTables.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    tableKey = candidate;
                    columnName = col;
                    return true;
                }
            }

            var two = $"{parts[^2]}.{parts[^1]}";
            if (referencedTables.Contains(two, StringComparer.OrdinalIgnoreCase))
            {
                tableKey = two;
                columnName = col;
                return true;
            }
        }

        if (parts.Length == 1 && referencedTables.Count == 1)
        {
            tableKey = referencedTables.First();
            columnName = parts[0];
            return !cteNames.Contains(columnName);
        }

        return false;
    }

    private sealed class BindingRow
    {
        public string? Name { get; set; }
        public int? Index { get; set; }
        public string? ProviderType { get; set; }
        public string? SqlDbType { get; set; }
        public string? TsKind { get; set; }
    }

    private static bool TryDeserializeBindings(string json, out IReadOnlyList<BindingRow> rows)
    {
        rows = Array.Empty<BindingRow>();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var list = JsonSerializer.Deserialize<List<BindingRow>>(json, BindingJsonOptions);
            if (list is null || list.Count == 0)
                return false;
            rows = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum TypeCategory
    {
        Unknown,
        StringFamily,
        IntFamily,
        FloatFamily,
        DateFamily,
        BoolFamily,
        GuidFamily,
        BinaryFamily
    }

    private static TypeCategory ClassifyColumnCategory(CatalogColumnMeta c)
    {
        if (IsStringLikeType(c.TypeName, c.SystemTypeId))
            return TypeCategory.StringFamily;
        if (IsIntLikeSystemTypeId(c.SystemTypeId))
            return TypeCategory.IntFamily;
        if (IsFloatLikeTypeName(c.TypeName))
            return TypeCategory.FloatFamily;
        if (IsDateLikeTypeName(c.TypeName))
            return TypeCategory.DateFamily;
        if (c.SystemTypeId == 104)
            return TypeCategory.BoolFamily;
        if (c.SystemTypeId == 36)
            return TypeCategory.GuidFamily;
        if (IsBinaryLikeTypeName(c.TypeName))
            return TypeCategory.BinaryFamily;
        return TypeCategory.Unknown;
    }

    private static bool IsIntLikeSystemTypeId(int systemTypeId) =>
        systemTypeId is 56 or 48 or 52 or 127 or 59 or 60 or 122;

    private static bool IsFloatLikeTypeName(string typeName)
    {
        foreach (var n in new[] { "float", "real", "decimal", "numeric", "money", "smallmoney" })
        {
            if (typeName.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsDateLikeTypeName(string typeName)
    {
        foreach (var n in new[] { "date", "datetime", "datetime2", "smalldatetime", "datetimeoffset", "time" })
        {
            if (typeName.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsBinaryLikeTypeName(string typeName)
    {
        foreach (var n in new[] { "binary", "varbinary", "image", "rowversion", "timestamp" })
        {
            if (typeName.StartsWith(n, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static TypeCategory ClassifyBinding(ComparisonRow row, IReadOnlyList<BindingRow> bindings)
    {
        if (bindings.Count == 0 || row.ParamName is null)
            return TypeCategory.Unknown;

        var name = row.ParamName.Trim();
        if (!name.StartsWith("@", StringComparison.Ordinal))
            name = "@" + name;

        foreach (var b in bindings)
        {
            if (string.IsNullOrWhiteSpace(b.Name))
                continue;
            var bn = b.Name.Trim();
            if (!bn.StartsWith("@", StringComparison.Ordinal))
                bn = "@" + bn;
            if (!string.Equals(bn, name, StringComparison.OrdinalIgnoreCase))
                continue;
            return ClassifyBindingRow(b);
        }

        return TypeCategory.Unknown;
    }

    private static TypeCategory ClassifyBindingRow(BindingRow b)
    {
        if (!string.IsNullOrWhiteSpace(b.SqlDbType))
            return MapSqlDbTypeName(b.SqlDbType);

        if (string.Equals(b.ProviderType, "typeScript", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(b.TsKind))
        {
            return b.TsKind.ToLowerInvariant() switch
            {
                "number" => TypeCategory.IntFamily,
                "string" => TypeCategory.StringFamily,
                "boolean" => TypeCategory.BoolFamily,
                _ => TypeCategory.Unknown
            };
        }

        return TypeCategory.Unknown;
    }

    private static TypeCategory MapSqlDbTypeName(string raw)
    {
        var t = raw.Trim();
        return t.ToUpperInvariant() switch
        {
            "NVARCHAR" or "NCHAR" or "NTEXT" or "XML" => TypeCategory.StringFamily,
            "VARCHAR" or "CHAR" or "TEXT" => TypeCategory.StringFamily,
            "BIGINT" or "INT" or "SMALLINT" or "TINYINT" => TypeCategory.IntFamily,
            "BIT" => TypeCategory.BoolFamily,
            "FLOAT" or "REAL" or "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" => TypeCategory.FloatFamily,
            "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "DATETIMEOFFSET" or "TIME" => TypeCategory.DateFamily,
            "UNIQUEIDENTIFIER" => TypeCategory.GuidFamily,
            "BINARY" or "VARBINARY" or "IMAGE" => TypeCategory.BinaryFamily,
            _ => TypeCategory.Unknown
        };
    }

    private static TypeCategory ClassifyLiteralSide(ComparisonRow row)
    {
        return row.LiteralKind switch
        {
            LiteralKindKind.Integer => TypeCategory.IntFamily,
            LiteralKindKind.String => TypeCategory.StringFamily,
            _ => TypeCategory.Unknown
        };
    }

    private static bool TryMismatchReason(TypeCategory column, TypeCategory other, string role, out string reason)
    {
        reason = "";
        if (column == TypeCategory.Unknown || other == TypeCategory.Unknown)
            return false;

        if (column == TypeCategory.StringFamily && other == TypeCategory.IntFamily)
        {
            reason =
                $"Column is string-like but {role} is numeric; SQL Server may convert the column to a numeric type.";
            return true;
        }

        if (column == TypeCategory.IntFamily && other == TypeCategory.StringFamily)
        {
            reason =
                $"Column is integer/numeric but {role} is string-like; implicit conversion or non-sargable plans are likely.";
            return true;
        }

        if (column == TypeCategory.DateFamily && (other == TypeCategory.IntFamily || other == TypeCategory.StringFamily))
        {
            reason = $"Column is date/time but {role} is not a date literal/typed parameter.";
            return true;
        }

        return false;
    }

    private static bool TryResolveColumnCatalogKey(
        ComparisonRow row,
        IReadOnlyCollection<string> referencedTables,
        IReadOnlyDictionary<string, CatalogColumnMeta> columnCatalog,
        out string catalogKey,
        out CatalogColumnMeta meta)
    {
        catalogKey = "";
        meta = default!;
        var tok = new ColumnRefToken(row.ColumnOwner, row.ColumnName);
        var tableKey = ResolveColumnsForTableMember(tok, row.AliasToTable, referencedTables, row.CteNames);
        if (tableKey is null)
            return false;
        catalogKey = $"{tableKey}.{row.ColumnName}";
        if (!columnCatalog.TryGetValue(catalogKey, out var m))
            return false;
        meta = m;
        return true;
    }

    private static string? VariableReferenceName(VariableReference vr) =>
        string.IsNullOrWhiteSpace(vr.Name) ? null : vr.Name;

    private enum LiteralKindKind
    {
        None,
        Integer,
        String
    }

    private sealed record ComparisonRow(
        string? ColumnOwner,
        string ColumnName,
        string? ParamName,
        LiteralKindKind LiteralKind,
        string ComparisonKind,
        IReadOnlyDictionary<string, string> AliasToTable,
        IReadOnlyCollection<string> ReferencedTables,
        HashSet<string> CteNames);

    private sealed class ColumnParameterLiteralEqualityExtractor : TSqlFragmentVisitor
    {
        private readonly IReadOnlyCollection<string> _referencedTables;
        private readonly IReadOnlyDictionary<string, string> _aliasToTable;
        private readonly HashSet<string> _cteNames;
        private readonly IReadOnlyList<BindingRow> _bindings;
        public List<ComparisonRow> Rows { get; } = new();

        public ColumnParameterLiteralEqualityExtractor(
            IReadOnlyCollection<string> referencedTables,
            IReadOnlyDictionary<string, string> aliasToTable,
            HashSet<string> cteNames,
            IReadOnlyList<BindingRow> bindings)
        {
            _referencedTables = referencedTables;
            _aliasToTable = aliasToTable;
            _cteNames = cteNames;
            _bindings = bindings;
        }

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            if (node.ComparisonType != BooleanComparisonType.Equals || node.SecondExpression is null)
            {
                base.ExplicitVisit(node);
                return;
            }

            TryAddPair(node.FirstExpression, node.SecondExpression);
            TryAddPair(node.SecondExpression, node.FirstExpression);
            base.ExplicitVisit(node);
        }

        private void TryAddPair(TSqlFragment? a, TSqlFragment? b)
        {
            var ca = UnwrapToColumn(a);
            if (ca is null)
                return;

            var t1 = ToColumnRefToken(ca);
            if (t1 is null)
                return;

            b = UnwrapParenExpr(b);
            if (b is VariableReference vr)
            {
                var name = VariableReferenceName(vr);
                if (string.IsNullOrWhiteSpace(name))
                    return;
                Rows.Add(new ComparisonRow(
                    t1.Owner,
                    t1.Column,
                    name,
                    LiteralKindKind.None,
                    $"variable `{name}`",
                    _aliasToTable,
                    _referencedTables,
                    _cteNames));
                return;
            }

            if (b is IntegerLiteral)
            {
                Rows.Add(new ComparisonRow(
                    t1.Owner,
                    t1.Column,
                    null,
                    LiteralKindKind.Integer,
                    "integer literal",
                    _aliasToTable,
                    _referencedTables,
                    _cteNames));
                return;
            }

            if (b is StringLiteral)
            {
                Rows.Add(new ComparisonRow(
                    t1.Owner,
                    t1.Column,
                    null,
                    LiteralKindKind.String,
                    "string literal",
                    _aliasToTable,
                    _referencedTables,
                    _cteNames));
            }
        }

        private static ColumnReferenceExpression? UnwrapToColumn(TSqlFragment? f)
        {
            f = UnwrapParenExpr(f);
            return f as ColumnReferenceExpression;
        }

        private static TSqlFragment? UnwrapParenExpr(TSqlFragment? f)
        {
            while (f is ParenthesisExpression p)
                f = p.Expression;
            return f;
        }
    }
}
