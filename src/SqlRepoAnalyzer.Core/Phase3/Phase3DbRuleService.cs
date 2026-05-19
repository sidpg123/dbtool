using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlRepoAnalyzer.Core.Logging;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Core.Tsql;

namespace SqlRepoAnalyzer.Core.Phase3;

public static partial class Phase3DbRuleService
{
    private static readonly string[] RuleIds =
    {
        "schema.unknown_table",
        "db.covering_index",
        "db.index_suitability",
        "db.minimal_dataset_extraction",
        "db.heavy_trigger_impact",
        "db.implicit_conversion_risk",
        "db.parameter_type_mismatch",
        "db.stats_freshness",
        "db.redundant_indexes",
        "db.unused_indexes",
        "db.fk_missing_index"
    };

    public static async Task<Phase3PlansReport> RunAsync(
        string environment,
        string connectionString,
        IReadOnlyList<QueryRecord> queries,
        Logger log,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var findings = new List<Phase3RuleFinding>();
        var queryFingerprints = queries
            .Where(q => !string.IsNullOrWhiteSpace(q.QueryId) && !string.IsNullOrWhiteSpace(q.Fingerprint))
            .Select(q => new Phase3QueryFingerprint
            {
                QueryId = q.QueryId,
                Fingerprint = q.Fingerprint!
            })
            .OrderBy(q => q.QueryId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var queryFingerprintById = queryFingerprints.ToDictionary(q => q.QueryId, q => q.Fingerprint, StringComparer.OrdinalIgnoreCase);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var connSummary = DescribeConnection(connectionString);
        log.Info("Phase3 DB connection opened", new Dictionary<string, object?>
        {
            ["environment"] = environment,
            ["connection"] = connSummary
        });

        var tableRefsByName = CollectReferencedTables(queries);
        var allQueryIds = queries
            .Where(q => !string.IsNullOrWhiteSpace(q.QueryId))
            .Select(q => q.QueryId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scopedTables = await ResolveReferencedTablesAsync(conn, tableRefsByName.Keys, ct).ConfigureAwait(false);
        var existingTables = scopedTables.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var objectIds = scopedTables.Values.Distinct().ToArray();
        var objectIdToQualified = await LoadObjectIdToQualifiedNamesAsync(conn, objectIds, ct).ConfigureAwait(false);
        var rowCounts = await LoadTableRowCountsAsync(conn, objectIds, ct).ConfigureAwait(false);
        var tableIndexInfo = await LoadTableIndexInfoAsync(conn, objectIds, ct).ConfigureAwait(false);
        var indexCatalog = await LoadIndexCatalogAsync(conn, objectIds, ct).ConfigureAwait(false);
        var missingIndexes = await LoadMissingIndexHintsAsync(conn, objectIds, ct).ConfigureAwait(false);
        var triggers = await LoadTriggerDefinitionsAsync(conn, objectIds, ct).ConfigureAwait(false);
        ApplyLogicalRefCatalogAliases(scopedTables, objectIdToQualified, rowCounts, tableIndexInfo, indexCatalog);
        var logicalRefToCatalogName = BuildLogicalRefToCatalogQualifiedNameMap(scopedTables, objectIdToQualified);
        var queryIndexRequirements = BuildQueryIndexRequirements(queries, tableRefsByName, queryFingerprintById);

        findings.AddRange(CheckUnknownTables(tableRefsByName, existingTables, queryFingerprintById, allQueryIds));
        findings.AddRange(CheckCoveringIndex(tableRefsByName, tableIndexInfo, existingTables, allQueryIds));
        findings.AddRange(CheckIndexSuitability(queryIndexRequirements, indexCatalog, missingIndexes, logicalRefToCatalogName, allQueryIds));
        findings.AddRange(CheckMinimalDatasetExtraction(queries, tableRefsByName, rowCounts, allQueryIds));
        findings.AddRange(CheckHeavyTriggers(triggers, tableRefsByName, allQueryIds));

        var columnCatalog = await LoadColumnCatalogAsync(conn, objectIds, ct).ConfigureAwait(false);
        ApplyLogicalRefAliasesToColumnCatalog(scopedTables, objectIdToQualified, columnCatalog);
        var equalityPairs = CollectEqualityColumnPairs(queries, tableRefsByName);
        findings.AddRange(CheckImplicitConversionRisk(equalityPairs, columnCatalog, allQueryIds));
        findings.AddRange(CheckParameterColumnTypeMismatch(queries, tableRefsByName, columnCatalog, allQueryIds));

        findings.AddRange(await CheckStatsFreshnessAsync(conn, scopedTables, rowCounts, objectIds, allQueryIds, ct).ConfigureAwait(false));

        findings.AddRange(CheckRedundantIndexes(indexCatalog, tableRefsByName, allQueryIds));

        var usageRows = await LoadIndexUsageRowsAsync(conn, objectIds, ct).ConfigureAwait(false);
        ApplyLogicalRefAliasesToIndexUsage(scopedTables, objectIdToQualified, usageRows);
        findings.AddRange(CheckUnusedIndexes(usageRows, indexCatalog, tableRefsByName, allQueryIds));

        var fkColumns = await LoadForeignKeyColumnsAsync(conn, objectIds, ct).ConfigureAwait(false);
        findings.AddRange(CheckFkMissingIndexes(indexCatalog, fkColumns, scopedTables, tableRefsByName, allQueryIds));

        var byRule = RuleIds
            .Select(ruleId =>
            {
                var ruleFindings = findings.Where(f => string.Equals(f.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)).ToList();
                return new Phase3RuleSummary
                {
                    RuleId = ruleId,
                    Pass = ruleFindings.Count(f => string.Equals(f.Status, "pass", StringComparison.OrdinalIgnoreCase)),
                    Warn = ruleFindings.Count(f => string.Equals(f.Status, "warn", StringComparison.OrdinalIgnoreCase)),
                    Fail = ruleFindings.Count(f => string.Equals(f.Status, "fail", StringComparison.OrdinalIgnoreCase)),
                    Error = ruleFindings.Count(f => string.Equals(f.Status, "error", StringComparison.OrdinalIgnoreCase))
                };
            })
            .ToList();

        return new Phase3PlansReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            Environment = environment,
            ConnectionSummary = connSummary,
            QueryCount = queries.Count,
            QueryFingerprints = queryFingerprints,
            StartedAtUtc = started.ToString("o"),
            DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
            TotalRules = RuleIds.Length,
            TotalFindings = findings.Count,
            Findings = findings,
            ByRule = byRule
        };
    }

    private static string DescribeConnection(string cs)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(cs);
            return $"DataSource={b.DataSource};InitialCatalog={b.InitialCatalog ?? "(default)"};";
        }
        catch
        {
            return "(connection string not parseable)";
        }
    }

    private static Dictionary<string, HashSet<string>> CollectReferencedTables(IReadOnlyList<QueryRecord> queries)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in queries)
        {
            if (string.IsNullOrWhiteSpace(q.SqlText)) continue;
            var cteNames = ExtractCteNames(q.SqlText);
            var parse = TsqlParser.Parse(q.SqlText);
            if (!parse.Success || parse.Fragment is null) continue;

            var v = new TableRefVisitor();
            parse.Fragment.Accept(v);
            foreach (var tr in v.TableRefs)
            {
                if (cteNames.Contains(GetObjectName(tr)))
                    continue;

                if (!map.TryGetValue(tr, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    map[tr] = ids;
                }
                ids.Add(q.QueryId);
            }
        }
        return map;
    }

    private static HashSet<string> ExtractCteNames(string sql)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sql))
            return names;

        const string pattern = @"(?ix)
            (?:\bWITH\b|,)
            \s*
            (?:\[(?<name>[^\]]+)\]|(?<name>[A-Za-z_][A-Za-z0-9_]*))
            \s+\bAS\b\s*\(
        ";

        foreach (Match m in Regex.Matches(sql, pattern))
        {
            var token = m.Groups["name"].Value.Trim();
            if (string.IsNullOrWhiteSpace(token))
                continue;

            if (!string.IsNullOrWhiteSpace(token))
                names.Add(token);
        }

        return names;
    }

    private static string GetObjectName(string tableRef)
    {
        if (string.IsNullOrWhiteSpace(tableRef))
            return string.Empty;

        var idx = tableRef.LastIndexOf('.');
        if (idx < 0 || idx == tableRef.Length - 1)
            return tableRef.Trim();

        return tableRef[(idx + 1)..].Trim();
    }

    private static IEnumerable<Phase3RuleFinding> CheckUnknownTables(
        Dictionary<string, HashSet<string>> refsByTable,
        HashSet<string> existing,
        IReadOnlyDictionary<string, string> queryFingerprintById,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();
        var missing = refsByTable.Keys.Where(t => !existing.Contains(t)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "schema.unknown_table",
                Status = "pass",
                Severity = "info",
                Message = "All statically referenced tables were found in the connected database.",
                QueryIds = allQueryIds
            });
            return findings;
        }

        foreach (var table in missing)
        {
            var queryIds = refsByTable[table].OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var fingerprints = queryIds
                .Where(queryFingerprintById.ContainsKey)
                .Select(id => queryFingerprintById[id])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            findings.Add(new Phase3RuleFinding
            {
                RuleId = "schema.unknown_table",
                Status = "fail",
                Severity = "error",
                Message = $"Referenced table `{table}` was not found in the connected database.",
                Recommendation = "Verify schema, table name, and target environment database.",
                AffectedObjects = new[] { table },
                QueryIds = queryIds,
                Evidence = new Dictionary<string, object?>
                {
                    ["queryIds"] = queryIds,
                    ["fingerprints"] = fingerprints
                }
            });
        }
        return findings;
    }

    private static IEnumerable<Phase3RuleFinding> CheckCoveringIndex(
        Dictionary<string, HashSet<string>> refsByTable,
        Dictionary<string, (int UsableIndexes, bool IsHeap)> indexInfo,
        HashSet<string> existing,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();
        var warnings = new List<Phase3RuleFinding>();

        foreach (var table in refsByTable.Keys.Where(existing.Contains))
        {
            if (!indexInfo.TryGetValue(table, out var info))
            {
                warnings.Add(BuildWarn(table, 0, true));
                continue;
            }

            if (info.UsableIndexes <= 1 && info.IsHeap)
                warnings.Add(BuildWarn(table, info.UsableIndexes, info.IsHeap));
        }

        if (warnings.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.covering_index",
                Status = "pass",
                Severity = "info",
                Message = "Referenced tables have at least basic index coverage heuristics.",
                QueryIds = allQueryIds
            });
            return findings;
        }

        findings.AddRange(warnings);
        return findings;

        Phase3RuleFinding BuildWarn(string table, int usableIndexes, bool isHeap) =>
            new()
            {
                RuleId = "db.covering_index",
                Status = "warn",
                Severity = "warn",
                Message = $"Table `{table}` appears under-indexed for reliable coverage checks.",
                Recommendation = "Review index strategy and add suitable covering indexes for filter/join/projection columns.",
                AffectedObjects = new[] { table },
                QueryIds = refsByTable.TryGetValue(table, out var ids)
                    ? ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                    : Array.Empty<string>(),
                Evidence = new Dictionary<string, object?>
                {
                    ["usableIndexCount"] = usableIndexes,
                    ["isHeap"] = isHeap
                }
            };
    }

    private static bool MissingIndexHintMatchesLogicalTable(
        string requirementLogicalTable,
        string dmvPhysicalTable,
        IReadOnlyDictionary<string, string> logicalRefToCatalogQualifiedName)
    {
        if (string.Equals(requirementLogicalTable, dmvPhysicalTable, StringComparison.OrdinalIgnoreCase))
            return true;
        return logicalRefToCatalogQualifiedName.TryGetValue(requirementLogicalTable, out var physical)
               && string.Equals(physical, dmvPhysicalTable, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps each referenced qualified name (including synonyms) to the resolved user table/view name in catalog form.
    /// </summary>
    private static Dictionary<string, string> BuildLogicalRefToCatalogQualifiedNameMap(
        IReadOnlyDictionary<string, int> scopedTables,
        IReadOnlyDictionary<int, string> objectIdToQualified)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (refKey, oid) in scopedTables)
        {
            if (objectIdToQualified.TryGetValue(oid, out var canon))
                map[refKey] = canon;
        }

        return map;
    }

    private static IEnumerable<Phase3RuleFinding> CheckIndexSuitability(
        IReadOnlyList<QueryIndexRequirement> requirements,
        IReadOnlyDictionary<string, List<TableIndexDefinition>> indexCatalog,
        List<(string Table, string EqCols, string IneqCols, string IncCols, decimal ImpactScore)> missingHints,
        IReadOnlyDictionary<string, string> logicalRefToCatalogQualifiedName,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();
        var failures = new List<Phase3RuleFinding>();

        foreach (var req in requirements)
        {
            if (req.PredicateColumns.Count == 0)
                continue;

            if (!indexCatalog.TryGetValue(req.Table, out var indexes) || indexes.Count == 0)
            {
                failures.Add(new Phase3RuleFinding
                {
                    RuleId = "db.index_suitability",
                    Status = "fail",
                    Severity = "error",
                    Message = $"No usable index matched query `{req.QueryId}` pattern on `{req.Table}`.",
                    Recommendation = "Review the templated DDL in evidence only as a starting point; validate naming, fill factor, filegroup, and existing indexes.",
                    AffectedObjects = new[] { req.Table, req.QueryId },
                    QueryIds = new[] { req.QueryId },
                    Evidence = BuildIndexMismatchEvidence(req, reason: "no_indexes_found", availableIndexes: null)
                });
                continue;
            }

            var match = indexes.Any(idx => MatchesRequirement(idx, req.PredicateColumns, req.ProjectedColumns));
            if (!match)
            {
                failures.Add(new Phase3RuleFinding
                {
                    RuleId = "db.index_suitability",
                    Status = "fail",
                    Severity = "error",
                    Message = $"No matching index pattern found for query `{req.QueryId}` on `{req.Table}`.",
                    Recommendation = "Review the templated DDL in evidence only as a starting point; extend or replace an existing index when possible.",
                    AffectedObjects = new[] { req.Table, req.QueryId },
                    QueryIds = new[] { req.QueryId },
                    Evidence = BuildIndexMismatchEvidence(req, reason: "no_matching_index", indexes.Select(i =>
                        new Dictionary<string, object?>
                        {
                            ["name"] = i.Name,
                            ["keyColumns"] = i.KeyColumns,
                            ["includeColumns"] = i.IncludeColumns
                        }).ToArray())
                });
            }
        }

        findings.AddRange(failures);

        var relevant = missingHints
            .Where(h => requirements.Any(r =>
                MissingIndexHintMatchesLogicalTable(r.Table, h.Table, logicalRefToCatalogQualifiedName)))
            .OrderByDescending(h => h.ImpactScore)
            .Take(20)
            .ToList();

        if (relevant.Count == 0 && failures.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.index_suitability",
                Status = "pass",
                Severity = "info",
                Message = "No high-impact missing index hints were found for referenced tables.",
                QueryIds = allQueryIds
            });
            return findings;
        }

        foreach (var h in relevant)
        {
            var matchedLogical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queryIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in requirements)
            {
                if (!MissingIndexHintMatchesLogicalTable(r.Table, h.Table, logicalRefToCatalogQualifiedName))
                    continue;
                if (!string.IsNullOrWhiteSpace(r.QueryId))
                    queryIdSet.Add(r.QueryId);
                matchedLogical.Add(r.Table);
            }

            var queryIds = queryIdSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var logicalAliases = matchedLogical
                .Where(t => !string.Equals(t, h.Table, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var message = logicalAliases.Length > 0
                ? $"Missing index hint for base object `{h.Table}` (queried as {string.Join(", ", logicalAliases.Select(t => $"`{t}`"))})."
                : $"Missing index hint detected for `{h.Table}`.";

            var evidence = new Dictionary<string, object?>
            {
                ["equalityColumns"] = h.EqCols,
                ["inequalityColumns"] = h.IneqCols,
                ["includedColumns"] = h.IncCols,
                ["impactScore"] = h.ImpactScore,
                ["suggestedIndexCreationScript"] = BuildDmMissingIndexPlaceholderSql(h.Table, h.EqCols, h.IneqCols, h.IncCols)
            };
            if (logicalAliases.Length > 0)
                evidence["queriedTableNames"] = logicalAliases;

            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.index_suitability",
                Status = "warn",
                Severity = "warn",
                Message = message,
                Recommendation = "Use DMV columns as hints only; assemble CREATE INDEX DDL manually after review.",
                AffectedObjects = logicalAliases.Length > 0
                    ? new[] { h.Table }.Concat(logicalAliases).ToArray()
                    : new[] { h.Table },
                QueryIds = queryIds,
                Evidence = evidence
            });
        }

        return findings;
    }

    private static IReadOnlyList<QueryIndexRequirement> BuildQueryIndexRequirements(
        IReadOnlyList<QueryRecord> queries,
        Dictionary<string, HashSet<string>> refsByTable,
        IReadOnlyDictionary<string, string> queryFingerprintById)
    {
        var requirements = new List<QueryIndexRequirement>();
        foreach (var q in queries)
        {
            if (string.IsNullOrWhiteSpace(q.SqlText) || string.IsNullOrWhiteSpace(q.QueryId))
                continue;

            var referencedTables = refsByTable
                .Where(kv => kv.Value.Contains(q.QueryId))
                .Select(kv => kv.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (referencedTables.Length == 0)
                continue;

            var parse = TsqlParser.Parse(q.SqlText);
            if (!parse.Success || parse.Fragment is null)
                continue;

            var visitor = new QueryColumnUsageVisitor();
            parse.Fragment.Accept(visitor);

            var aliasToTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in visitor.Tables)
            {
                var tableKey = $"{t.Schema}.{t.Name}";
                if (!referencedTables.Contains(tableKey, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(t.Alias))
                    aliasToTable[t.Alias] = tableKey;
                aliasToTable[t.Name] = tableKey;
            }

            foreach (var table in referencedTables)
            {
                var predicate = ResolveColumnsForTable(visitor.PredicateColumnRefs, table, aliasToTable, referencedTables);
                if (predicate.Count == 0)
                    continue;

                var projected = ResolveColumnsForTable(visitor.ProjectedColumnRefs, table, aliasToTable, referencedTables);
                queryFingerprintById.TryGetValue(q.QueryId, out var fingerprint);

                requirements.Add(new QueryIndexRequirement(
                    q.QueryId,
                    fingerprint,
                    table,
                    predicate,
                    projected));
            }
        }

        return requirements;
    }

    private static IEnumerable<Phase3RuleFinding> CheckMinimalDatasetExtraction(
        IReadOnlyList<QueryRecord> queries,
        Dictionary<string, HashSet<string>> refsByTable,
        Dictionary<string, long> rowCounts,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();
        var starQueries = queries
            .Where(q => !string.IsNullOrWhiteSpace(q.SqlText) && q.SqlText!.Contains("select *", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var issues = new List<Phase3RuleFinding>();
        foreach (var q in starQueries)
        {
            var tableNames = refsByTable
                .Where(kv => kv.Value.Contains(q.QueryId))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var t in tableNames)
            {
                if (!rowCounts.TryGetValue(t, out var rows)) continue;
                if (rows < 100_000) continue;

                issues.Add(new Phase3RuleFinding
                {
                    RuleId = "db.minimal_dataset_extraction",
                    Status = "warn",
                    Severity = "warn",
                    Message = $"Query `{q.QueryId}` uses SELECT * on large table `{t}`.",
                    Recommendation = "Select only required columns to reduce IO and memory usage.",
                    AffectedObjects = new[] { t, q.QueryId },
                    QueryIds = new[] { q.QueryId },
                    Evidence = new Dictionary<string, object?>
                    {
                        ["queryId"] = q.QueryId,
                        ["fingerprint"] = q.Fingerprint,
                        ["table"] = t,
                        ["estimatedRowCount"] = rows
                    }
                });
            }
        }

        if (issues.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.minimal_dataset_extraction",
                Status = "pass",
                Severity = "info",
                Message = "No SELECT * usage detected against large tables.",
                QueryIds = allQueryIds
            });
            return findings;
        }

        findings.AddRange(issues);
        return findings;
    }

    private static IEnumerable<Phase3RuleFinding> CheckHeavyTriggers(
        List<(string TriggerName, string ParentTableQualified, string? Definition)> triggers,
        Dictionary<string, HashSet<string>> refsByTable,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();
        var issues = new List<Phase3RuleFinding>();
        foreach (var t in triggers)
        {
            var body = t.Definition ?? string.Empty;
            var isHeavy = body.Length > 4000
                          || body.Contains("cursor", StringComparison.OrdinalIgnoreCase)
                          || body.Contains("while", StringComparison.OrdinalIgnoreCase)
                          || body.Contains("sp_executesql", StringComparison.OrdinalIgnoreCase)
                          || body.Contains("exec ", StringComparison.OrdinalIgnoreCase);
            if (!isHeavy) continue;

            issues.Add(new Phase3RuleFinding
            {
                RuleId = "db.heavy_trigger_impact",
                Status = "warn",
                Severity = "warn",
                Message =
                    $"Trigger `{t.TriggerName}` on table `{t.ParentTableQualified}` appears heavy and may impact DML performance.",
                Recommendation = "Simplify trigger logic and avoid row-by-row/procedural operations where possible.",
                AffectedObjects = new[] { t.ParentTableQualified, t.TriggerName },
                QueryIds = allQueryIds,
                Evidence = new Dictionary<string, object?>
                {
                    ["parentTable"] = t.ParentTableQualified,
                    ["definitionLength"] = body.Length,
                    ["containsCursor"] = body.Contains("cursor", StringComparison.OrdinalIgnoreCase),
                    ["containsWhile"] = body.Contains("while", StringComparison.OrdinalIgnoreCase),
                    ["containsExec"] = body.Contains("exec", StringComparison.OrdinalIgnoreCase)
                }
            });
        }

        if (issues.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.heavy_trigger_impact",
                Status = "pass",
                Severity = "info",
                Message = "No heavy trigger patterns were detected.",
                QueryIds = allQueryIds
            });
            return findings;
        }

        findings.AddRange(issues);
        return findings;
    }

    private static readonly string[] StringLikeSystemTypes = { "char", "nchar", "varchar", "nvarchar", "text", "ntext", "sysname" };

    private sealed record CatalogColumnMeta(int SystemTypeId, int UserTypeId, string TypeName, string? CollationName);

    private sealed record EqualityColumnPair(
        string LeftTableQualified,
        string RightTableQualified,
        string LeftColumn,
        string RightColumn);

    /// <remarks>Loads column type metadata for resolving implicit conversion risks.</remarks>
    private static async Task<Dictionary<string, CatalogColumnMeta>> LoadColumnCatalogAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var map = new Dictionary<string, CatalogColumnMeta>(StringComparer.OrdinalIgnoreCase);
        if (objectIds.Count == 0)
            return map;

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT s.name AS schema_name, o.name AS table_name, c.name AS column_name,
       c.system_type_id, c.user_type_id,
       tp.name AS type_name,
       c.collation_name
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id AND o.type IN ('U', 'V')
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.types tp ON tp.user_type_id = c.user_type_id
WHERE c.object_id IN ({idSql});
""";

        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        AddObjectIdParameters(cmd, objectIds);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var tbl = $"{rdr.GetString(0)}.{rdr.GetString(1)}.{rdr.GetString(2)}";
            var sysType = Convert.ToInt32(rdr.GetValue(3));
            var usrType = Convert.ToInt32(rdr.GetValue(4));
            var tn = rdr.IsDBNull(5) ? "?" : rdr.GetString(5);
            var collation = rdr.IsDBNull(6) ? null : rdr.GetString(6);
            map[tbl] = new CatalogColumnMeta(sysType, usrType, tn, collation);
        }

        return map;
    }

    private static IReadOnlyList<(EqualityColumnPair Pair, string QueryId, string? Fingerprint)> CollectEqualityColumnPairs(
        IReadOnlyList<QueryRecord> queries,
        Dictionary<string, HashSet<string>> refsByTable)
    {
        var list = new List<(EqualityColumnPair, string QueryId, string? Fingerprint)>();
        foreach (var q in queries)
        {
            if (string.IsNullOrWhiteSpace(q.SqlText) || string.IsNullOrWhiteSpace(q.QueryId))
                continue;

            var referencedTables = refsByTable
                .Where(kv => kv.Value.Contains(q.QueryId))
                .Select(kv => kv.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (referencedTables.Length == 0)
                continue;

            var cteNames = ExtractCteNames(q.SqlText);
            var parse = TsqlParser.Parse(q.SqlText);
            if (!parse.Success || parse.Fragment is null)
                continue;

            var usage = new QueryColumnUsageVisitor();
            parse.Fragment.Accept(usage);

            var aliasToTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in usage.Tables)
            {
                var tableKey = $"{t.Schema}.{t.Name}";
                if (!referencedTables.Contains(tableKey, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrWhiteSpace(t.Alias))
                    aliasToTable[t.Alias] = tableKey;
                aliasToTable[t.Name] = tableKey;
            }

            var eqVisitor = new EqualityPairExtractor();
            parse.Fragment.Accept(eqVisitor);

            foreach (var (e1, e2) in eqVisitor.Pairs)
            {
                if (e1 is null || e2 is null)
                    continue;

                var t1 = ToColumnRefToken(e1);
                var t2 = ToColumnRefToken(e2);
                if (t1 is null || t2 is null)
                    continue;

                var k1 = ResolveColumnRefToTable(t1, aliasToTable, referencedTables, cteNames);
                var k2 = ResolveColumnRefToTable(t2, aliasToTable, referencedTables, cteNames);
                if (k1 is null || k2 is null || string.Equals(k1, k2, StringComparison.OrdinalIgnoreCase))
                    continue;

                list.Add((new EqualityColumnPair(k1, k2, t1.Column, t2.Column), q.QueryId, q.Fingerprint));
            }
        }

        return list;
    }

    private static ColumnRefToken? ToColumnRefToken(ColumnReferenceExpression? col)
    {
        if (col?.MultiPartIdentifier?.Identifiers is not { Count: > 0 } ids)
            return null;
        var name = ids[^1].Value;
        var owner = ids.Count >= 2 ? ids[^2].Value : null;
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return new ColumnRefToken(owner, name);
    }

    private static string? ResolveColumnRefToTable(
        ColumnRefToken tok,
        IReadOnlyDictionary<string, string> aliasToTable,
        IReadOnlyCollection<string> referencedTables,
        HashSet<string> cteNames)
    {
        return ResolveColumnsForTableMember(tok, aliasToTable, referencedTables, cteNames);
    }

    /// <summary>Selects zero or one table when resolving an unambiguous column qualifier to a scoped table.</summary>
    private static string? ResolveColumnsForTableMember(
        ColumnRefToken tok,
        IReadOnlyDictionary<string, string> aliasToTable,
        IReadOnlyCollection<string> referencedTables,
        HashSet<string> cteNames)
    {
        if (string.IsNullOrWhiteSpace(tok.Column))
            return null;

        if (!string.IsNullOrWhiteSpace(tok.Owner))
        {
            if (aliasToTable.TryGetValue(tok.Owner, out var t))
                return t;
            if (cteNames.Contains(tok.Owner))
                return null;
            foreach (var r in referencedTables)
            {
                if (string.Equals(GetObjectName(r), tok.Owner, StringComparison.OrdinalIgnoreCase))
                    return r;
            }

            return null;
        }

        if (referencedTables.Count == 1)
            return referencedTables.First();

        return null;
    }

    private sealed class EqualityPairExtractor : TSqlFragmentVisitor
    {
        public List<(ColumnReferenceExpression? First, ColumnReferenceExpression? Second)> Pairs { get; } = new();

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            if (node.ComparisonType == BooleanComparisonType.Equals && node.SecondExpression is not null)
            {
                var first = UnwrapScalarToColumn(node.FirstExpression);
                var second = UnwrapScalarToColumn(node.SecondExpression);
                Pairs.Add((first, second));
            }

            base.ExplicitVisit(node);
        }

        private static ColumnReferenceExpression? UnwrapScalarToColumn(TSqlFragment? fragment)
        {
            if (fragment is null)
                return null;
            if (fragment is ColumnReferenceExpression col)
                return col;
            if (fragment is ParenthesisExpression p)
                return UnwrapScalarToColumn(p.Expression);
            return null;
        }
    }

    private static IEnumerable<Phase3RuleFinding> CheckImplicitConversionRisk(
        IReadOnlyList<(EqualityColumnPair Pair, string QueryId, string? Fingerprint)> pairs,
        IReadOnlyDictionary<string, CatalogColumnMeta> catalog,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();

        if (pairs.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.implicit_conversion_risk",
                Status = "pass",
                Severity = "info",
                Message = "No column-to-column equality predicates were extracted for implicit-conversion comparison.",
                QueryIds = allQueryIds
            });
            return findings;
        }

        var catalogResolvedPairs = 0;
        foreach (var (pair, queryId, fp) in pairs)
        {
            var k1 = $"{pair.LeftTableQualified}.{pair.LeftColumn}";
            var k2 = $"{pair.RightTableQualified}.{pair.RightColumn}";
            if (!catalog.TryGetValue(k1, out var c1) || !catalog.TryGetValue(k2, out var c2))
                continue;

            catalogResolvedPairs++;

            string? mismatchReason = null;
            if (c1.SystemTypeId != c2.SystemTypeId || c1.UserTypeId != c2.UserTypeId)
                mismatchReason = $"Different SQL types `{c1.TypeName}` vs `{c2.TypeName}` (system types {c1.SystemTypeId} vs {c2.SystemTypeId}).";
            else if (IsStringLikeType(c1.TypeName, c1.SystemTypeId) && IsStringLikeType(c2.TypeName, c2.SystemTypeId))
            {
                var col1 = string.IsNullOrEmpty(c1.CollationName) ? "DATABASE_DEFAULT" : c1.CollationName;
                var col2 = string.IsNullOrEmpty(c2.CollationName) ? "DATABASE_DEFAULT" : c2.CollationName;
                if (!string.Equals(col1, col2, StringComparison.OrdinalIgnoreCase))
                    mismatchReason = $"Same base type `{c1.TypeName}` but collation differs (`{col1}` vs `{col2}`), which can trigger implicit conversions in comparisons.";
            }

            if (mismatchReason is null)
                continue;

            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.implicit_conversion_risk",
                Status = "warn",
                Severity = "warn",
                Message =
                    $"Column equality `{pair.LeftColumn}` and `{pair.RightColumn}` may require implicit conversion in query `{queryId}`.",
                Recommendation = "Align column definitions (types/collation) on both sides of joins and equality predicates.",
                AffectedObjects = new[] { k1, k2, queryId },
                QueryIds = new[] { queryId },
                Evidence = new Dictionary<string, object?>
                {
                    ["leftColumn"] = k1,
                    ["rightColumn"] = k2,
                    ["details"] = mismatchReason,
                    ["fingerprint"] = fp
                }
            });
        }

        if (findings.Count > 0)
            return findings;

        findings.Add(new Phase3RuleFinding
        {
            RuleId = "db.implicit_conversion_risk",
            Status = "pass",
            Severity = "info",
            Message = catalogResolvedPairs == 0
                ? "Column-to-column predicates were extracted but neither side mapped to scoped catalog columns."
                : "Extracted column-to-column comparisons use matching column types/collation metadata for sampled pairs.",
            QueryIds = allQueryIds
        });

        return findings;
    }

    private static bool IsStringLikeType(string typeName, int systemTypeId)
    {
        foreach (var t in StringLikeSystemTypes)
        {
            if (typeName.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return systemTypeId is 167 or 231 or 175 or 99 or 35 or 239;
    }

    private static Phase3RuleFinding StatsPassFinding(IReadOnlyList<string> allQueryIds, string? skipReason = null) =>
        new()
        {
            RuleId = "db.stats_freshness",
            Status = "pass",
            Severity = "info",
            Message = skipReason is null
                ? "No statistics staleness heuristics were triggered on referenced tables."
                : $"Statistics freshness skipped ({skipReason}).",
            QueryIds = allQueryIds,
            Evidence = skipReason is null ? null : new Dictionary<string, object?> { ["reason"] = skipReason }
        };

    private static async Task<List<Phase3RuleFinding>> CheckStatsFreshnessAsync(
        SqlConnection conn,
        Dictionary<string, int> scopedTables,
        IReadOnlyDictionary<string, long> rowCountsByTable,
        IReadOnlyCollection<int> objectIds,
        IReadOnlyList<string> allQueryIds,
        CancellationToken ct)
    {
        _ = scopedTables;
        var findings = new List<Phase3RuleFinding>();
        if (objectIds.Count == 0)
        {
            findings.Add(StatsPassFinding(allQueryIds));
            return findings;
        }

        const string sqlTemplate = """
