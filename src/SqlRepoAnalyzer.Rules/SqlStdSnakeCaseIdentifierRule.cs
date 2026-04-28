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
        return visitor.Findings;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(NamedTableReference node)
        {
            foreach (var id in node.SchemaObject.Identifiers)
            {
                var v = id?.Value;
                if (string.IsNullOrEmpty(v) || v.StartsWith("#", StringComparison.Ordinal) || v.StartsWith("@", StringComparison.Ordinal))
                    continue;
                if (v.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!ValidSnake.IsMatch(v))
                {
                    Findings.Add(new Finding(
                        "sql.std.snake_case",
                        Severity.Info,
                        Confidence.Low,
                        $"Identifier `{v}` should be snake_case (lowercase letters, digits, underscore; start with a letter).",
                        Suggestion: "Rename to snake_case per team naming standard."));
                }
            }

            if (node.Alias?.Value is { } alias && !ValidSnake.IsMatch(alias))
            {
                Findings.Add(new Finding(
                    "sql.std.snake_case",
                    Severity.Info,
                    Confidence.Low,
                    $"Table alias `{alias}` should be snake_case.",
                    Suggestion: "Use a short lowercase snake_case alias."));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids)
            {
                var col = ids[^1].Value;
                if (!string.IsNullOrEmpty(col) && !col.StartsWith("@", StringComparison.Ordinal) && !ValidSnake.IsMatch(col))
                {
                    Findings.Add(new Finding(
                        "sql.std.snake_case",
                        Severity.Info,
                        Confidence.Low,
                        $"Column identifier `{col}` should be snake_case.",
                        Suggestion: "Use lowercase snake_case column names."));
                }
            }

            base.ExplicitVisit(node);
        }
    }
}
