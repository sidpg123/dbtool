namespace SqlRepoAnalyzer.Rules;

public static class RulesRegistry
{
    public static IReadOnlyList<IRule> DefaultRules { get; } = new IRule[]
    {
        new TsqlParseErrorRule(),
        new SelectStarRule(),
        new LeadingWildcardLikeRule(),
        new UnknownTableReferenceRule(),
        new SqlStdMergeProhibitedRule(),
        new SqlStdCursorAvoidRule(),
        new SqlStdTruncateWarnRule(),
        new SqlStdNonSargablePredicateRule(),
        new SqlStdSchemaQualifiedObjectRule(),
        new SqlStdJoinRequiresAliasRule(),
        new SqlStdSnakeCaseIdentifierRule(),
        new SqlStdIndentTabsRule(),
        new SqlStdKeywordUppercaseRule(),
        new SqlStdXactAbortRule()
    };
}