SELECT
    s.name AS schema_name,
    o.name AS object_name,
    st.name AS stats_name,
    sp.last_updated,
    COALESCE(sp.rows, 0) AS stat_rows,
    COALESCE(sp.modification_counter, 0) AS mod_counter
FROM sys.stats st
JOIN sys.objects o ON o.object_id = st.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
CROSS APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) sp
WHERE st.object_id IN ({0})
  AND st.auto_created = 0
  AND o.type IN ('U', 'V');
""";

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var warns = new List<Phase3RuleFinding>();

        try
        {
            await using var cmd = new SqlCommand(string.Format(sqlTemplate, idSql), conn) { CommandType = CommandType.Text };
            AddObjectIdParameters(cmd, objectIds);
            await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;

            while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            {
                var sch = rdr.IsDBNull(0) ? "dbo" : rdr.GetString(0);
                var tbl = rdr.IsDBNull(1) ? "?" : rdr.GetString(1);
                var tableKey = $"{sch}.{tbl}";
                rowCountsByTable.TryGetValue(tableKey, out var heapRows);

                DateTime? lastUpdated = null;
                if (!rdr.IsDBNull(3))
                {
                    var dt = rdr.GetDateTime(3);
                    lastUpdated = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
                }

                var statRows = rdr.IsDBNull(4) ? 0L : Convert.ToInt64(rdr.GetValue(4));
                var mods = rdr.IsDBNull(5) ? 0L : Convert.ToInt64(rdr.GetValue(5));

                var statName = rdr.IsDBNull(2) ? "?" : rdr.GetString(2);
                var statLabel = $"{sch}.{tbl}.{statName}";

                var staleTime = statRows >= 500 && lastUpdated.HasValue &&
                                (now - lastUpdated.Value).TotalDays >= 30;
                var highMods = statRows > 100 &&
                               mods > Math.Max(statRows / 10, heapRows > 1_000_000 ? statRows / 40 : statRows / 20);

                if (staleTime || highMods)
                {
                    warns.Add(new Phase3RuleFinding
                    {
                        RuleId = "db.stats_freshness",
                        Status = "warn",
                        Severity = "warn",
                        Message =
                            $"Statistics `{statLabel}` may be stale (rows={statRows}, modification_counter={mods}, last_updated={lastUpdated:O}).",
                        Recommendation = "Run UPDATE STATISTICS on hot tables before heavy workloads.",
                        AffectedObjects = new[] { tableKey, statLabel },
                        QueryIds = allQueryIds,
                        Evidence = new Dictionary<string, object?>
                        {
                            ["table"] = tableKey,
                            ["statsName"] = statName,
                            ["modificationCounter"] = mods,
                            ["rowsSampledCatalog"] = statRows,
                            ["lastUpdatedUtc"] = lastUpdated?.ToString("o")
                        }
                    });
                }
            }
        }
        catch
        {
            findings.Add(StatsPassFinding(allQueryIds, "STATS_FRESHNESS_SKIPPED_PERMISSIONS_OR_DMV"));
            return findings;
        }

        if (warns.Count == 0)
            findings.Add(StatsPassFinding(allQueryIds));
        else
            findings.AddRange(warns);

        return findings;
    }

    private static bool WiderNonClusteredCoversNarrow(TableIndexDefinition wide, TableIndexDefinition narrow)
    {
        if (narrow.IsClustered || wide.IsClustered)
            return false;
        if (narrow.KeyColumns.Count == 0 || narrow.KeyColumns.Count > wide.KeyColumns.Count)
            return false;

        for (var k = 0; k < narrow.KeyColumns.Count; k++)
        {
            if (!string.Equals(narrow.KeyColumns[k], wide.KeyColumns[k], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var coveredByWide = wide.KeyColumns.Concat(wide.IncludeColumns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return narrow.IncludeColumns.All(c => coveredByWide.Contains(c));
    }

    private static IEnumerable<Phase3RuleFinding> CheckRedundantIndexes(
        IReadOnlyDictionary<string, List<TableIndexDefinition>> indexCatalog,
        Dictionary<string, HashSet<string>> tableRefsByName,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();

        foreach (var table in tableRefsByName.Keys)
        {
            if (!indexCatalog.TryGetValue(table, out var indexes) || indexes.Count < 2)
                continue;

            var ordered = indexes.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                for (var j = i + 1; j < ordered.Count; j++)
                {
                    var a = ordered[i];
                    var b = ordered[j];

                    var duplicateKeyInc = SequenceEqualIgnoringCase(a.KeyColumns, b.KeyColumns)
                                          && SequenceEqualIgnoringCaseIgnOrder(a.IncludeColumns, b.IncludeColumns);
                    if (duplicateKeyInc)
                    {
                        findings.Add(new Phase3RuleFinding
                        {
                            RuleId = "db.redundant_indexes",
                            Status = "warn",
                            Severity = "warn",
                            Message =
                                $"Table `{table}` has duplicate index definitions `{a.Name}` and `{b.Name}` (keys and INCLUDE list).",
                            Recommendation = "Consolidate or drop duplicated indexes.",
                            AffectedObjects = new[] { table, a.Name, b.Name },
                            QueryIds = allQueryIds
                        });
                        continue;
                    }

                    foreach (var (narrow, wide) in new[] { (a, b), (b, a) })
                    {
                        if (narrow.Name.Equals(wide.Name, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!WiderNonClusteredCoversNarrow(wide, narrow))
                            continue;

                        findings.Add(new Phase3RuleFinding
                        {
                            RuleId = "db.redundant_indexes",
                            Status = "warn",
                            Severity = "warn",
                            Message =
                                $"Index `{narrow.Name}` on `{table}` may be redundant versus `{wide.Name}` (prefix keys + INCLUDE coverage heuristic).",
                            Recommendation = "Review whether the narrower index is still required or can be removed.",
                            AffectedObjects = new[] { table, narrow.Name, wide.Name },
                            QueryIds = allQueryIds,
                            Evidence = new Dictionary<string, object?>
                            {
                                ["narrowKeyColumns"] = narrow.KeyColumns.ToArray(),
                                ["wideKeyColumns"] = wide.KeyColumns.ToArray()
                            }
                        });
                        break;
                    }
                }
            }
        }

        if (findings.Count == 0)
        {
            return new[]
            {
                new Phase3RuleFinding
                {
                    RuleId = "db.redundant_indexes",
                    Status = "pass",
                    Severity = "info",
                    Message = "No obvious redundant nonclustered index pairs detected on referenced tables.",
                    QueryIds = allQueryIds
                }
            };
        }

        return findings;
    }

    private static bool SequenceEqualIgnoringCase(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool SequenceEqualIgnoringCaseIgnOrder(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
            return false;

        var sa = new SortedSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var sb = new SortedSet<string>(b, StringComparer.OrdinalIgnoreCase);
        return sa.SequenceEqual(sb, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<string, (long Reads, long Writes)>> LoadIndexUsageRowsAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var map = new Dictionary<string, (long Reads, long Writes)>(StringComparer.OrdinalIgnoreCase);
        if (objectIds.Count == 0)
            return map;

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT
    OBJECT_SCHEMA_NAME(i.object_id) AS schema_name,
    OBJECT_NAME(i.object_id) AS table_name,
    i.name AS index_name,
    COALESCE(us.user_seeks, 0) + COALESCE(us.user_scans, 0) + COALESCE(us.user_lookups, 0) AS total_reads,
    COALESCE(us.user_updates, 0) AS total_writes
FROM sys.indexes i
LEFT JOIN sys.dm_db_index_usage_stats us
    ON us.object_id = i.object_id
   AND us.index_id = i.index_id
   AND us.database_id = DB_ID()
WHERE i.object_id IN ({idSql})
  AND i.index_id > 0
  AND i.is_hypothetical = 0
  AND i.is_disabled = 0;
""";

        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
            AddObjectIdParameters(cmd, objectIds);
            await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            {
                var sch = rdr.IsDBNull(0) ? "dbo" : rdr.GetString(0);
                var tbl = rdr.IsDBNull(1) ? "?" : rdr.GetString(1);
                var idx = rdr.IsDBNull(2) ? "?" : rdr.GetString(2);
                var reads = rdr.IsDBNull(3) ? 0L : Convert.ToInt64(rdr.GetValue(3));
                var writes = rdr.IsDBNull(4) ? 0L : Convert.ToInt64(rdr.GetValue(4));
                var key = $"{sch}.{tbl}::{idx}";
                map[key] = (reads, writes);
            }
        }
        catch
        {
            // DMV access denied
        }

        return map;
    }

    private static IEnumerable<Phase3RuleFinding> CheckUnusedIndexes(
        IReadOnlyDictionary<string, (long Reads, long Writes)> usageByTableIndex,
        IReadOnlyDictionary<string, List<TableIndexDefinition>> indexCatalog,
        Dictionary<string, HashSet<string>> tableRefsByName,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();
        if (usageByTableIndex.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.unused_indexes",
                Status = "pass",
                Severity = "info",
                Message = "Index usage statistics were not available (empty result or DMV access may be limited).",
                QueryIds = allQueryIds,
                Evidence = new Dictionary<string, object?> { ["reason"] = "no_usage_rows" }
            });
            return findings;
        }

        foreach (var table in tableRefsByName.Keys)
        {
            if (!indexCatalog.TryGetValue(table, out var indexes))
                continue;

            foreach (var ix in indexes)
            {
                if (ix.IsClustered)
                    continue;

                var k = $"{table}::{ix.Name}";
                if (!usageByTableIndex.TryGetValue(k, out var u))
                    continue;

                if (u.Reads != 0)
                    continue;

                // Maintenance cost without evidenced read paths (DMVs reset on service restart — advisory only).
                if (u.Writes <= 0)
                    continue;

                findings.Add(new Phase3RuleFinding
                {
                    RuleId = "db.unused_indexes",
                    Status = "warn",
                    Severity = "warn",
                    Message =
                        $"Nonclustered index `{ix.Name}` on `{table}` has zero read activity but recorded updates (writes={u.Writes}). Confirm necessity after accounting for DMV resets.",
                    Recommendation =
                        "Remove or replace indexes that only add maintenance cost; validate on a warmed production-like workload.",
                    AffectedObjects = new[] { table, ix.Name },
                    QueryIds = allQueryIds,
                    Evidence = new Dictionary<string, object?>
                    {
                        ["reads"] = u.Reads,
                        ["writes"] = u.Writes
                    }
                });
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.unused_indexes",
                Status = "pass",
                Severity = "info",
                Message =
                    "No nonclustered indexes on referenced tables showed zero reads with sustained writes in usage DMVs.",
                QueryIds = allQueryIds
            });
            return findings;
        }

        return findings;
    }

    private sealed record FkGroup(string Schema, string Table, string FkName, IReadOnlyList<string> ColumnNamesInOrder);

    private static async Task<List<FkGroup>> LoadForeignKeyColumnsAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var list = new List<FkGroup>();
        if (objectIds.Count == 0)
            return list;

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT
    fk.object_id,
    fk.name AS fk_name,
    sch.name AS schema_name,
    po.name AS table_name,
    fc.constraint_column_id,
    COL_NAME(fc.parent_object_id, fc.parent_column_id)
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fc ON fc.constraint_object_id = fk.object_id
JOIN sys.objects po ON po.object_id = fk.parent_object_id
JOIN sys.schemas sch ON sch.schema_id = po.schema_id
WHERE fk.parent_object_id IN ({idSql})
ORDER BY fk.object_id, fc.constraint_column_id;
""";

        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        AddObjectIdParameters(cmd, objectIds);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<(int FkOid, string FkName, string Sch, string Tbl, int Ord, string Col)>();
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var fkOid = Convert.ToInt32(rdr.GetValue(0));
            var fkName = rdr.IsDBNull(1) ? $"FK_{fkOid}" : rdr.GetString(1);
            var sch = rdr.IsDBNull(2) ? "dbo" : rdr.GetString(2);
            var tbl = rdr.IsDBNull(3) ? "?" : rdr.GetString(3);
            var cid = Convert.ToInt32(rdr.GetValue(4));
            var cn = rdr.IsDBNull(5) ? "?" : rdr.GetString(5);
            rows.Add((fkOid, fkName, sch, tbl, cid, cn));
        }

        foreach (var g in rows.GroupBy(r => r.FkOid))
        {
            var first = g.First();
            var ordered = g.OrderBy(x => x.Ord).Select(x => x.Col).ToArray();
            list.Add(new FkGroup(first.Sch, first.Tbl, first.FkName, ordered));
        }

        return list;
    }

    private static IEnumerable<Phase3RuleFinding> CheckFkMissingIndexes(
        IReadOnlyDictionary<string, List<TableIndexDefinition>> indexCatalog,
        IReadOnlyList<FkGroup> fkGroups,
        Dictionary<string, int> scopedTables,
        Dictionary<string, HashSet<string>> refsByTable,
        IReadOnlyList<string> allQueryIds)
    {
        var findings = new List<Phase3RuleFinding>();
        foreach (var fk in fkGroups)
        {
            var tableKey = $"{fk.Schema}.{fk.Table}";
            if (!scopedTables.ContainsKey(tableKey))
                continue;

            if (fk.ColumnNamesInOrder.Count == 0)
                continue;

            if (!indexCatalog.TryGetValue(tableKey, out var indexes) || indexes.Count == 0)
            {
                findings.Add(new Phase3RuleFinding
                {
                    RuleId = "db.fk_missing_index",
                    Status = "warn",
                    Severity = "warn",
                    Message =
                        $"Foreign key `{fk.FkName}` on `{tableKey}` references columns `{string.Join(", ", fk.ColumnNamesInOrder)}` but no index metadata was matched.",
                    Recommendation =
                        "Add a nonclustered index on FK columns matching join/delete lookup patterns (leading key order).",
                    AffectedObjects = new[] { tableKey, fk.FkName },
                    QueryIds = refsByTable.TryGetValue(tableKey, out var qs)
                        ? qs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                        : allQueryIds,
                    Evidence = new Dictionary<string, object?> { ["columns"] = fk.ColumnNamesInOrder.ToArray() }
                });
                continue;
            }

            var hasSupporting = indexes.Any(ix => LeadingKeyPrefixesFk(ix.KeyColumns, fk.ColumnNamesInOrder));
            if (!hasSupporting)
            {
                findings.Add(new Phase3RuleFinding
                {
                    RuleId = "db.fk_missing_index",
                    Status = "warn",
                    Severity = "warn",
                    Message =
                        $"Foreign key `{fk.FkName}` on `{tableKey}` lacks a compatible leading index on `{string.Join(", ", fk.ColumnNamesInOrder)}`.",
                    Recommendation =
                        "Create an index whose key columns match the FK column sequence as a prefix (nonclustered is typical on the referencing table).",
                    AffectedObjects = new[] { tableKey, fk.FkName },
                    QueryIds = refsByTable.TryGetValue(tableKey, out var qs)
                        ? qs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                        : allQueryIds,
                    Evidence = new Dictionary<string, object?> { ["fkColumnsOrdered"] = fk.ColumnNamesInOrder.ToArray() }
                });
            }
        }

        if (findings.Count == 0)
        {
            findings.Add(new Phase3RuleFinding
            {
                RuleId = "db.fk_missing_index",
                Status = "pass",
                Severity = "info",
                Message =
                    "Foreign keys on referencing tables appeared to have a leading-prefix index supporting the FK columns, or none required validation.",
                QueryIds = allQueryIds
            });
            return findings;
        }

        return findings;
    }

    private static bool LeadingKeyPrefixesFk(IReadOnlyList<string> indexKeyColumns, IReadOnlyList<string> fkColumnsOrdered)
    {
        if (indexKeyColumns.Count < fkColumnsOrdered.Count || fkColumnsOrdered.Count == 0)
            return false;

        for (var i = 0; i < fkColumnsOrdered.Count; i++)
        {
            if (!string.Equals(indexKeyColumns[i], fkColumnsOrdered[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static async Task<Dictionary<string, int>> ResolveReferencedTablesAsync(
        SqlConnection conn,
        IEnumerable<string> referencedTables,
        CancellationToken ct)
    {
        var refs = referencedTables
            .Select(ParseSchemaAndTable)
            .Where(x => !string.IsNullOrWhiteSpace(x.Schema) && !string.IsNullOrWhiteSpace(x.Table))
            .Distinct()
            .ToList();

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (refs.Count == 0)
            return map;

        var valuesSql = string.Join(", ", refs.Select((_, i) => $"(@s{i}, @t{i})"));
        var sql = $"""
