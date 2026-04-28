namespace SqlRepoAnalyzer.Rules;

public static class RulesRegistry
{
    public static IReadOnlyList<IRule> DefaultRules { get; } = new IRule[]
    {
        new TsqlParseErrorRule(),
        new SelectStarRule(),
        new LeadingWildcardLikeRule(),
        new SqlStdMergeProhibitedRule(),
        new SqlStdCursorAvoidRule(),
        new SqlStdTruncateWarnRule(),
        new SqlStdNonSargablePredicateRule(),
        new SqlStdSchemaQualifiedObjectRule(),
        new SqlStdJoinRequiresAliasRule(),
        new SqlStdColumnAliasQualifiedRule(),
        new SqlStdBracketQuotedIdentifiersRule(),
        new SqlStdSnakeCaseIdentifierRule(),
        new SqlStdIndentTabsRule(),
        new SqlStdKeywordUppercaseRule(),
        new SqlStdXactAbortRule(),
        new SqlStdSelectColumnSeparateLineRule(),
        new SqlStdSelectModifierSameLineRule(),
        new SqlStdPredicateSeparateLineRule(),
        new SqlStdPreferCteOverNestedQueryRule(),
        new SqlStdPreferTempTableOverTableVariableRule(),
        new SqlStdObjectPrefixConventionRule(),
        new SqlStdNamedConstraintRequiredRule(),
        new SqlStdComplexJoinCommentRule()
    };
}
