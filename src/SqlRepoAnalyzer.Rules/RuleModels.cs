namespace SqlRepoAnalyzer.Rules;

public enum Severity
{
    Info = 1,
    Warn = 2,
    Error = 3
}

public enum Confidence
{
    Low = 1,
    Medium = 2,
    High = 3
}

public sealed record Finding(
    string RuleId,
    Severity Severity,
    Confidence Confidence,
    string Message,
    string? Suggestion = null,
    IReadOnlyDictionary<string, object?>? Evidence = null
);
