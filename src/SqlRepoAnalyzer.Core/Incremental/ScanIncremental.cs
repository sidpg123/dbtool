using System.Text.Json;
using System.Text.Json.Serialization;
using SqlRepoAnalyzer.Core.Queries;

namespace SqlRepoAnalyzer.Core.Incremental;

/// <summary>
/// Persists queryId → fingerprint after each <c>scan</c> so the next scan can emit <c>queries.incremental.json</c>.
/// </summary>
public sealed class ScanState
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("lastScanAtUtc")]
    public required string LastScanAtUtc { get; init; }

    /// <summary>Snapshot of <see cref="QueryRecord.QueryId"/> → <see cref="QueryRecord.Fingerprint"/> from the last completed scan.</summary>
    [JsonPropertyName("fingerprintsByQueryId")]
    public Dictionary<string, string> FingerprintsByQueryId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ScanIncremental
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static ScanState? TryReadScanState(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<ScanState>(json, JsonOptions);
            if (parsed is null) return null;
            parsed.FingerprintsByQueryId = new Dictionary<string, string>(
                parsed.FingerprintsByQueryId ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    public static void WriteScanState(string path, ScanState state)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }

    /// <summary>
    /// Compares <paramref name="current"/> to the previous scan baseline. When there is no baseline, every query is treated as incremental.
    /// </summary>
    public static (IReadOnlyList<QueryRecord> IncrementalQueries, ScanState NewState) ComputeIncremental(
        IReadOnlyList<QueryRecord> current,
        ScanState? previous)
    {
        var baseline = previous?.FingerprintsByQueryId;
        var incremental = new List<QueryRecord>();
        if (baseline is null || baseline.Count == 0)
        {
            incremental.AddRange(current);
        }
        else
        {
            foreach (var q in current)
            {
                if (!baseline.TryGetValue(q.QueryId, out var oldFp) ||
                    !string.Equals(oldFp, q.Fingerprint, StringComparison.Ordinal))
                {
                    incremental.Add(q);
                }
            }
        }

        var newState = new ScanState
        {
            LastScanAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            FingerprintsByQueryId = current.ToDictionary(q => q.QueryId, q => q.Fingerprint, StringComparer.OrdinalIgnoreCase)
        };

        return (incremental, newState);
    }
}
