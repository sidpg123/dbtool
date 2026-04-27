using System.Data;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SqlRepoAnalyzer.Rules;

namespace SqlRepoAnalyzer.ShowPlan;

internal sealed record IndexRef(string Schema, string Table, string Index);

internal sealed record IndexColumnDef(
    string Column,
    bool IsIncluded,
    int KeyOrdinal,
    bool IsDescending);

internal sealed record IndexDef(
    string Schema,
    string Table,
    string Name,
    bool IsUnique,
    bool HasFilter,
    string? FilterDefinition,
    IReadOnlyList<IndexColumnDef> Columns);

internal sealed class MssqlIndexMetadataCache
{
    private readonly Dictionary<string, IndexDef> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string schema, string table, string index, out IndexDef def) =>
        _cache.TryGetValue(Key(schema, table, index), out def!);

    public void Put(IndexDef def) => _cache[Key(def.Schema, def.Table, def.Name)] = def;

    private static string Key(string schema, string table, string index) => $"{schema}.{table}.{index}";
}

internal static class PlanMetadataEnricher
{
    private static readonly XNamespace ShowplanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static async Task<IReadOnlyList<Finding>> EnrichIfNeededAsync(
        string connectionString,
        string showPlanXml,
        IReadOnlyList<Finding> findings,
        int commandTimeoutSeconds,
        MssqlIndexMetadataCache cache,
        CancellationToken cancellationToken)
    {
        if (!NeedsMetadata(findings))
            return findings;

        var refs = ExtractIndexRefs(showPlanXml);
        if (refs.Count == 0)
            return findings;

        var defs = new List<IndexDef>();
        foreach (var r in refs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cache.TryGet(r.Schema, r.Table, r.Index, out var cached))
            {
                defs.Add(cached);
                continue;
            }

            var loaded = await TryLoadIndexDefAsync(
                    connectionString,
                    r,
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            if (loaded is null) continue;
            cache.Put(loaded);
            defs.Add(loaded);
        }

        if (defs.Count == 0)
            return findings;

        var evidence = new Dictionary<string, object?>
        {
            ["indexDefinitions"] = defs.Select(d => new Dictionary<string, object?>
            {
                ["schema"] = d.Schema,
                ["table"] = d.Table,
                ["index"] = d.Name,
                ["isUnique"] = d.IsUnique,
                ["hasFilter"] = d.HasFilter,
                ["filterDefinition"] = d.FilterDefinition,
                ["columns"] = d.Columns.Select(c => new Dictionary<string, object?>
                {
                    ["column"] = c.Column,
                    ["isIncluded"] = c.IsIncluded,
                    ["keyOrdinal"] = c.KeyOrdinal,
                    ["isDescending"] = c.IsDescending
                }).ToList()
            }).ToList()
        };

        var extra = new Finding(
            "plan.index_metadata",
            Severity.Info,
            Confidence.High,
            $"Loaded metadata for {defs.Count} index(es) referenced by the plan.",
            Suggestion: null,
            Evidence: evidence);

        // Append a single enrichment finding to keep output stable/compact.
        return findings.Concat(new[] { extra }).ToList();
    }

    private static bool NeedsMetadata(IReadOnlyList<Finding> findings) =>
        findings.Any(f => f.Evidence is not null &&
                          f.Evidence.TryGetValue("needsMetadata", out var v) &&
                          v is bool b &&
                          b);

    private static List<IndexRef> ExtractIndexRefs(string showPlanXml)
    {
        try
        {
            var doc = XDocument.Parse(showPlanXml, LoadOptions.PreserveWhitespace);
            var root = doc.Root;
            if (root is null) return new List<IndexRef>();

            // IndexScan/Object and IndexSeek/Object typically include Schema/Table/Index attributes.
            // We intentionally only collect explicit Index names (skip heaps / unnamed).
            var objects = root.Descendants(ShowplanNs + "Object");
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<IndexRef>();

            foreach (var o in objects)
            {
                var schema = (string?)o.Attribute("Schema");
                var table = (string?)o.Attribute("Table");
                var index = (string?)o.Attribute("Index");

                if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(index))
                    continue;

                schema = string.IsNullOrWhiteSpace(schema) ? "dbo" : schema!;

                // Filter out pseudo-index names sometimes used for heaps.
                if (index.Equals("Heap", StringComparison.OrdinalIgnoreCase))
                    continue;

                var key = $"{schema}.{table}.{index}";
                if (!refs.Add(key)) continue;
                list.Add(new IndexRef(schema, table!, index!));
            }

            return list;
        }
        catch
        {
            return new List<IndexRef>();
        }
    }

    private static async Task<IndexDef?> TryLoadIndexDefAsync(
        string connectionString,
        IndexRef indexRef,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT
  s.name AS schema_name,
  t.name AS table_name,
  i.name AS index_name,
  i.is_unique,
  i.has_filter,
  i.filter_definition,
  ic.key_ordinal,
  ic.is_included_column,
  ic.is_descending_key,
  c.name AS column_name
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE s.name = @schema AND t.name = @table AND i.name = @index
ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id;";

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var cmd = new SqlCommand(sql, conn)
            {
                CommandTimeout = commandTimeoutSeconds
            };
            cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = indexRef.Schema });
            cmd.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = indexRef.Table });
            cmd.Parameters.Add(new SqlParameter("@index", SqlDbType.NVarChar, 128) { Value = indexRef.Index });

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            string? schema = null;
            string? table = null;
            string? name = null;
            bool? isUnique = null;
            bool? hasFilter = null;
            string? filter = null;
            var cols = new List<IndexColumnDef>();

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schema ??= reader.GetString(reader.GetOrdinal("schema_name"));
                table ??= reader.GetString(reader.GetOrdinal("table_name"));
                name ??= reader.GetString(reader.GetOrdinal("index_name"));
                isUnique ??= reader.GetBoolean(reader.GetOrdinal("is_unique"));
                hasFilter ??= reader.GetBoolean(reader.GetOrdinal("has_filter"));
                filter ??= reader.IsDBNull(reader.GetOrdinal("filter_definition"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("filter_definition"));

                var column = reader.GetString(reader.GetOrdinal("column_name"));
                var keyOrdinal = reader.GetInt32(reader.GetOrdinal("key_ordinal"));
                var isIncluded = reader.GetBoolean(reader.GetOrdinal("is_included_column"));
                var isDescending = reader.GetBoolean(reader.GetOrdinal("is_descending_key"));
                cols.Add(new IndexColumnDef(column, isIncluded, keyOrdinal, isDescending));
            }

            if (schema is null || table is null || name is null || isUnique is null || hasFilter is null)
                return null;

            return new IndexDef(
                Schema: schema,
                Table: table,
                Name: name,
                IsUnique: isUnique.Value,
                HasFilter: hasFilter.Value,
                FilterDefinition: filter,
                Columns: cols);
        }
        catch
        {
            return null;
        }
    }
}

