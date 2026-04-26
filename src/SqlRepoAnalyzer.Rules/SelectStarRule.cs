using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

public sealed class SelectStarRule : IRule
{
    public string Id => "sql.select_star";

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        if (ctx.Ast is null) return Array.Empty<Finding>();

        var visitor = new Visitor();
        ctx.Ast.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(SelectStarExpression node)
        {
            Findings.Add(new Finding(
                "sql.select_star",
                Severity.Info,
                Confidence.High,
                "Query selects all columns via `*`.",
                Suggestion: "Prefer explicit column lists to reduce IO and avoid unintended column changes."));
        }
    }
}