WITH refs(schema_name, table_name) AS (
    SELECT v.schema_name, v.table_name
    FROM (VALUES {valuesSql}) AS v(schema_name, table_name)
)
SELECT r.schema_name, r.table_name,
       COALESCE(tb.object_id, vw.object_id, snb.object_id) AS resolved_object_id
FROM refs r
JOIN sys.schemas sch ON sch.name = r.schema_name
LEFT JOIN sys.tables tb ON tb.schema_id = sch.schema_id AND tb.name = r.table_name
LEFT JOIN sys.views vw ON vw.schema_id = sch.schema_id AND vw.name = r.table_name
LEFT JOIN sys.synonyms sn ON sn.schema_id = sch.schema_id AND sn.name = r.table_name
LEFT JOIN sys.objects snb ON snb.object_id = OBJECT_ID(sn.base_object_name)
WHERE COALESCE(tb.object_id, vw.object_id, snb.object_id) IS NOT NULL;
""";

        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        for (var i = 0; i < refs.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@s{i}", refs[i].Schema);
            cmd.Parameters.AddWithValue($"@t{i}", refs[i].Table);
        }

        {
            await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = $"{rdr.GetString(0)}.{rdr.GetString(1)}";
                var objectId = rdr.GetInt32(2);
                map[key] = objectId;
            }
        }

        if (map.Count == 0)
            return map;

        var forward = await LoadSynonymForwardEdgesAsync(conn, map.Values.Distinct().ToArray(), ct).ConfigureAwait(false);
        foreach (var key in map.Keys.ToArray())
            map[key] = ResolveThroughSynonymEdges(map[key], forward);

        await FilterScopedTablesToUserTablesAndViewsAsync(conn, map, ct).ConfigureAwait(false);
        return map;
    }

    private static async Task<Dictionary<int, int>> LoadSynonymForwardEdgesAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> seedObjectIds,
        CancellationToken ct)
    {
        var forward = new Dictionary<int, int>();
        var frontier = new HashSet<int>(seedObjectIds.Where(id => id != 0));
        for (var hop = 0; hop < 16 && frontier.Count > 0; hop++)
        {
            var ids = frontier.ToArray();
            frontier.Clear();
            var idSql = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
            var sql = $"""
