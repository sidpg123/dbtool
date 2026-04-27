using SqlRepoAnalyzer.Core.Reports;
using SqlRepoAnalyzer.Suggestions;

namespace SqlRepoAnalyzer.ShowPlan;

public static class PlanRunService
{
    /// <summary>
    /// Reads queries.jsonl, optionally captures SHOWPLAN_XML per SELECT-only query, writes showplan-xml/*.xml and plans.jsonl.
    /// </summary>
    public static async Task<IReadOnlyList<PlanRecord>> RunAsync(PlanRunOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutDir);
        var xmlDir = Path.Combine(options.OutDir, "showplan-xml");
        Directory.CreateDirectory(xmlDir);

        var queries = SuggestionService.ReadQueriesJsonl(options.QueriesPath);
        var results = new List<PlanRecord>(queries.Count);
        var serverAttempts = 0;
        var metadataCache = new MssqlIndexMetadataCache();
        var planSuggestions = new List<SqlRepoAnalyzer.Suggestions.SuggestionRecord>(queries.Count);

        foreach (var q in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(q.SqlText))
            {
                results.Add(new PlanRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    Status = "skipped",
                    SkipReason = "no_sql_text"
                });
                planSuggestions.Add(new SqlRepoAnalyzer.Suggestions.SuggestionRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    AnalysisStatus = "no_sql_text",
                    AnalysisWarning = "No SQL text available for SHOWPLAN capture.",
                    ParseOk = null,
                    ParseErrors = null,
                    Findings = Array.Empty<SqlRepoAnalyzer.Rules.Finding>()
                });
                continue;
            }

            if (!SelectOnlyValidator.IsEligibleForShowPlan(q.SqlText, options.AllowDml, out var reject))
            {
                results.Add(new PlanRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    Status = "skipped",
                    SkipReason = reject
                });
                planSuggestions.Add(new SqlRepoAnalyzer.Suggestions.SuggestionRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    AnalysisStatus = "skipped",
                    AnalysisWarning = $"Not eligible for SHOWPLAN: {reject}",
                    ParseOk = null,
                    ParseErrors = null,
                    Findings = Array.Empty<SqlRepoAnalyzer.Rules.Finding>()
                });
                continue;
            }

            if (serverAttempts >= options.MaxQueries)
            {
                results.Add(new PlanRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    Status = "skipped",
                    SkipReason = "max_queries_cap"
                });
                planSuggestions.Add(new SqlRepoAnalyzer.Suggestions.SuggestionRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    AnalysisStatus = "skipped",
                    AnalysisWarning = "Skipped due to --max-queries cap.",
                    ParseOk = null,
                    ParseErrors = null,
                    Findings = Array.Empty<SqlRepoAnalyzer.Rules.Finding>()
                });
                continue;
            }

            serverAttempts++;

            if (options.DryRun)
            {
                results.Add(new PlanRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    Status = "dry_run",
                    SkipReason = "would_capture_showplan"
                });
                planSuggestions.Add(new SqlRepoAnalyzer.Suggestions.SuggestionRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    AnalysisStatus = "dry_run",
                    AnalysisWarning = "Dry run: would capture SHOWPLAN_XML.",
                    ParseOk = null,
                    ParseErrors = null,
                    Findings = Array.Empty<SqlRepoAnalyzer.Rules.Finding>()
                });
                continue;
            }

            var exec = await ShowPlanExecutor.CaptureShowPlanXmlAsync(
                    options.ConnectionString!,
                    q.SqlText,
                    options.CommandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!exec.Success || string.IsNullOrWhiteSpace(exec.Xml))
            {
                results.Add(new PlanRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    Status = "error",
                    Error = exec.ErrorMessage ?? "unknown_error"
                });
                planSuggestions.Add(new SqlRepoAnalyzer.Suggestions.SuggestionRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Completeness = q.Completeness,
                    AnalysisStatus = "error",
                    AnalysisWarning = exec.ErrorMessage ?? "unknown_error",
                    ParseOk = null,
                    ParseErrors = null,
                    Findings = Array.Empty<SqlRepoAnalyzer.Rules.Finding>()
                });
                continue;
            }

            var safeName = SafeFileSegment(q.QueryId) + ".xml";
            var relative = Path.Combine("showplan-xml", safeName).Replace('\\', '/');
            var fullPath = Path.Combine(options.OutDir, "showplan-xml", safeName);
            await File.WriteAllTextAsync(fullPath, exec.Xml, cancellationToken).ConfigureAwait(false);

            var findings = ShowPlanXmlAnalyzer.Analyze(exec.Xml);
            findings = await PlanMetadataEnricher.EnrichIfNeededAsync(
                    options.ConnectionString!,
                    exec.Xml,
                    findings,
                    options.CommandTimeoutSeconds,
                    metadataCache,
                    cancellationToken)
                .ConfigureAwait(false);

            results.Add(new PlanRecord
            {
                QueryId = q.QueryId,
                Fingerprint = q.Fingerprint,
                SourceKind = q.SourceKind,
                Completeness = q.Completeness,
                Status = "ok",
                PlanXmlRelativePath = relative,
                Findings = findings
            });

            planSuggestions.Add(new SqlRepoAnalyzer.Suggestions.SuggestionRecord
            {
                QueryId = q.QueryId,
                Fingerprint = q.Fingerprint,
                SourceKind = q.SourceKind,
                Completeness = q.Completeness,
                AnalysisStatus = "planned",
                AnalysisWarning = null,
                ParseOk = true,
                ParseErrors = null,
                Findings = findings
            });
        }

        var plansPath = Path.Combine(options.OutDir, "plans.jsonl");
        JsonlWriter.WriteJsonLines(plansPath, results);

        var planSuggestionsPath = Path.Combine(options.OutDir, "plan-suggestions.jsonl");
        JsonlWriter.WriteJsonLines(planSuggestionsPath, planSuggestions);
        return results;
    }

    private static string SafeFileSegment(string queryId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(queryId.Length);
        foreach (var c in queryId)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
