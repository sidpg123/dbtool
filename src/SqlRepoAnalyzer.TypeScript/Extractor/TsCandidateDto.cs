namespace SqlRepoAnalyzer.TypeScript.Extractor;

public sealed record TsCandidateDto(
    string SourceKind,
    string File,
    int StartLine,
    int StartCol,
    int EndLine,
    int EndCol,
    string? SqlText,
    string? Completeness,
    string? ParameterBindingsJson = null);

