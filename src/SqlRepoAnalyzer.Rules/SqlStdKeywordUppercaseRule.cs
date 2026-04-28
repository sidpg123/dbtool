using System.Text.RegularExpressions;
using System.Linq;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: SQL keywords should be UPPERCASE (heuristic on raw text).
/// </summary>
public sealed class SqlStdKeywordUppercaseRule : IRule
{
    public string Id => "sql.std.keyword_uppercase";

    private static readonly string[] Keywords =
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "NULL",
        "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "ON",
        "GROUP", "BY", "ORDER", "HAVING",
        "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE",
        "MERGE", "UNION", "ALL", "DISTINCT", "TOP", "AS", "CASE", "WHEN", "THEN", "ELSE", "END",
        "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION", "WITH"
    };

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        var wrong = new List<string>();
        foreach (var kw in Keywords)
        {
            var rx = new Regex(@"\b" + Regex.Escape(kw) + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match m in rx.Matches(sql))
            {
                if (!string.Equals(m.Value, kw, StringComparison.Ordinal))
                    wrong.Add($"{m.Value} (expected {kw})");
            }
        }

        if (wrong.Count == 0) return Array.Empty<Finding>();

        var sample = string.Join("; ", wrong.Take(5));
        if (wrong.Count > 5) sample += "; …";

        return new[]
        {
            new Finding(
                Id,
                Severity.Info,
                Confidence.Low,
                $"SQL keywords should be UPPERCASE. Examples: {sample}",
                Suggestion: "Uppercase reserved words per coding standard (may include false positives inside string literals).")
        };
    }
}
