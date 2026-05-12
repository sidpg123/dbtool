using System.Text.Json;
using System.Text.Json.Serialization;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Core.Reports;
using SqlRepoAnalyzer.Core.Tsql;
using SqlRepoAnalyzer.Rules;

namespace SqlRepoAnalyzer.Suggestions;

public static class SuggestionService
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<SuggestionRecord> BuildSuggestions(
        IReadOnlyList<QueryRecord> queries,
        IReadOnlyList<IRule>? rules = null)
    {
        rules ??= RulesRegistry.DefaultRules;

        var results = new List<SuggestionRecord>(queries.Count);
        foreach (var q in queries)
        {
            var findings = new List<Finding>();

            if (string.IsNullOrWhiteSpace(q.SqlText))
            {
                results.Add(new SuggestionRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    AnalysisStatus = "no_sql_text",
                    AnalysisWarning = q.SourceKind == SourceKind.TypeOrmQueryBuilderSite
                        ? "QueryBuilder site recorded; no static SQL text available for analysis."
                        : "No SQL text available for analysis.",
                    ParseOk = null,
                    ParseErrors = null,
                    Findings = findings
                });
                continue;
            }

            var parse = TsqlParser.Parse(q.SqlText);
            var ctx = new RuleContext
            {
                Query = q,
                Parse = parse
            };

            foreach (var rule in rules)
            {
                findings.AddRange(rule.Evaluate(ctx));
            }

            results.Add(new SuggestionRecord
            {
                QueryId = q.QueryId,
                Fingerprint = q.Fingerprint,
                SourceKind = q.SourceKind,
                Completeness = q.Completeness,
                AnalysisStatus = "analyzed",
                AnalysisWarning = ShouldWarnPartial(q) ? PartialWarning(q) : null,
                ParseOk = parse.Success,
                ParseErrors = parse.Success ? null : parse.Errors.Select(e => $"L{e.Line}:C{e.Column}: {e.Message}").ToList(),
                Findings = findings
            });
        }

        return results;
    }

    /// <summary>
    /// Reuses prior <see cref="SuggestionRecord"/> rows when the query fingerprint is unchanged; re-runs static rules for <paramref name="deltaQueries"/> and for any full-query rows that cannot be reused.
    /// </summary>
    public static IReadOnlyList<SuggestionRecord> BuildSuggestionsMerged(
        IReadOnlyList<QueryRecord> fullQueries,
        IReadOnlyList<QueryRecord> deltaQueries,
        IReadOnlyDictionary<string, SuggestionRecord>? previousByQueryId,
        IReadOnlyList<IRule>? rules = null)
    {
        previousByQueryId ??= new Dictionary<string, SuggestionRecord>(StringComparer.OrdinalIgnoreCase);
        var fromDelta = BuildSuggestions(deltaQueries, rules)
            .ToDictionary(s => s.QueryId, StringComparer.OrdinalIgnoreCase);

        var orphans = new List<QueryRecord>();
        foreach (var q in fullQueries)
        {
            if (fromDelta.ContainsKey(q.QueryId)) continue;
            if (previousByQueryId.TryGetValue(q.QueryId, out var prev) &&
                string.Equals(prev.Fingerprint, q.Fingerprint, StringComparison.Ordinal))
                continue;
            orphans.Add(q);
        }

        var fromOrphans = orphans.Count == 0
            ? new Dictionary<string, SuggestionRecord>(StringComparer.OrdinalIgnoreCase)
            : BuildSuggestions(orphans, rules).ToDictionary(s => s.QueryId, StringComparer.OrdinalIgnoreCase);

        var merged = new List<SuggestionRecord>(fullQueries.Count);
        foreach (var q in fullQueries)
        {
            if (fromDelta.TryGetValue(q.QueryId, out var rebuilt))
            {
                merged.Add(rebuilt);
                continue;
            }

            if (previousByQueryId.TryGetValue(q.QueryId, out var prev) &&
                string.Equals(prev.Fingerprint, q.Fingerprint, StringComparison.Ordinal))
            {
                merged.Add(prev);
                continue;
            }

            merged.Add(fromOrphans[q.QueryId]);
        }

        return merged;
    }

    public static Dictionary<string, SuggestionRecord>? TryReadSuggestionsByQueryId(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<SuggestionRecord>>(json, ReadJsonOptions);
        if (list is null || list.Count == 0) return null;
        return list.ToDictionary(s => s.QueryId, StringComparer.OrdinalIgnoreCase);
    }

    public static List<QueryRecord> ReadQueriesJson(string path)
    {
        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<QueryRecord>>(json, ReadJsonOptions);
        return list ?? new List<QueryRecord>();
    }

    public static void WriteSuggestionsJson(string path, IEnumerable<SuggestionRecord> records) =>
        JsonlWriter.WriteJsonArray(path, records);

    private static bool ShouldWarnPartial(QueryRecord q) =>
        string.Equals(q.Completeness, "partial", StringComparison.OrdinalIgnoreCase);

    private static string PartialWarning(QueryRecord q) =>
        q.SourceKind switch
        {
            SourceKind.TypeOrmQueryBuilderSite =>
                "Partial QueryBuilder analysis: only statically visible portions are considered.",
            SourceKind.TypeOrmQueryDynamic =>
                "Dynamic TypeORM query: SQL text was not fully static; findings may be incomplete.",
            _ => "Partial analysis: treat findings as lower confidence when SQL is incomplete/dynamic."
        };
}
