using System.Text.RegularExpressions;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: objects/columns should be bracket-quoted.
/// </summary>
public sealed class SqlStdBracketQuotedIdentifiersRule : IRule
{
    public string Id => "sql.std.bracket_quoted_identifiers";

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        if (ctx.Ast is null || !ctx.Parse!.Success) return Array.Empty<Finding>();
        var v = new Visitor();
        ctx.Ast.Accept(v);
        return v.Findings;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(NamedTableReference node)
        {
            foreach (var id in node.SchemaObject.Identifiers)
            {
                if (id is null || string.IsNullOrWhiteSpace(id.Value)) continue;
                if (id.Value.StartsWith("#", StringComparison.Ordinal)) continue;
                if (id.QuoteType != QuoteType.SquareBracket)
                {
                    Findings.Add(new Finding(
                        "sql.std.bracket_quoted_identifiers",
                        Severity.Info,
                        Confidence.Low,
                        $"Identifier `{id.Value}` should be bracket-quoted (`[{id.Value}]`) per coding standard."));
                    break;
                }
            }

            base.ExplicitVisit(node);
        }
    }
}

/// <summary>
/// Coding standard: in multi-table queries, selected columns should use alias-qualified two-part form (alias.column).
/// </summary>
public sealed class SqlStdColumnAliasQualifiedRule : IRule
{
    public string Id => "sql.std.column_alias_qualified";

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        if (ctx.Ast is null || !ctx.Parse!.Success) return Array.Empty<Finding>();
        var v = new Visitor();
        ctx.Ast.Accept(v);
        return v.Findings;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(QuerySpecification node)
        {
            var tableCount = CountNamedTables(node.FromClause?.TableReferences);
            if (tableCount < 2)
            {
                base.ExplicitVisit(node);
                return;
            }

            foreach (var se in node.SelectElements.OfType<SelectScalarExpression>())
            {
                var colVisitor = new ColumnVisitor();
                se.Expression?.Accept(colVisitor);
                if (colVisitor.HasUnqualifiedColumn)
                {
                    Findings.Add(new Finding(
                        "sql.std.column_alias_qualified",
                        Severity.Info,
                        Confidence.Medium,
                        "Multi-table query has selected columns without alias qualification.",
                        Suggestion: "Use `alias.column` for selected columns in multi-table queries."));
                    break;
                }
            }

            base.ExplicitVisit(node);
        }

        private static int CountNamedTables(IList<TableReference>? refs)
        {
            if (refs is null) return 0;
            var acc = new List<NamedTableReference>();
            foreach (var tr in refs) CollectNamed(tr, acc);
            return acc.Count;
        }

        private static void CollectNamed(TableReference? tr, List<NamedTableReference> acc)
        {
            switch (tr)
            {
                case NamedTableReference n:
                    acc.Add(n);
                    return;
                case QualifiedJoin j:
                    CollectNamed(j.FirstTableReference, acc);
                    CollectNamed(j.SecondTableReference, acc);
                    return;
                case JoinParenthesisTableReference p:
                    CollectNamed(p.Join, acc);
                    return;
            }
        }

        private sealed class ColumnVisitor : TSqlFragmentVisitor
        {
            public bool HasUnqualifiedColumn { get; private set; }

            public override void ExplicitVisit(ColumnReferenceExpression node)
            {
                var ids = node.MultiPartIdentifier?.Identifiers;
                if (ids is { Count: 1 })
                {
                    var name = ids[0].Value;
                    if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("@", StringComparison.Ordinal))
                        HasUnqualifiedColumn = true;
                }
                base.ExplicitVisit(node);
            }
        }
    }
}