SELECT sn.object_id, OBJECT_ID(sn.base_object_name) AS base_object_id, bt.type AS base_type
FROM sys.synonyms sn
JOIN sys.objects bt ON bt.object_id = OBJECT_ID(sn.base_object_name)
WHERE sn.object_id IN ({idSql})
  AND OBJECT_ID(sn.base_object_name) IS NOT NULL;
""";
            await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
            AddObjectIdParameters(cmd, ids);
            await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            {
                var synOid = rdr.GetInt32(0);
                var baseOid = rdr.GetInt32(1);
                var baseType = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
                forward[synOid] = baseOid;
                if (string.Equals(baseType, "SN", StringComparison.OrdinalIgnoreCase))
                    frontier.Add(baseOid);
            }
        }

        return forward;
    }

    private static int ResolveThroughSynonymEdges(int objectId, IReadOnlyDictionary<int, int> forward)
    {
        var current = objectId;
        for (var guard = 0; guard < 32 && forward.TryGetValue(current, out var next); guard++)
            current = next;
        return current;
    }

    private static async Task FilterScopedTablesToUserTablesAndViewsAsync(
        SqlConnection conn,
        Dictionary<string, int> map,
        CancellationToken ct)
    {
        var ids = map.Values.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0)
            return;

        var idSql = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT object_id
FROM sys.objects
WHERE object_id IN ({idSql})
  AND type IN ('U', 'V');
""";
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        AddObjectIdParameters(cmd, ids);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var allowed = new HashSet<int>();
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            allowed.Add(rdr.GetInt32(0));

        foreach (var key in map.Keys.ToArray())
        {
            if (!allowed.Contains(map[key]))
                map.Remove(key);
        }
    }

    private static async Task<Dictionary<int, string>> LoadObjectIdToQualifiedNamesAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        if (objectIds.Count == 0)
            return result;

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT o.object_id, s.name AS schema_name, o.name AS object_name
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.object_id IN ({idSql})
  AND o.type IN ('U', 'V');
