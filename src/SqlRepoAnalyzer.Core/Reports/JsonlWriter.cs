using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlRepoAnalyzer.Core.Reports;

public static class JsonlWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void WriteJsonLines<T>(string path, IEnumerable<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var sw = new StreamWriter(fs);
        foreach (var item in items)
        {
            var json = JsonSerializer.Serialize(item, Options);
            sw.WriteLine(json);
        }
    }
}

