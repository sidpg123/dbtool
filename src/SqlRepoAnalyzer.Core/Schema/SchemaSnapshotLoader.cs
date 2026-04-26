using System.Text.Json;

namespace SqlRepoAnalyzer.Core.Schema;

public static class SchemaSnapshotLoader
{
    public static SchemaSnapshot Load(string path)
    {
        var json = File.ReadAllText(path);
        var snapshot = JsonSerializer.Deserialize<SchemaSnapshot>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (snapshot is null)
            throw new InvalidOperationException($"Failed to deserialize schema snapshot: {path}");

        return snapshot;
    }
}