""";
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        AddObjectIdParameters(cmd, objectIds);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var oid = rdr.GetInt32(0);
            var sch = rdr.GetString(1);
            var name = rdr.GetString(2);
            result[oid] = $"{sch}.{name}";
        }

        return result;
    }

    private static void ApplyLogicalRefCatalogAliases(
        IReadOnlyDictionary<string, int> scopedTables,
        IReadOnlyDictionary<int, string> objectIdToQualified,
        Dictionary<string, long> rowCounts,
        Dictionary<string, (int UsableIndexes, bool IsHeap)> tableIndexInfo,
        Dictionary<string, List<TableIndexDefinition>> indexCatalog)
    {
        foreach (var (refKey, oid) in scopedTables)
        {
            if (!objectIdToQualified.TryGetValue(oid, out var canon))
                continue;
            if (string.Equals(refKey, canon, StringComparison.OrdinalIgnoreCase))
                continue;

            if (rowCounts.TryGetValue(canon, out var rc))
                rowCounts[refKey] = rc;
            else if (!rowCounts.ContainsKey(refKey))
                rowCounts[refKey] = 0L;

            if (tableIndexInfo.TryGetValue(canon, out var tii))
                tableIndexInfo[refKey] = tii;

            if (indexCatalog.TryGetValue(canon, out var ixList))
                indexCatalog[refKey] = ixList;
        }
    }

    private static void ApplyLogicalRefAliasesToColumnCatalog(
        IReadOnlyDictionary<string, int> scopedTables,
        IReadOnlyDictionary<int, string> objectIdToQualified,
        Dictionary<string, CatalogColumnMeta> columnCatalog)
    {
        var additions = new List<(string Key, CatalogColumnMeta Meta)>();
        foreach (var (refKey, oid) in scopedTables)
        {
            if (!objectIdToQualified.TryGetValue(oid, out var canon))
                continue;
            if (string.Equals(refKey, canon, StringComparison.OrdinalIgnoreCase))
                continue;

            var prefix = $"{canon}.";
            foreach (var kv in columnCatalog)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var suffix = kv.Key[prefix.Length..];
                additions.Add(($"{refKey}.{suffix}", kv.Value));
            }
        }

        foreach (var (key, meta) in additions)
            columnCatalog[key] = meta;
    }

    private static void ApplyLogicalRefAliasesToIndexUsage(
        IReadOnlyDictionary<string, int> scopedTables,
        IReadOnlyDictionary<int, string> objectIdToQualified,
        Dictionary<string, (long Reads, long Writes)> usageByTableIndex)
    {
        var additions = new List<(string Key, (long Reads, long Writes) Value)>();
        foreach (var (refKey, oid) in scopedTables)
        {
            if (!objectIdToQualified.TryGetValue(oid, out var canon))
                continue;
            if (string.Equals(refKey, canon, StringComparison.OrdinalIgnoreCase))
                continue;

            var prefix = $"{canon}::";
            foreach (var kv in usageByTableIndex.ToArray())
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var idxPart = kv.Key[prefix.Length..];
                additions.Add(($"{refKey}::{idxPart}", kv.Value));
            }
        }

        foreach (var (key, val) in additions)
            usageByTableIndex.TryAdd(key, val);
    }

    private static async Task<Dictionary<string, long>> LoadTableRowCountsAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (objectIds.Count == 0)
            return map;

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT q.schema_name, q.object_name AS table_name, COALESCE(SUM(ps.row_count), 0) AS row_count
FROM (
    SELECT t.object_id, s.name AS schema_name, t.name AS object_name
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    UNION ALL
    SELECT v.object_id, s.name, v.name
    FROM sys.views v
    JOIN sys.schemas s ON s.schema_id = v.schema_id
) q
LEFT JOIN sys.dm_db_partition_stats ps ON ps.object_id = q.object_id AND ps.index_id IN (0, 1)
WHERE q.object_id IN ({idSql})
GROUP BY q.schema_name, q.object_name;
""";
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        AddObjectIdParameters(cmd, objectIds);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = $"{rdr.GetString(0)}.{rdr.GetString(1)}";
            var rows = rdr.IsDBNull(2) ? 0L : Convert.ToInt64(rdr.GetValue(2));
            map[key] = rows;
        }
        return map;
    }

    private static async Task<Dictionary<string, (int UsableIndexes, bool IsHeap)>> LoadTableIndexInfoAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var map = new Dictionary<string, (int UsableIndexes, bool IsHeap)>(StringComparer.OrdinalIgnoreCase);
        if (objectIds.Count == 0)
            return map;

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT
    s.name AS schema_name,
    o.name AS table_name,
    SUM(CASE WHEN i.index_id > 0 AND i.is_hypothetical = 0 AND i.is_disabled = 0 THEN 1 ELSE 0 END) AS usable_indexes,
    MAX(CASE WHEN i.type_desc = 'HEAP' THEN 1 ELSE 0 END) AS is_heap
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.indexes i ON i.object_id = o.object_id
WHERE o.object_id IN ({idSql})
  AND o.type IN ('U', 'V')
