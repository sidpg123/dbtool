namespace SqlRepoAnalyzer.Core.Queries;

public sealed record QueryRecord(
    string QueryId,
    string Fingerprint,
    string? SqlText,
    SourceKind SourceKind,
    string? Completeness,
    IReadOnlyList<QueryOccurrence> Occurrences,
    string? ParameterBindingsJson = null);