/// <summary>
/// Coding standard: each selected column should be on a separate line.
/// </summary>
public sealed class SqlStdSelectColumnSeparateLineRule : IRule
{
    public string Id => "sql.std.select_column_separate_line";

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        if (ctx.Ast is null || !ctx.Parse!.Success) return Array.Empty<Finding>();
        var v = new Visitor();
        ctx.Ast.Accept(v);
        return v.Findings;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(QuerySpecification node)
        {
            var lines = new HashSet<int>();
            foreach (var e in node.SelectElements)
            {
                if (!lines.Add(e.StartLine))
                {
                    Findings.Add(new Finding(
                        "sql.std.select_column_separate_line",
                        Severity.Info,
                        Confidence.Medium,
                        "Multiple selected columns appear on the same line.",
                        Suggestion: "Put each selected column/expression on its own line."));
                    break;
                }
            }

            base.ExplicitVisit(node);
        }
    }
}

/// <summary>
/// Coding standard: SELECT modifiers (TOP, DISTINCT) should be on the same line as SELECT.
/// </summary>
public sealed class SqlStdSelectModifierSameLineRule : IRule
{
    public string Id => "sql.std.select_modifier_same_line";

    private static readonly Regex MultiLineSelectModifier =
        new(@"(?im)^\s*SELECT\s*$\s*(DISTINCT|TOP\b)", RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        if (!MultiLineSelectModifier.IsMatch(sql))
            return Array.Empty<Finding>();

        return new[]
        {
            new Finding(
                Id,
                Severity.Info,
                Confidence.Low,
                "SELECT modifiers (for example TOP/DISTINCT) should be on the same line as SELECT.",
                Suggestion: "Write `SELECT DISTINCT ...` or `SELECT TOP (n) ...` on one line.")
        };
    }
}

/// <summary>
/// Coding standard: AND/OR predicates should be split across lines for readability.
/// </summary>
public sealed class SqlStdPredicateSeparateLineRule : IRule
{
    public string Id => "sql.std.predicate_separate_line";

    private static readonly Regex InLinePredicateConnectors =
        new(@"(?im)^\s*(WHERE|ON|HAVING)\b.+\b(AND|OR)\b.+$", RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        if (!InLinePredicateConnectors.IsMatch(sql))
            return Array.Empty<Finding>();

        return new[]
        {
            new Finding(
                Id,
                Severity.Info,
                Confidence.Low,
                "Predicates appear inline with AND/OR on the same line.",
                Suggestion: "Place each predicate on a separate line and align AND/OR blocks.")
        };
    }
}

/// <summary>
/// Coding standard: prefer CTEs over nested subqueries.
/// </summary>
public sealed class SqlStdPreferCteOverNestedQueryRule : IRule
{
    public string Id => "sql.std.prefer_cte_over_nested_query";

    private static readonly Regex DerivedSubqueryPattern =
        new(@"(?is)\bFROM\s*\(\s*SELECT\b", RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        if (!DerivedSubqueryPattern.IsMatch(sql))
            return Array.Empty<Finding>();

        return new[]
        {
            new Finding(
                Id,
                Severity.Info,
                Confidence.Low,
                "Nested subquery pattern detected; coding standard prefers CTEs for readability.",
                Suggestion: "Refactor nested subqueries into CTEs when practical.")
        };
    }
}

/// <summary>
/// Coding standard: prefer temp tables over table variables.
/// </summary>
public sealed class SqlStdPreferTempTableOverTableVariableRule : IRule
{
    public string Id => "sql.std.prefer_temp_table_over_table_variable";

    private static readonly Regex TableVariablePattern =
        new(@"(?im)\bDECLARE\s+@\w+\s+TABLE\b", RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        if (!TableVariablePattern.IsMatch(sql))
            return Array.Empty<Finding>();

        return new[]
        {
            new Finding(
                Id,
                Severity.Info,
                Confidence.Medium,
                "Table variable declaration detected.",
                Suggestion: "Prefer temporary tables (`#temp`) over table variables for larger workloads.")
        };
    }
}

/// <summary>
/// Coding standard: object type prefixes for DDL names (sp_, vw_, fn_, tvf_).
/// </summary>
public sealed class SqlStdObjectPrefixConventionRule : IRule
{
    public string Id => "sql.std.object_prefix_convention";

