using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: avoid TRUNCATE+reload patterns on large data; prefer incremental changes.
/// </summary>
public sealed class SqlStdTruncateWarnRule : IRule
{
    public string Id => "sql.std.truncate_caution";

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

        public override void ExplicitVisit(TruncateTableStatement node)
        {
            Findings.Add(new Finding(
                "sql.std.truncate_caution",
                Severity.Info,
                Confidence.Medium,
                "TRUNCATE TABLE affects the entire table; avoid truncate-and-reload patterns on large datasets per coding standard.",
                Suggestion: "Prefer incremental upserts/deletes or staged loads where appropriate."));
        }
    }
}
