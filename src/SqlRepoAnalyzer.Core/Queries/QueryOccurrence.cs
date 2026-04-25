namespace SqlRepoAnalyzer.Core.Queries;

public sealed record QueryOccurrence(
    string FilePath,
    int StartLine,
    int StartCol,
    int EndLine,
    int EndCol
);

