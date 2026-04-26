namespace SqlRepoAnalyzer.Rules;

public static class RulesRegistry
{
    public static IReadOnlyList<IRule> DefaultRules { get; } = new IRule[]
    {
        new TsqlParseErrorRule(),
        new SelectStarRule(),
        new LeadingWildcardLikeRule(),
        new UnknownTableReferenceRule()
    };
}
