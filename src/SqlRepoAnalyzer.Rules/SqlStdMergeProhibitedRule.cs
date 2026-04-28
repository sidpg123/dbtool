using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: MERGE is prohibited; use separate INSERT/UPDATE/DELETE.
/// </summary>
public sealed class SqlStdMergeProhibitedRule : IRule
{
    public string Id => "sql.std.merge_prohibited";

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

        public override void ExplicitVisit(MergeStatement node)
        {
            Findings.Add(new Finding(
                "sql.std.merge_prohibited",
                Severity.Warn,
                Confidence.High,
                "MERGE statement is prohibited by coding standard (prefer separate INSERT, UPDATE, DELETE).",
                Suggestion: "Rewrite using discrete DML statements with explicit transaction logic."));
        }
    }
}
