using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlRepoAnalyzer.Rules;

namespace SqlRepoAnalyzer.Suggestions;

/// <summary>
/// Human-readable Markdown view of phase-2 static analysis (<c>suggestions.json</c>).
/// </summary>
public static class SuggestionsMarkdownFormatter
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    public static string Format(IReadOnlyList<SuggestionRecord> suggestions, string generatedAtUtcIso)
    {
        var sb = new StringBuilder(capacity: Math.Max(12_288, suggestions.Count * 200));
        Emit(sb, suggestions, generatedAtUtcIso);
        return sb.ToString();
    }

    public static void WriteUtf8File(string path, IReadOnlyList<SuggestionRecord> suggestions, string generatedAtUtcIso)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, Format(suggestions, generatedAtUtcIso), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Emit(StringBuilder sb, IReadOnlyList<SuggestionRecord> suggestions, string generatedAtUtcIso)
    {
        sb.AppendLine("# SQL static analysis (Phase 2 — suggestions)");
        sb.AppendLine();
        sb.AppendLine("This file mirrors **`suggestions.json`**: rule-based findings from ScriptDom and text heuristics. ");
        sb.AppendLine("**Phase 3 (`plan`)** adds database-connected checks separately (`plans.json` / `markdown/plans.md`).");
        sb.AppendLine();
        sb.AppendLine($"*Generated (UTC): {MdEscape(generatedAtUtcIso)}*");
        sb.AppendLine();

        var ordered = suggestions.OrderBy(s => s.QueryId, StringComparer.OrdinalIgnoreCase).ToList();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| Queries analyzed | {ordered.Count} |");

        var analyzed = ordered.Count(s => string.Equals(s.AnalysisStatus, "analyzed", StringComparison.OrdinalIgnoreCase));
        var noSql = ordered.Count(s => string.Equals(s.AnalysisStatus, "no_sql_text", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine($"| Status `analyzed` | {analyzed} |");
        sb.AppendLine($"| Status `no_sql_text` | {noSql} |");

        var totalFindings = ordered.Sum(s => s.Findings.Count);
        var err = ordered.Sum(s => s.Findings.Count(f => f.Severity == Severity.Error));
        var warn = ordered.Sum(s => s.Findings.Count(f => f.Severity == Severity.Warn));
        var info = ordered.Sum(s => s.Findings.Count(f => f.Severity == Severity.Info));
        sb.AppendLine($"| Total findings | {totalFindings} |");
        sb.AppendLine($"| Findings — Error | {err} |");
        sb.AppendLine($"| Findings — Warn | {warn} |");
        sb.AppendLine($"| Findings — Info | {info} |");
        sb.AppendLine();

        sb.AppendLine("## Index");
        sb.AppendLine();
        sb.AppendLine("| Query ID | Analysis | Parse OK | Findings |");
        sb.AppendLine("|----------|----------|:--------:|---------:|");
        foreach (var s in ordered)
        {
            sb.Append("| `").Append(MdEscape(s.QueryId)).Append("` | ");
            sb.Append(MdEscape(s.AnalysisStatus)).Append(" | ");
            sb.Append(s.ParseOk switch { true => "yes", false => "no", null => "—" }).Append(" | ");
            sb.Append(s.Findings.Count).AppendLine(" |");
        }
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Details by query");
        sb.AppendLine();

        foreach (var s in ordered)
        {
            sb.AppendLine($"### `{MdEscape(s.QueryId)}`");
            sb.AppendLine();
            sb.AppendLine("| Field | Value |");
            sb.AppendLine("|-------|-------|");
            sb.AppendLine($"| Fingerprint | `{MdEscape(s.Fingerprint)}` |");
            sb.AppendLine($"| Source kind | {MdEscape(s.SourceKind.ToString())} |");
            sb.AppendLine($"| Completeness | {MdCell(s.Completeness ?? "—")} |");
            sb.AppendLine($"| Analysis status | {MdEscape(s.AnalysisStatus)} |");

            if (!string.IsNullOrWhiteSpace(s.AnalysisWarning))
                sb.AppendLine($"| Analysis warning | {MdParagraph(s.AnalysisWarning)} |");

            sb.AppendLine($"| Parse OK | {(s.ParseOk switch { true => "yes", false => "no", null => "—"})} |");
            sb.AppendLine();

            if (s.ParseErrors is { Count: > 0 })
            {
                sb.AppendLine("#### Parse errors");
                sb.AppendLine();
                foreach (var pe in s.ParseErrors)
                    sb.AppendLine($"- {MdParagraph(pe)}");
                sb.AppendLine();
            }

            if (s.Findings.Count == 0)
            {
                sb.AppendLine("*No rule findings for this query.*");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine("#### Rule findings");
            sb.AppendLine();
            sb.AppendLine("| Severity | Rule ID | Confidence | Message |");
            sb.AppendLine("|:--------:|---------|:----------:|---------|");
            foreach (var f in s.Findings.OrderByDescending(x => x.Severity).ThenBy(x => x.RuleId, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("| ").Append(MdEscape(f.Severity.ToString())).Append(" | `").Append(MdEscape(f.RuleId)).Append("` | ");
                sb.Append(MdEscape(f.Confidence.ToString())).Append(" | ").Append(MdCellOneLine(f.Message)).AppendLine(" |");
            }
            sb.AppendLine();

            var withDetail = s.Findings.Where(f => !string.IsNullOrWhiteSpace(f.Suggestion) || f.Evidence is { Count: > 0 }).ToList();
            if (withDetail.Count > 0)
            {
                sb.AppendLine("#### Finding detail (suggestions & evidence)");
                sb.AppendLine();
                foreach (var f in withDetail.OrderByDescending(x => x.Severity).ThenBy(x => x.RuleId, StringComparer.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"##### `{MdEscape(f.RuleId)}` ({MdEscape(f.Severity.ToString())})");
                    sb.AppendLine();
                    if (!string.IsNullOrWhiteSpace(f.Suggestion))
                        sb.AppendLine($"- **Suggestion:** {MdParagraph(f.Suggestion)}");
                    if (f.Evidence is { Count: > 0 })
                    {
                        sb.AppendLine("- **Evidence:**");
                        sb.AppendLine();
                        sb.AppendLine("```json");
                        try
                        {
                            sb.AppendLine(JsonSerializer.Serialize(f.Evidence, EvidenceJsonOptions));
                        }
                        catch
                        {
                            sb.AppendLine("{ \"_note\": \"evidence serialization failed\" }");
                        }
                        sb.AppendLine("```");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }
    }

    private static string MdCell(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var one = Regex.Replace(text.Replace("\r\n", " ").Replace('\n', ' ').Trim(), @"\s+", " ");
        return MdEscape(one);
    }

    private static string MdCellOneLine(string? text) => MdCell(text);

    private static string MdParagraph(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var t = Regex.Replace(text.Trim(), @"\s+", " ");
        return MdEscape(t);
    }

    private static string MdEscape(string text)
    {
        if (text.Length == 0)
            return text;
        return text.Replace("\\", "\\\\").Replace("|", "\\|");
    }
}
