using System.Text.Json;
using System.Text.Json.Serialization;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Core.Reports;
using SqlRepoAnalyzer.Core.Schema;
using SqlRepoAnalyzer.Core.Tsql;
using SqlRepoAnalyzer.Rules;

namespace SqlRepoAnalyzer.Suggestions;

public static class SuggestionService
{
    public static IReadOnlyList<SuggestionRecord> BuildSuggestions(
        IReadOnlyList<QueryRecord> queries,
        SchemaSnapshot? schemaSnapshot,
        IReadOnlyList<IRule>? rules = null)
    {
        rules ??= RulesRegistry.DefaultRules;
        var schemaModel = schemaSnapshot is null ? null : new SchemaModel(schemaSnapshot);

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
                Schema = schemaModel,
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

    public static List<QueryRecord> ReadQueriesJson(string path)
    {
        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<QueryRecord>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        });
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