    private static readonly Regex CreateProc = new(@"(?im)\bCREATE\s+(OR\s+ALTER\s+)?(PROC|PROCEDURE)\s+([^\s(]+)", RegexOptions.Compiled);
    private static readonly Regex CreateView = new(@"(?im)\bCREATE\s+(OR\s+ALTER\s+)?VIEW\s+([^\s(]+)", RegexOptions.Compiled);
    private static readonly Regex CreateFn = new(@"(?im)\bCREATE\s+(OR\s+ALTER\s+)?FUNCTION\s+([^\s(]+)", RegexOptions.Compiled);
    private static readonly Regex ReturnsTable = new(@"(?im)\bRETURNS\s+TABLE\b", RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        var findings = new List<Finding>();

        foreach (Match m in CreateProc.Matches(sql))
        {
            var name = LastIdentifier(m.Groups[3].Value);
            if (!name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase))
                findings.Add(PrefixFinding("stored procedure", name, "sp_"));
        }

        foreach (Match m in CreateView.Matches(sql))
        {
            var name = LastIdentifier(m.Groups[2].Value);
            if (!name.StartsWith("vw_", StringComparison.OrdinalIgnoreCase))
                findings.Add(PrefixFinding("view", name, "vw_"));
        }

        foreach (Match m in CreateFn.Matches(sql))
        {
            var name = LastIdentifier(m.Groups[2].Value);
            var expected = ReturnsTable.IsMatch(sql) ? "tvf_" : "fn_";
            if (!name.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                findings.Add(PrefixFinding("function", name, expected));
        }

        return findings;
    }

    private static Finding PrefixFinding(string kind, string name, string expected) =>
        new(
            "sql.std.object_prefix_convention",
            Severity.Info,
            Confidence.Low,
            $"DDL {kind} `{name}` does not follow expected prefix `{expected}`.",
            Suggestion: $"Rename to start with `{expected}` per coding standard.");

    private static string LastIdentifier(string token)
    {
        var t = token.Trim();
        var dot = t.LastIndexOf('.');
        if (dot >= 0) t = t[(dot + 1)..];
        return t.Trim('[', ']');
    }
}

/// <summary>
/// Coding standard: constraints should have explicit names.
/// </summary>
public sealed class SqlStdNamedConstraintRequiredRule : IRule
{
    public string Id => "sql.std.named_constraint_required";

    private static readonly Regex UnnamedConstraintPattern =
        new(@"(?is)\bCREATE\s+TABLE\b(?:(?!\bCONSTRAINT\b).)*\b(PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE|DEFAULT)\b", RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        if (!UnnamedConstraintPattern.IsMatch(sql))
            return Array.Empty<Finding>();

        return new[]
        {
            new Finding(
                Id,
                Severity.Info,
                Confidence.Low,
                "Potential unnamed constraint detected in CREATE TABLE statement.",
                Suggestion: "Declare constraints explicitly with `CONSTRAINT <name> ...`.")
        };
    }
}

/// <summary>
/// Coding standard: complex joins should include intent comments.
/// </summary>
public sealed class SqlStdComplexJoinCommentRule : IRule
{
    public string Id => "sql.std.complex_join_comment";

    private static readonly Regex JoinToken = new(@"\bJOIN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommentToken = new(@"(--|/\*)", RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        var joins = JoinToken.Matches(sql).Count;
        if (joins < 4) return Array.Empty<Finding>(); // 5-table style query ~= 4+ JOIN tokens
        if (CommentToken.IsMatch(sql)) return Array.Empty<Finding>();

        return new[]
        {
            new Finding(
                Id,
                Severity.Info,
                Confidence.Low,
                "Complex multi-join query found without comments.",
                Suggestion: "Add comments that explain business intent for complex joins.")
        };
    }
}

