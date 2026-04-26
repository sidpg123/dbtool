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
                    Status = "skipped",
                    SkipReason = "no_sql_text"
                });
                continue;
            }

            if (!SelectOnlyValidator.IsSelectOnly(q.SqlText, out var reject))
            {
                results.Add(new PlanRecord
                {
                    QueryId = q.QueryId,
                    Fingerprint = q.Fingerprint,
                    SourceKind = q.SourceKind,
                    Status = "skipped",
                    SkipReason = reject
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
                    Status = "skipped",
                    SkipReason = "max_queries_cap"
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
                    Status = "dry_run",
                    SkipReason = "would_capture_showplan"
                });
                continue;
            }

            var exec = await ShowPlanExecutor.CaptureShowPlanXmlAsync(
                    options.ConnectionString,
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
                    Status = "error",
                    Error = exec.ErrorMessage ?? "unknown_error"
                });
                continue;
            }

            var safeName = SafeFileSegment(q.QueryId) + ".xml";
            var relative = Path.Combine("showplan-xml", safeName).Replace('\\', '/');
            var fullPath = Path.Combine(options.OutDir, "showplan-xml", safeName);
            await File.WriteAllTextAsync(fullPath, exec.Xml, cancellationToken).ConfigureAwait(false);

            var findings = ShowPlanXmlAnalyzer.Analyze(exec.Xml);
            results.Add(new PlanRecord
            {
                QueryId = q.QueryId,
                Fingerprint = q.Fingerprint,
                SourceKind = q.SourceKind,
                Status = "ok",
                PlanXmlRelativePath = relative,
                Findings = findings
            });
        }

        var plansPath = Path.Combine(options.OutDir, "plans.jsonl");
        JsonlWriter.WriteJsonLines(plansPath, results);
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
