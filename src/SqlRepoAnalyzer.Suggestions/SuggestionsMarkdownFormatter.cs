using System.Collections;
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
            sb.AppendLine($"### Query ID:  `{MdEscape(s.QueryId)}`");
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
            sb.AppendLine("| Severity | Rule ID | Confidence | Count | Message |");
            sb.AppendLine("|:--------:|---------|:----------:|------:|---------|");
            foreach (var f in s.Findings.OrderByDescending(x => x.Severity).ThenBy(x => x.RuleId, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("| ").Append(MdEscape(f.Severity.ToString())).Append(" | `").Append(MdEscape(f.RuleId)).Append("` | ");
                sb.Append(MdEscape(f.Confidence.ToString())).Append(" | ").Append(GetOccurrenceCount(f).ToString()).Append(" | ");
                sb.Append(MdCellOneLine(f.Message)).AppendLine(" |");
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }
    }

    private static int GetOccurrenceCount(Finding f)
    {
        if (f.Evidence?.TryGetValue("occurrenceCount", out var v) != true || v is null)
            return 1;

        return v switch
        {
            int i => i,
            long l => (int)l,
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.TryGetInt32(out var n) ? n : 1,
            _ => 1
        };
    }

    private static string FormatOccurrenceLocationsLine(IReadOnlyDictionary<string, object?>? evidence)
    {
        if (evidence?.TryGetValue("occurrences", out var occ) != true || occ is null)
            return "";

        var parts = new List<string>();
        foreach (var item in FlattenOccurrenceList(occ))
        {
            if (TryGetLineColumn(item, out var line, out var col))
                parts.Add($"L{line}:C{col}");
        }

        return parts.Count == 0 ? "" : string.Join(", ", parts);
    }

    private static IEnumerable<object?> FlattenOccurrenceList(object occ)
    {
        switch (occ)
        {
            case IEnumerable<object?> eo:
                foreach (var x in eo)
                    yield return x;
                yield break;
            case IEnumerable en:
                foreach (var x in en)
                    yield return x;
                yield break;
            default:
                yield break;
        }
    }

    private static bool TryGetLineColumn(object? item, out int line, out int col)
    {
        line = 0;
        col = 0;
        switch (item)
        {
            case IReadOnlyDictionary<string, object?> d:
                return TryReadLineColumn(d, out line, out col);
            default:
                return false;
        }
    }

    private static bool TryReadLineColumn(IReadOnlyDictionary<string, object?> d, out int line, out int col)
    {
        line = 0;
        col = 0;
        if (!d.TryGetValue("line", out var ln) || !d.TryGetValue("column", out var cn))
            return false;

        line = CoerceToPositiveInt(ln);
        col = CoerceToPositiveInt(cn);
        return line > 0;
    }

    private static int CoerceToPositiveInt(object? v)
    {
        switch (v)
        {
            case int i:
                return i;
            case long l:
                return (int)l;
            case JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n):
                return n;
            default:
                return 0;
        }
    }

    private static bool HasEvidenceBeyondOccurrences(IReadOnlyDictionary<string, object?> ev)
    {
        foreach (var key in ev.Keys)
        {
            if (!string.Equals(key, "occurrenceCount", StringComparison.Ordinal)
                && !string.Equals(key, "occurrences", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, object?> FilterEvidenceForJsonDump(IReadOnlyDictionary<string, object?> ev)
    {
        var filtered = ev.Where(kv =>
                !string.Equals(kv.Key, "occurrenceCount", StringComparison.Ordinal)
                && !string.Equals(kv.Key, "occurrences", StringComparison.Ordinal))
            .ToDictionary(x => x.Key, x => x.Value);

        return filtered;
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
