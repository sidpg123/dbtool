using System.Text.Json.Serialization;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Rules;

namespace SqlRepoAnalyzer.ShowPlan;

public sealed class PlanRecord
{
    [JsonPropertyName("queryId")]
    public required string QueryId { get; init; }

    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    [JsonPropertyName("sourceKind")]
    public SourceKind SourceKind { get; init; }

    [JsonPropertyName("completeness")]
    public string? Completeness { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("skipReason")]
    public string? SkipReason { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("planXmlRelativePath")]
    public string? PlanXmlRelativePath { get; init; }

    [JsonPropertyName("findings")]
    public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();
}
