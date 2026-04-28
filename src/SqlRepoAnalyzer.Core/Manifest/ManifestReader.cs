using System.Text.Json;

namespace SqlRepoAnalyzer.Core.Manifest;

public static class ManifestReader
{
    /// <summary>
    /// Reads <c>backend</c> from an existing manifest without deserializing the full record.
    /// </summary>
    public static string? TryReadBackend(string manifestPath)
    {
        if (!File.Exists(manifestPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("backend", out var b) && b.ValueKind == JsonValueKind.String)
                return b.GetString();
        }
        catch
        {
            // ignore corrupt / partial manifest
        }

        return null;
    }
}
