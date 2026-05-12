using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: objects and columns use snake_case (letters, numbers, underscore; start with letter).
/// </summary>
public sealed class SqlStdSnakeCaseIdentifierRule : IRule
{
    public string Id => "sql.std.snake_case";

    private static readonly Regex ValidSnake = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        if (ctx.Ast is null || !ctx.Parse!.Success) return Array.Empty<Finding>();

        var visitor = new Visitor();
        ctx.Ast.Accept(visitor);
        return visitor.ToFindings();
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly Dictionary<string, List<(int Line, int Column)>> _schemaObjectIdentifiers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(int Line, int Column)>> _aliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(int Line, int Column)>> _columns = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<Finding> ToFindings()
        {
            var list = new List<Finding>(
                _schemaObjectIdentifiers.Count + _aliases.Count + _columns.Count);

            foreach (var kv in _schemaObjectIdentifiers.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var (name, positions) = (kv.Key, kv.Value);
                list.Add(new Finding(
                    "sql.std.snake_case",
                    Severity.Info,
                    Confidence.Low,
                    $"Identifier `{name}` should be snake_case (lowercase letters, digits, underscore; start with a letter).",
                    Suggestion: "Rename to snake_case per team naming standard.",
                    FindingEvidence.FromPositions(positions)));
            }

            foreach (var kv in _aliases.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var (alias, positions) = (kv.Key, kv.Value);
                list.Add(new Finding(
                    "sql.std.snake_case",
                    Severity.Info,
                    Confidence.Low,
                    $"Table alias `{alias}` should be snake_case.",
                    Suggestion: "Use a short lowercase snake_case alias.",
                    FindingEvidence.FromPositions(positions)));
            }

            foreach (var kv in _columns.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var (col, positions) = (kv.Key, kv.Value);
                list.Add(new Finding(
                    "sql.std.snake_case",
                    Severity.Info,
                    Confidence.Low,
                    $"Column identifier `{col}` should be snake_case.",
                    Suggestion: "Use lowercase snake_case column names.",
                    FindingEvidence.FromPositions(positions)));
            }

            return list;
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            foreach (var id in node.SchemaObject.Identifiers)
            {
                var v = id?.Value;
                if (string.IsNullOrEmpty(v) || v.StartsWith("#", StringComparison.Ordinal) || v.StartsWith("@", StringComparison.Ordinal))
                    continue;
                if (v.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (id is not null && !ValidSnake.IsMatch(v))
                    Bump(_schemaObjectIdentifiers, v, FindingEvidence.Position(id));
            }

            if (node.Alias is { } aliasId && !string.IsNullOrEmpty(aliasId.Value) && !ValidSnake.IsMatch(aliasId.Value))
                Bump(_aliases, aliasId.Value, FindingEvidence.Position(aliasId));

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids)
            {
                var colId = ids[^1];
                var col = colId.Value;
                if (!string.IsNullOrEmpty(col) && !col.StartsWith("@", StringComparison.Ordinal) && !ValidSnake.IsMatch(col))
                    Bump(_columns, col, FindingEvidence.Position(colId));
            }

            base.ExplicitVisit(node);
        }

        private static void Bump(Dictionary<string, List<(int Line, int Column)>> counts, string key, (int Line, int Column) pos)
        {
            if (!counts.TryGetValue(key, out var list))
            {
                list = new List<(int Line, int Column)>();
                counts[key] = list;
            }

            list.Add(pos);
        }
    }
}
