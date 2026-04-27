namespace SqlRepoAnalyzer.ShowPlan;

public sealed record PlanRunOptions(
    string QueriesPath,
    string OutDir,
    string? ConnectionString,
    int CommandTimeoutSeconds,
    int MaxQueries,
    bool EnableShowplanAcknowledged,
    bool AllowDml,
    bool DryRun);
