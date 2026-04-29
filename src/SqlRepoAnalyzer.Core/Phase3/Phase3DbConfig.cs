using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlRepoAnalyzer.Core.Phase3;

public sealed class Phase3DbConfig
{
    [JsonPropertyName("defaultEnvironment")]
    public string? DefaultEnvironment { get; init; }

    [JsonPropertyName("environments")]
    public Dictionary<string, Phase3DbEnvironment> Environments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class Phase3DbEnvironment
{
    [JsonPropertyName("connectionString")]
    public string? ConnectionString { get; init; }
}

public static class Phase3DbConfigLoader
{
    public static Phase3DbConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<Phase3DbConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        if (cfg is null)
            throw new InvalidOperationException($"Failed to deserialize DB config JSON: {path}");
        return cfg;
    }

    public static (string EnvironmentName, string ConnectionString) ResolveConnection(Phase3DbConfig config, string? requestedEnvironment)
    {
        var env = string.IsNullOrWhiteSpace(requestedEnvironment)
            ? config.DefaultEnvironment
            : requestedEnvironment;

        if (string.IsNullOrWhiteSpace(env))
            throw new InvalidOperationException("No environment selected. Provide --env or set defaultEnvironment in DB config.");

        if (!config.Environments.TryGetValue(env, out var e))
            throw new InvalidOperationException($"Environment '{env}' was not found in DB config.");

        if (string.IsNullOrWhiteSpace(e.ConnectionString))
            throw new InvalidOperationException($"Environment '{env}' has an empty connectionString.");

        return (env, e.ConnectionString);
    }
}

