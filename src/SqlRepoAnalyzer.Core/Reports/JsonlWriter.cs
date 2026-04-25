using System.Text.Json;

namespace SqlRepoAnalyzer.Core.Reports;

public static class JsonlWriter
{
    public static void WriteJsonLines<T>(string path, IEnumerable<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var sw = new StreamWriter(fs);
        foreach (var item in items)
        {
            var json = JsonSerializer.Serialize(item, new JsonSerializerOptions
            {
                WriteIndented = false
            });
            sw.WriteLine(json);
        }
    }
}

