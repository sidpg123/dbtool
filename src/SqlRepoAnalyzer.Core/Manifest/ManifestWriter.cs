using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlRepoAnalyzer.Core.Manifest;

public static class ManifestWriter
{
    public static void WriteManifest(
        string manifestPath,
        ManifestRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var json = JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        File.WriteAllText(manifestPath, json);
    }
}

public sealed record ManifestRecord(
    int ReportSchemaVersion,
    string ToolVersion,
    string GeneratedAtUtc,
    string RepoRoot,
    string OutDir,
    string? GitSha,
    string? RulesVersion = null,
    string? SchemaFingerprint = null,
    /// <summary>
    /// Primary backend stack for extraction/heuristics: <c>csharp</c>, <c>node</c>, or <c>mixed</c>.
    /// Set by <c>scan --backend</c>; preserved when later commands overwrite the manifest.
    /// </summary>
    string? Backend = null,
    IReadOnlyDictionary<string, object?>? Config = null
);