GROUP BY s.name, o.name;
""";
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        AddObjectIdParameters(cmd, objectIds);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = $"{rdr.GetString(0)}.{rdr.GetString(1)}";
            var usable = rdr.IsDBNull(2) ? 0 : Convert.ToInt32(rdr.GetValue(2));
            var isHeap = !rdr.IsDBNull(3) && Convert.ToInt32(rdr.GetValue(3)) > 0;
            map[key] = (usable, isHeap);
        }
        return map;
    }

    private static async Task<Dictionary<string, List<TableIndexDefinition>>> LoadIndexCatalogAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, MutableIndexDefinition>>(StringComparer.OrdinalIgnoreCase);
        if (objectIds.Count == 0)
            return new Dictionary<string, List<TableIndexDefinition>>(StringComparer.OrdinalIgnoreCase);

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT
    s.name AS schema_name,
    o.name AS table_name,
    i.name AS index_name,
    i.index_id,
    i.is_hypothetical,
    i.is_disabled,
    i.type_desc,
    ic.key_ordinal,
    ic.is_included_column,
    c.name AS column_name
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
JOIN sys.indexes i ON i.object_id = o.object_id AND i.index_id > 0
LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE o.object_id IN ({idSql})
  AND o.type IN ('U', 'V')
ORDER BY s.name, o.name, i.name, ic.key_ordinal, ic.index_column_id;
""";

        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        AddObjectIdParameters(cmd, objectIds);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var table = $"{rdr.GetString(0)}.{rdr.GetString(1)}";
            var indexName = rdr.IsDBNull(2) ? $"idx_{rdr.GetInt32(3)}" : rdr.GetString(2);
            var isHypo = !rdr.IsDBNull(4) && rdr.GetBoolean(4);
            var isDisabled = !rdr.IsDBNull(5) && rdr.GetBoolean(5);
            var typeDesc = rdr.IsDBNull(6) ? "" : rdr.GetString(6);
            if (isHypo || isDisabled)
                continue;

            if (!result.TryGetValue(table, out var idxMap))
            {
                idxMap = new Dictionary<string, MutableIndexDefinition>(StringComparer.OrdinalIgnoreCase);
                result[table] = idxMap;
            }

            if (!idxMap.TryGetValue(indexName, out var idx))
            {
                idx = new MutableIndexDefinition(indexName)
                {
                    IsClustered = string.Equals(typeDesc, "CLUSTERED", StringComparison.OrdinalIgnoreCase)
                };
                idxMap[indexName] = idx;
            }

            if (rdr.IsDBNull(9))
                continue;

            var col = rdr.GetString(9);
            var isIncluded = !rdr.IsDBNull(8) && rdr.GetBoolean(8);
            var keyOrdinal = rdr.IsDBNull(7) ? 0 : Convert.ToInt32(rdr.GetValue(7));
            if (isIncluded)
            {
                if (!idx.IncludeColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                    idx.IncludeColumns.Add(col);
            }
            else if (keyOrdinal > 0)
            {
                idx.KeyColumns[keyOrdinal] = col;
            }
        }

        var final = new Dictionary<string, List<TableIndexDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in result)
        {
            final[table.Key] = table.Value.Values
                .Select(v => new TableIndexDefinition(
                    v.Name,
                    v.KeyColumns.OrderBy(x => x.Key).Select(x => x.Value).ToArray(),
                    v.IncludeColumns.ToArray(),
                    v.IsClustered))
                .ToList();
        }
        return final;
    }

    private static async Task<List<(string Table, string EqCols, string IneqCols, string IncCols, decimal ImpactScore)>> LoadMissingIndexHintsAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var list = new List<(string Table, string EqCols, string IneqCols, string IncCols, decimal ImpactScore)>();
        if (objectIds.Count == 0)
            return list;

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT
    OBJECT_SCHEMA_NAME(mid.object_id) AS schema_name,
    OBJECT_NAME(mid.object_id) AS table_name,
    COALESCE(mid.equality_columns, '') AS equality_columns,
    COALESCE(mid.inequality_columns, '') AS inequality_columns,
    COALESCE(mid.included_columns, '') AS included_columns,
    COALESCE(CONVERT(decimal(18,2), migs.avg_total_user_cost * (migs.avg_user_impact / 100.0) * (migs.user_seeks + migs.user_scans)), 0) AS impact_score
