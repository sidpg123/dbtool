using System.Text.Json.Serialization;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Rules;

namespace SqlRepoAnalyzer.Suggestions;

public sealed class SuggestionRecord
{
    [JsonPropertyName("queryId")]
    public required string QueryId { get; init; }

    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    [JsonPropertyName("sourceKind")]
    public SourceKind SourceKind { get; init; }

    [JsonPropertyName("completeness")]
    public string? Completeness { get; init; }

    [JsonPropertyName("analysisStatus")]
    public string AnalysisStatus { get; init; } = "analyzed";

    [JsonPropertyName("analysisWarning")]
    public string? AnalysisWarning { get; init; }

    [JsonPropertyName("parseOk")]
    public bool? ParseOk { get; init; }

    [JsonPropertyName("parseErrors")]
    public IReadOnlyList<string>? ParseErrors { get; init; }

    [JsonPropertyName("findings")]
    public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();
}
