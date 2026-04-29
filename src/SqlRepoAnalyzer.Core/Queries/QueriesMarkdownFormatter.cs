using System.Text;
using System.Text.RegularExpressions;

namespace SqlRepoAnalyzer.Core.Queries;

/// <summary>
/// Human-readable Markdown view of <c>queries.json</c> for DBAs and reviewers.
/// Canonical data remains JSON under <c>.sqltool/</c>; Markdown lives under <c>markdown/</c>.
/// </summary>
public static class QueriesMarkdownFormatter
{
    public static string Format(IReadOnlyList<QueryRecord> records, string generatedAtUtcIso)
    {
        var sb = new StringBuilder(capacity: Math.Max(16_384, records.Count * 256));
        Emit(sb, records, generatedAtUtcIso);
        return sb.ToString();
    }

    public static void WriteUtf8File(string path, IReadOnlyList<QueryRecord> records, string generatedAtUtcIso)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, Format(records, generatedAtUtcIso), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Emit(StringBuilder sb, IReadOnlyList<QueryRecord> records, string generatedAtUtcIso)
    {
        sb.AppendLine("# SQL query inventory");
        sb.AppendLine();
        sb.AppendLine("This file mirrors **`queries.json`** in a readable form. For automation, use the JSON file.");
        sb.AppendLine();
        sb.AppendLine($"*Generated (UTC): {MdEscape(generatedAtUtcIso)}*");
        sb.AppendLine();

        var ordered = records.OrderBy(r => r.QueryId, StringComparer.OrdinalIgnoreCase).ToList();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"| Total distinct queries | {ordered.Count} |");
        sb.AppendLine();

        var byKind = ordered.GroupBy(r => r.SourceKind).OrderBy(g => g.Key.ToString(), StringComparer.OrdinalIgnoreCase);
        sb.AppendLine("| Source kind | Count |");
        sb.AppendLine("|-------------|------:|");
        foreach (var g in byKind)
            sb.Append("| ").Append(MdEscape(g.Key.ToString())).Append(" | ").Append(g.Count()).AppendLine(" |");
        sb.AppendLine();

        var withSql = ordered.Count(r => !string.IsNullOrWhiteSpace(r.SqlText));
        var partial = ordered.Count(r => string.Equals(r.Completeness, "partial", StringComparison.OrdinalIgnoreCase));
        sb.AppendLine("| Metric | Count |");
        sb.AppendLine("|--------|------:|");
        sb.AppendLine($"| Distinct queries with `sqlText` | {withSql} |");
        sb.AppendLine($"| Rows with completeness = partial | {partial} |");
        sb.AppendLine();

        sb.AppendLine("## Index");
        sb.AppendLine();
        sb.AppendLine("| Query ID | Source kind | SQL text | Completeness | Occurrences |");
        sb.AppendLine("|----------|-------------|----------|--------------|-------------:|");
        foreach (var r in ordered)
        {
            sb.Append("| `").Append(MdEscape(r.QueryId)).Append("` | ");
            sb.Append(MdEscape(r.SourceKind.ToString())).Append(" | ");
            sb.Append(YesNo(!string.IsNullOrWhiteSpace(r.SqlText))).Append(" | ");
            sb.Append(MdCell(r.Completeness ?? "—")).Append(" | ");
            sb.Append(r.Occurrences.Count).AppendLine(" |");
        }
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("## Query details");
        sb.AppendLine();

        foreach (var r in ordered)
        {
            sb.AppendLine($"### `{MdEscape(r.QueryId)}`");
            sb.AppendLine();
            sb.AppendLine("| Field | Value |");
            sb.AppendLine("|-------|-------|");
            sb.AppendLine($"| Fingerprint (SHA-256 hex) | `{MdEscape(r.Fingerprint)}` |");
            sb.AppendLine($"| Source kind | {MdEscape(r.SourceKind.ToString())} |");
            sb.AppendLine($"| Completeness | {MdCell(r.Completeness ?? "—")} |");
            sb.AppendLine();

            if (r.Occurrences.Count > 0)
            {
                sb.AppendLine("#### Source locations");
                sb.AppendLine();
                sb.AppendLine("| File | Start (line:col) | End (line:col) |");
                sb.AppendLine("|------|------------------|----------------|");
                foreach (var o in r.Occurrences)
                {
                    sb.Append("| ").Append(MdCell(o.FilePath)).Append(" | ");
                    sb.Append(o.StartLine).Append(':').Append(o.StartCol).Append(" | ");
                    sb.Append(o.EndLine).Append(':').Append(o.EndCol).AppendLine(" |");
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("*No file occurrences recorded.*");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(r.SqlText))
            {
                sb.AppendLine("#### SQL text");
                sb.AppendLine();
                sb.AppendLine("```sql");
                sb.AppendLine(r.SqlText);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("#### SQL text");
                sb.AppendLine();
                sb.AppendLine("*Not available for this entry (e.g. dynamic TypeORM, partial extract, or scope filter).*");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }
    }

    private static string YesNo(bool v) => v ? "yes" : "no";

    private static string MdCell(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var one = Regex.Replace(text.Replace("\r\n", " ").Replace('\n', ' ').Trim(), @"\s+", " ");
        return MdEscape(one);
    }

    private static string MdEscape(string text)
    {
        if (text.Length == 0)
            return text;
        return text.Replace("\\", "\\\\").Replace("|", "\\|");
    }
}
