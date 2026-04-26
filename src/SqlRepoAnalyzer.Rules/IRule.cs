namespace SqlRepoAnalyzer.Rules;

public interface IRule
{
    string Id { get; }
    IReadOnlyList<Finding> Evaluate(RuleContext ctx);
}