FROM sys.dm_db_missing_index_details mid
JOIN sys.dm_db_missing_index_groups mig ON mid.index_handle = mig.index_handle
JOIN sys.dm_db_missing_index_group_stats migs ON mig.index_group_handle = migs.group_handle
WHERE mid.database_id = DB_ID()
  AND mid.object_id IN ({idSql});
""";

        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
            AddObjectIdParameters(cmd, objectIds);
            await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await rdr.ReadAsync(ct).ConfigureAwait(false))
            {
                var schema = rdr.IsDBNull(0) ? "dbo" : rdr.GetString(0);
                var table = rdr.IsDBNull(1) ? "?" : rdr.GetString(1);
                var eq = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2);
                var ineq = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3);
                var inc = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4);
                var impact = rdr.IsDBNull(5) ? 0M : Convert.ToDecimal(rdr.GetValue(5));
                list.Add(($"{schema}.{table}", eq, ineq, inc, impact));
            }
        }
        catch
        {
            // Some SQL roles may not have DMV access; keep list empty.
        }

        return list;
    }

    private static async Task<List<(string TriggerName, string ParentTableQualified, string? Definition)>> LoadTriggerDefinitionsAsync(
        SqlConnection conn,
        IReadOnlyCollection<int> objectIds,
        CancellationToken ct)
    {
        var list = new List<(string TriggerName, string ParentTableQualified, string? Definition)>();
        if (objectIds.Count == 0)
            return list;

        var idSql = string.Join(", ", objectIds.Select((_, i) => $"@id{i}"));
        var sql = $"""
SELECT
    OBJECT_SCHEMA_NAME(tr.parent_id) AS parent_schema_name,
    OBJECT_NAME(tr.parent_id) AS parent_table_name,
    OBJECT_SCHEMA_NAME(tr.object_id) AS trigger_schema_name,
    tr.name AS trigger_name,
    m.definition
FROM sys.triggers tr
LEFT JOIN sys.sql_modules m ON m.object_id = tr.object_id
WHERE tr.parent_class_desc = 'OBJECT_OR_COLUMN'
  AND tr.parent_id IN ({idSql});
""";
        await using var cmd = new SqlCommand(sql, conn) { CommandType = CommandType.Text };
        AddObjectIdParameters(cmd, objectIds);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var parentSchema = rdr.IsDBNull(0) ? "dbo" : rdr.GetString(0);
            var parentName = rdr.IsDBNull(1) ? "?" : rdr.GetString(1);
            var parentQualified = $"{parentSchema}.{parentName}";
            var trigSchema = rdr.IsDBNull(2) ? "dbo" : rdr.GetString(2);
            var trigName = rdr.IsDBNull(3) ? "?" : rdr.GetString(3);
            var def = rdr.IsDBNull(4) ? null : rdr.GetString(4);
            list.Add(($"{trigSchema}.{trigName}", parentQualified, def));
        }
        return list;
    }

    private static (string Schema, string Table) ParseSchemaAndTable(string tableRef)
    {
        if (string.IsNullOrWhiteSpace(tableRef))
            return ("dbo", string.Empty);

        var idx = tableRef.IndexOf('.');
        if (idx <= 0 || idx >= tableRef.Length - 1)
            return ("dbo", tableRef.Trim());

        var schema = tableRef[..idx].Trim();
        var table = tableRef[(idx + 1)..].Trim();
        return (string.IsNullOrWhiteSpace(schema) ? "dbo" : schema, table);
    }

    private static void AddObjectIdParameters(SqlCommand cmd, IReadOnlyCollection<int> objectIds)
    {
        var i = 0;
        foreach (var id in objectIds)
        {
            cmd.Parameters.AddWithValue($"@id{i}", id);
            i++;
        }
    }

    private static bool MatchesRequirement(
        TableIndexDefinition index,
        IReadOnlyCollection<string> predicateColumns,
        IReadOnlyCollection<string> projectedColumns)
    {
        if (predicateColumns.Count == 0)
            return true;

        var keySet = index.KeyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in predicateColumns)
        {
            if (!keySet.Contains(p))
                return false;
        }

        var covered = index.KeyColumns.Concat(index.IncludeColumns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in projectedColumns)
        {
            if (!covered.Contains(c))
                return false;
        }

        return true;
    }

    private static Dictionary<string, object?> BuildIndexMismatchEvidence(
        QueryIndexRequirement req,
        string reason,
        IReadOnlyList<Dictionary<string, object?>>? availableIndexes)
    {
        var preds = req.PredicateColumns.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var projs = req.ProjectedColumns.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var includes = projs.Where(p => !preds.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();

        var suggestedSql = BuildTemplatedCreateNonclusteredIndexSql(req.Table, preds, includes, req.QueryId);
        var evidence = new Dictionary<string, object?>
        {
            ["fingerprint"] = req.Fingerprint,
            ["whereJoinColumns"] = preds,
            ["selectListColumns"] = projs,
            ["reason"] = reason,
            ["suggestedIndexCreationScript"] = suggestedSql
        };

        if (availableIndexes is { Count: > 0 })
            evidence["availableIndexes"] = availableIndexes.ToArray();

        return evidence;
    }

    /// <summary>
    /// Generates bracket-quoted CREATE INDEX text from predicate key columns (alphabetically ordered) plus INCLUDE for projection overlap.
    /// </summary>
    private static string BuildTemplatedCreateNonclusteredIndexSql(
        string tableQualified,
        IReadOnlyList<string> predicateKeyColumnsAlphabetical,
        IReadOnlyList<string> includeColumns,
        string queryId)
    {
        var (schema, table) = ParseSchemaAndTable(tableQualified);
        var fqTable = $"{SqlBracketIdentifier(schema)}.{SqlBracketIdentifier(table)}";
        var indexNameQuoted = SqlBracketIdentifier(BuildSafeSuggestedIndexName(schema, table, queryId));

        var keys = predicateKeyColumnsAlphabetical.Count > 0
            ? string.Join(", ", predicateKeyColumnsAlphabetical.Select(SqlBracketIdentifier))
            : SqlBracketIdentifier("__no_predicates_defined__");

        var sqlCore = includeColumns.Count > 0
            ? $"""
