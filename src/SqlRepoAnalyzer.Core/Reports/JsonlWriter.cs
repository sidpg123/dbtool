using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlRepoAnalyzer.Core.Reports;

public static class JsonlWriter
{
    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
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
            var json = JsonSerializer.Serialize(item, JsonlOptions);
            sw.WriteLine(json);
        }
    }

    public static void WritePrettyJsonArray<T>(string path, IEnumerable<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(items, PrettyOptions);
        File.WriteAllText(path, json);
    }

    public static void WriteJsonArray<T>(string path, IEnumerable<T> items) =>
        WritePrettyJsonArray(path, items);

    public static void WriteJsonObject<T>(string path, T item)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(item, PrettyOptions);
        File.WriteAllText(path, json);
    }
}

