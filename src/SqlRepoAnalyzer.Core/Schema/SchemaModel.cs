namespace SqlRepoAnalyzer.Core.Schema;

public sealed class SchemaModel
{
    private readonly Dictionary<string, SchemaTable> _tables;

    public SchemaModel(SchemaSnapshot snapshot)
    {
        _tables = new Dictionary<string, SchemaTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in snapshot.Tables)
        {
            var key = TableKey(t.Schema, t.Name);
            _tables[key] = t;
        }
    }

    public bool TryGetTable(string? schema, string name, out SchemaTable table)
    {
        schema = string.IsNullOrWhiteSpace(schema) ? "dbo" : schema!;
        var key = TableKey(schema, name);
        return _tables.TryGetValue(key, out table!);
    }

    public bool HasColumn(string? schema, string table, string column)
    {
        if (!TryGetTable(schema, table, out var t)) return false;
        return t.Columns.Any(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
    }

    private static string TableKey(string schema, string name) => $"{schema}.{name}";
}
