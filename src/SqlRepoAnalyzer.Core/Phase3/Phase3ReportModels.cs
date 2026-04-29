using System.Text.Json.Serialization;

namespace SqlRepoAnalyzer.Core.Phase3;

public sealed class Phase3PlansReport
{
    [JsonPropertyName("generatedAtUtc")]
    public required string GeneratedAtUtc { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("connectionSummary")]
    public required string ConnectionSummary { get; init; }

    [JsonPropertyName("queryCount")]
    public int QueryCount { get; init; }

    [JsonPropertyName("queryFingerprints")]
    public IReadOnlyList<Phase3QueryFingerprint> QueryFingerprints { get; init; } = Array.Empty<Phase3QueryFingerprint>();

    [JsonPropertyName("startedAtUtc")]
    public required string StartedAtUtc { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("totalRules")]
    public int TotalRules { get; init; }

    [JsonPropertyName("totalFindings")]
    public int TotalFindings { get; init; }

    [JsonPropertyName("findings")]
    public IReadOnlyList<Phase3RuleFinding> Findings { get; init; } = Array.Empty<Phase3RuleFinding>();

    [JsonPropertyName("byRule")]
    public IReadOnlyList<Phase3RuleSummary> ByRule { get; init; } = Array.Empty<Phase3RuleSummary>();
}

public sealed class Phase3QueryFingerprint
{
    [JsonPropertyName("queryId")]
    public required string QueryId { get; init; }

    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }
}

public sealed class Phase3RuleFinding
{
    [JsonPropertyName("ruleId")]
    public required string RuleId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; } // pass|warn|fail|error

    [JsonPropertyName("severity")]
    public required string Severity { get; init; } // info|warn|error

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("recommendation")]
    public string? Recommendation { get; init; }

    [JsonPropertyName("affectedObjects")]
    public IReadOnlyList<string> AffectedObjects { get; init; } = Array.Empty<string>();

    [JsonPropertyName("queryIds")]
    public IReadOnlyList<string> QueryIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("evidence")]
    public IReadOnlyDictionary<string, object?>? Evidence { get; init; }
}

public sealed class Phase3RuleSummary
{
    [JsonPropertyName("ruleId")]
    public required string RuleId { get; init; }

    [JsonPropertyName("pass")]
    public int Pass { get; init; }

    [JsonPropertyName("warn")]
    public int Warn { get; init; }

    [JsonPropertyName("fail")]
    public int Fail { get; init; }

    [JsonPropertyName("error")]
    public int Error { get; init; }
}

