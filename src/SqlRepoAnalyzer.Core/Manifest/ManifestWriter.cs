using System.Text.Json;

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
            WriteIndented = true
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
    IReadOnlyDictionary<string, object?>? Config = null
);

