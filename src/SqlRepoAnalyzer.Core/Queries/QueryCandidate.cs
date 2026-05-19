namespace SqlRepoAnalyzer.Core.Queries;

public sealed record QueryCandidate(
    SourceKind SourceKind,
    string FilePath,
    int StartLine,
    int StartCol,
    int EndLine,
    int EndCol,
    string? SqlText,
    string? Completeness = null,
    string? ParameterBindingsJson = null);
