using System.Text.Json.Serialization;

namespace SqlRepoAnalyzer.Core.Schema;

public sealed class SchemaSnapshot
{
    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    [JsonPropertyName("database")]
    public string? Database { get; init; }

    [JsonPropertyName("capturedAtUtc")]
    public string? CapturedAtUtc { get; init; }

    [JsonPropertyName("tables")]
    public List<SchemaTable> Tables { get; init; } = new();
}

public sealed class SchemaTable
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = "dbo";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("columns")]
    public List<SchemaColumn> Columns { get; init; } = new();

    [JsonPropertyName("indexes")]
    public List<SchemaIndex> Indexes { get; init; } = new();
}

public sealed class SchemaColumn
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("nullable")]
    public bool? Nullable { get; init; }
}

public sealed class SchemaIndex
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("isUnique")]
    public bool? IsUnique { get; init; }

    [JsonPropertyName("keys")]
    public List<SchemaIndexKey> Keys { get; init; } = new();

    [JsonPropertyName("includes")]
    public List<string> Includes { get; init; } = new();
}

public sealed class SchemaIndexKey
{
    [JsonPropertyName("column")]
    public string Column { get; init; } = "";

    [JsonPropertyName("descending")]
    public bool? Descending { get; init; }
}