CREATE NONCLUSTERED INDEX {indexNameQuoted}
ON {fqTable} ({keys})
INCLUDE ({string.Join(", ", includeColumns.Select(SqlBracketIdentifier))});
"""
            : $"""
CREATE NONCLUSTERED INDEX {indexNameQuoted}
ON {fqTable} ({keys});
""";

        return sqlCore.TrimEnd();
    }

    private static string SqlBracketIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "[]";

        var inner = name.Replace("]", "]]", StringComparison.Ordinal);
        return $"[{inner}]";
    }

    private static string BuildSafeSuggestedIndexName(string schema, string table, string queryId)
    {
        var t = Regex.Replace(table ?? "t", @"[^A-Za-z0-9_]", "");
        var s = Regex.Replace(schema ?? "dbo", @"[^A-Za-z0-9_]", "");
        var suffix = DeriveQuerySuffix(queryId);
        var combined = $"{s}_{Truncate(t, 20)}_{suffix}";
        if (combined.Length > 90)
            combined = combined[..90];

        return $"IX_sqltool_{combined}";
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= max ? value : value[..max];
    }

    private static string DeriveQuerySuffix(string queryId)
    {
        if (string.IsNullOrWhiteSpace(queryId))
            return "query";

        var hex = Regex.Match(queryId, @"[a-fA-F0-9]{8,}");
        if (hex.Success)
            return hex.Value[..Math.Min(8, hex.Value.Length)];

        var alnum = Regex.Replace(queryId, @"[^A-Za-z0-9]", "");
        return alnum.Length == 0 ? "id" : alnum[..Math.Min(8, alnum.Length)];
    }

    /// <summary>
    /// Plain CREATE INDEX skeleton for DMV-based findings; equality/inequality/include details stay in separate evidence fields.
    /// </summary>
    private static string BuildDmMissingIndexPlaceholderSql(string tableQualified, string _eq, string _ineq, string _inc)
    {
        var (schema, table) = ParseSchemaAndTable(tableQualified);
        var fq = $"{SqlBracketIdentifier(schema)}.{SqlBracketIdentifier(table)}";
        var idx = SqlBracketIdentifier($"IX_sqltool_dm_{DeriveQuerySuffix(tableQualified)}");

        return $"""
CREATE NONCLUSTERED INDEX {idx}
ON {fq} ([key_columns_here])
INCLUDE ([include_columns_here]);
""".TrimEnd();
    }

    private static HashSet<string> ResolveColumnsForTable(
        IReadOnlyCollection<ColumnRefToken> refs,
        string table,
        IReadOnlyDictionary<string, string> aliasToTable,
        IReadOnlyCollection<string> queryTables)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var colRef in refs)
        {
            if (string.IsNullOrWhiteSpace(colRef.Column))
                continue;

            if (!string.IsNullOrWhiteSpace(colRef.Owner))
            {
                if (aliasToTable.TryGetValue(colRef.Owner, out var ownerTable) &&
                    string.Equals(ownerTable, table, StringComparison.OrdinalIgnoreCase))
                {
                    cols.Add(colRef.Column);
                }
                continue;
            }

            if (queryTables.Count == 1 && queryTables.Contains(table, StringComparer.OrdinalIgnoreCase))
                cols.Add(colRef.Column);
        }
        return cols;
    }

    private sealed record QueryIndexRequirement(
        string QueryId,
        string? Fingerprint,
        string Table,
        IReadOnlyCollection<string> PredicateColumns,
        IReadOnlyCollection<string> ProjectedColumns);

    private sealed record TableIndexDefinition(
        string Name,
        IReadOnlyList<string> KeyColumns,
        IReadOnlyList<string> IncludeColumns,
        bool IsClustered = false);

    private sealed class MutableIndexDefinition
    {
        public MutableIndexDefinition(string name) => Name = name;
        public string Name { get; }
        public bool IsClustered { get; set; }
        public Dictionary<int, string> KeyColumns { get; } = new();
        public List<string> IncludeColumns { get; } = new();
    }

    private sealed record TableToken(string Schema, string Name, string? Alias);
    private sealed record ColumnRefToken(string? Owner, string Column);

    private sealed class QueryColumnUsageVisitor : TSqlFragmentVisitor
    {
        public List<TableToken> Tables { get; } = new();
        public HashSet<ColumnRefToken> PredicateColumnRefs { get; } = new();
        public HashSet<ColumnRefToken> ProjectedColumnRefs { get; } = new();

        public override void ExplicitVisit(NamedTableReference node)
        {
            var ids = node.SchemaObject?.Identifiers;
            if (ids is not null && ids.Count > 0)
            {
                var name = ids[^1].Value;
                var schema = ids.Count >= 2 ? ids[^2].Value : "dbo";
                var alias = node.Alias?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                    Tables.Add(new TableToken(schema, name, alias));
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            AddPredicateColumns(node.FirstExpression);
            AddPredicateColumns(node.SecondExpression);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InPredicate node)
        {
            AddPredicateColumns(node.Expression);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(LikePredicate node)
        {
            AddPredicateColumns(node.FirstExpression);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanIsNullExpression node)
        {
            AddPredicateColumns(node.Expression);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanTernaryExpression node)
        {
            AddPredicateColumns(node.FirstExpression);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectScalarExpression node)
        {
            AddProjectedColumns(node.Expression);
            base.ExplicitVisit(node);
        }

        private void AddPredicateColumns(TSqlFragment? fragment)
        {
            if (fragment is null) return;
            var collector = new ColumnRefCollector();
            fragment.Accept(collector);
            foreach (var c in collector.Columns)
                PredicateColumnRefs.Add(c);
        }

        private void AddProjectedColumns(TSqlFragment? fragment)
        {
            if (fragment is null) return;
            var collector = new ColumnRefCollector();
            fragment.Accept(collector);
            foreach (var c in collector.Columns)
                ProjectedColumnRefs.Add(c);
        }
    }

    private sealed class ColumnRefCollector : TSqlFragmentVisitor
    {
        public HashSet<ColumnRefToken> Columns { get; } = new();

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            var ids = node.MultiPartIdentifier?.Identifiers;
            if (ids is null || ids.Count == 0)
            {
                base.ExplicitVisit(node);
                return;
            }

            var column = ids[^1].Value;
            if (string.IsNullOrWhiteSpace(column))
            {
                base.ExplicitVisit(node);
                return;
            }

            var owner = ids.Count >= 2 ? ids[^2].Value : null;
            if (!string.IsNullOrWhiteSpace(owner) && string.Equals(owner, "dbo", StringComparison.OrdinalIgnoreCase) && ids.Count >= 3)
                owner = ids[^2].Value;

            Columns.Add(new ColumnRefToken(owner, column));
            base.ExplicitVisit(node);
        }
    }

    private sealed class TableRefVisitor : TSqlFragmentVisitor
    {
        public HashSet<string> TableRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(WithCtesAndXmlNamespaces node)
        {
            var added = new List<string>();
            if (node.CommonTableExpressions is not null)
            {
                foreach (var cte in node.CommonTableExpressions)
                {
                    var cteName = cte.ExpressionName?.Value;
                    if (string.IsNullOrWhiteSpace(cteName)) continue;
                    if (CteNames.Add(cteName))
                        added.Add(cteName);
                }
            }

            base.ExplicitVisit(node);

            foreach (var name in added)
                CteNames.Remove(name);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            var ids = node.SchemaObject?.Identifiers;
            if (ids is null || ids.Count == 0)
            {
                base.ExplicitVisit(node);
                return;
            }

            var name = ids[^1].Value;
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("#", StringComparison.Ordinal))
            {
                base.ExplicitVisit(node);
                return;
            }

            if (ids.Count == 1 && CteNames.Contains(name))
            {
                base.ExplicitVisit(node);
                return;
            }

            var schema = ids.Count >= 2 ? ids[^2].Value : "dbo";
            if (!string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(name))
                TableRefs.Add($"{schema}.{name}");

            base.ExplicitVisit(node);
        }
    }
}

