namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: indentation uses tabs (not leading spaces-only at line start).
/// Text heuristic; skips blank lines and lines that start as comments after trim.
/// </summary>
public sealed class SqlStdIndentTabsRule : IRule
{
    public string Id => "sql.std.indent_tabs";

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        foreach (var rawLine in sql.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("--", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("/*", StringComparison.Ordinal)) continue;

            var leadingLen = line.Length - trimmed.Length;
            if (leadingLen == 0) continue;

            var ws = line.AsSpan(0, leadingLen);
            var onlySpaces = true;
            foreach (var c in ws)
            {
                if (c == '\t')
                {
                    onlySpaces = false;
                    break;
                }

                if (c != ' ')
                {
                    onlySpaces = false;
                    break;
                }
            }

            // Leading whitespace contains a tab — allowed
            if (!onlySpaces || ws.Length == 0) continue;

            return new[]
            {
                new Finding(
                    Id,
                    Severity.Info,
                    Confidence.Low,
                    "Indentation uses leading spaces; coding standard requires tab indentation.",
                    Suggestion: "Re-indent SQL using the Tab key.")
            };
        }

        return Array.Empty<Finding>();
    }
}
