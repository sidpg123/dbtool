using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

public sealed class LeadingWildcardLikeRule : IRule
{
    public string Id => "sql.like_leading_wildcard";

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

        public override void ExplicitVisit(LikePredicate node)
        {
            if (node.SecondExpression is StringLiteral lit)
            {
                var v = lit.Value ?? "";
                if (v.StartsWith("%", StringComparison.Ordinal) || v.StartsWith("_", StringComparison.Ordinal))
                {
                    Findings.Add(new Finding(
                        "sql.like_leading_wildcard",
                        Severity.Warn,
                        Confidence.Medium,
                        "LIKE pattern starts with a wildcard, which is often non-sargable.",
                        Suggestion: "Avoid leading `%`/`_` when possible; consider full-text search or redesigned filtering."));
                }
            }
        }
    }
}
