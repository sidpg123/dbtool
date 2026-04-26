using System.Text.RegularExpressions;
using SqlRepoAnalyzer.Core.Queries;

namespace SqlRepoAnalyzer.Core.SqlFiles;

public static class SqlFileExtractor
{
    public static IEnumerable<QueryCandidate> ExtractFromFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var lineStarts = BuildLineStarts(text);

        foreach (var candidate in ExtractBatches(filePath, text, lineStarts))
            yield return candidate;
    }

    private static IEnumerable<QueryCandidate> ExtractBatches(string filePath, string text, int[] lineStarts)
    {
        // SQL Server module definitions (procedures, triggers, functions) are batch-oriented.
        // Splitting on semicolons breaks them at inner statements; GO is the reliable script boundary.
        var batchStart = 0;
        foreach (var (lineStart, lineEnd, nextLineStart) in EnumerateLines(text))
        {
            var line = text[lineStart..lineEnd];
            if (!GoOnly.IsMatch(line)) continue;

            foreach (var candidate in CreateBatchCandidates(filePath, text, lineStarts, batchStart, lineStart))
                yield return candidate;
            batchStart = nextLineStart;
        }

        foreach (var candidate in CreateBatchCandidates(filePath, text, lineStarts, batchStart, text.Length))
            yield return candidate;
    }

    private static IEnumerable<QueryCandidate> CreateBatchCandidates(
        string filePath,
        string text,
        int[] lineStarts,
        int start,
        int endExclusive)
    {
        var (trimmedStart, trimmedEnd) = TrimRange(text, start, endExclusive);
        if (trimmedStart >= trimmedEnd) yield break;

        var moduleStarts = FindModuleStarts(text, trimmedStart, trimmedEnd).ToList();
        if (moduleStarts.Count == 0)
        {
            foreach (var candidate in CreateStatementCandidates(filePath, text, lineStarts, trimmedStart, trimmedEnd))
                yield return candidate;
            yield break;
        }

        var firstModuleStart = FindModuleHeaderStart(text, trimmedStart, moduleStarts[0]);
        foreach (var candidate in CreateStatementCandidates(filePath, text, lineStarts, trimmedStart, firstModuleStart))
            yield return candidate;

        for (var i = 0; i < moduleStarts.Count; i++)
        {
            var moduleStart = FindModuleHeaderStart(text, trimmedStart, moduleStarts[i]);
            var moduleEnd = i + 1 < moduleStarts.Count
                ? FindModuleHeaderStart(text, trimmedStart, moduleStarts[i + 1])
                : trimmedEnd;

            var candidate = CreateSingleBatchCandidate(filePath, text, lineStarts, moduleStart, moduleEnd);
            if (candidate is not null) yield return candidate;
        }
    }

    private static IEnumerable<QueryCandidate> CreateStatementCandidates(
        string filePath,
        string text,
        int[] lineStarts,
        int start,
        int endExclusive)
    {
        foreach (var (statementStart, statementEnd) in SplitStatements(text, start, endExclusive))
        {
            var candidate = CreateSingleBatchCandidate(filePath, text, lineStarts, statementStart, statementEnd);
            if (candidate is not null) yield return candidate;
        }
    }

    private static QueryCandidate? CreateSingleBatchCandidate(
        string filePath,
        string text,
        int[] lineStarts,
        int start,
        int endExclusive)
    {
        var (trimmedStart, trimmedEnd) = TrimRange(text, start, endExclusive);
        if (trimmedStart >= trimmedEnd) return null;

        trimmedStart = SkipLeadingSqlTrivia(text, trimmedStart, trimmedEnd);
        if (trimmedStart >= trimmedEnd) return null;

        var sql = StripSqlComments(text[trimmedStart..trimmedEnd]).Trim();
        if (!IsLikelySqlBatch(sql))
        {
            var sqlStart = FindFirstSqlLineStart(text, trimmedStart, trimmedEnd);
            if (sqlStart < 0) return null;

            trimmedStart = SkipLeadingSqlTrivia(text, sqlStart, trimmedEnd);
            (trimmedStart, trimmedEnd) = TrimRange(text, trimmedStart, trimmedEnd);
            sql = StripSqlComments(text[trimmedStart..trimmedEnd]).Trim();
            if (!IsLikelySqlBatch(sql)) return null;
        }

        var (sl, sc) = ToLineCol(lineStarts, trimmedStart);
        var (el, ec) = ToLineCol(lineStarts, Math.Max(trimmedStart, trimmedEnd - 1));

        return new QueryCandidate(
            SourceKind.SqlFile,
            filePath,
            sl, sc, el, ec,
            sql
        );
    }

    private static int SkipLeadingSqlTrivia(string text, int start, int endExclusive)
    {
        var pos = start;
        while (pos < endExclusive)
        {
            while (pos < endExclusive && char.IsWhiteSpace(text[pos])) pos++;
            if (pos >= endExclusive) return pos;

            if (pos + 1 < endExclusive && text[pos] == '-' && text[pos + 1] == '-')
            {
                pos += 2;
                while (pos < endExclusive && text[pos] != '\n') pos++;
                if (pos < endExclusive) pos++;
                continue;
            }

            if (pos + 1 < endExclusive && text[pos] == '/' && text[pos + 1] == '*')
            {
                var close = text.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                if (close < 0 || close + 2 > endExclusive) return endExclusive;
                pos = close + 2;
                continue;
            }

            break;
        }

        return pos;
    }

    private static string StripSqlComments(string sql)
    {
        var result = new System.Text.StringBuilder(sql.Length);
        var inSingle = false;
        var inDouble = false;
        var inBracket = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n')
                {
                    inLineComment = false;
                    result.Append(ch);
                }
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inSingle)
            {
                result.Append(ch);
                if (ch == '\'' && next == '\'')
                {
                    result.Append(next);
                    i++;
                    continue;
                }

                if (ch == '\'') inSingle = false;
                continue;
            }

            if (inDouble)
            {
                result.Append(ch);
                if (ch == '"') inDouble = false;
                continue;
            }

            if (inBracket)
            {
                result.Append(ch);
                if (ch == ']') inBracket = false;
                continue;
            }

            if (ch == '-' && next == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (ch == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            result.Append(ch);

            if (ch == '\'') inSingle = true;
            else if (ch == '"') inDouble = true;
            else if (ch == '[') inBracket = true;
        }

        return result.ToString();
    }

    private static IEnumerable<int> FindModuleStarts(string text, int start, int endExclusive)
    {
        foreach (var (lineStart, lineEnd, _) in EnumerateLines(text[start..endExclusive], start))
        {
            var line = text[lineStart..lineEnd];
            if (ModuleStart.IsMatch(line)) yield return lineStart;
        }
    }

    private static IEnumerable<(int start, int endExclusive)> SplitStatements(string text, int start, int endExclusive)
    {
        var statementStart = start;
        var inSingle = false;
        var inDouble = false;
        var inBracket = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = start; i < endExclusive; i++)
        {
            var ch = text[i];
            var next = i + 1 < endExclusive ? text[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch == '\n') inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inSingle)
            {
                if (ch == '\'' && next == '\'')
                {
                    i++;
                    continue;
                }

                if (ch == '\'') inSingle = false;
                continue;
            }

            if (inDouble)
            {
                if (ch == '"') inDouble = false;
                continue;
            }

            if (inBracket)
            {
                if (ch == ']') inBracket = false;
                continue;
            }

            if (ch == '-' && next == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (ch == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (ch == '\'')
            {
                inSingle = true;
                continue;
            }

            if (ch == '"')
            {
                inDouble = true;
                continue;
            }

            if (ch == '[')
            {
                inBracket = true;
                continue;
            }

            if (ch != ';') continue;

            yield return (statementStart, i);
            statementStart = i + 1;
        }

        if (statementStart < endExclusive)
            yield return (statementStart, endExclusive);
    }

    private static int FindModuleHeaderStart(string text, int batchStart, int createLineStart)
    {
        var headerStart = createLineStart;
        var lineStarts = EnumerateLines(text[batchStart..createLineStart], batchStart)
            .Select(l => l.lineStart)
            .ToList();

        for (var i = lineStarts.Count - 1; i >= 0; i--)
        {
            var lineStart = lineStarts[i];
            var lineEnd = i + 1 < lineStarts.Count ? PreviousLineEnd(text, lineStarts[i + 1]) : PreviousLineEnd(text, createLineStart);
            var line = text[lineStart..lineEnd];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                headerStart = lineStart;
                continue;
            }

            if (trimmed.StartsWith("--", StringComparison.Ordinal) &&
                !trimmed.Contains("exec", StringComparison.OrdinalIgnoreCase))
            {
                headerStart = lineStart;
                continue;
            }

            break;
        }

        return headerStart;
    }

    private static int PreviousLineEnd(string text, int lineStart)
    {
        var end = Math.Max(0, lineStart - 1);
        if (end > 0 && text[end - 1] == '\r') end--;
        return end;
    }

    private static bool IsLikelySqlBatch(string sql)
    {
        if (sql.Length < 6) return false;
        if (GoOnly.IsMatch(sql)) return false;
        return Sqlish.IsMatch(sql);
    }

    private static int FindFirstSqlLineStart(string text, int start, int endExclusive)
    {
        foreach (var (lineStart, lineEnd, _) in EnumerateLines(text[start..endExclusive], start))
        {
            var line = text[lineStart..lineEnd];
            if (SqlLineStart.IsMatch(line)) return lineStart;
        }

        return -1;
    }

    private static IEnumerable<(int lineStart, int lineEnd, int nextLineStart)> EnumerateLines(string text, int offset = 0)
    {
        var lineStart = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;

            var lineEnd = i > 0 && text[i - 1] == '\r' ? i - 1 : i;
            yield return (offset + lineStart, offset + lineEnd, offset + i + 1);
            lineStart = i + 1;
        }

        if (lineStart < text.Length)
        {
            yield return (offset + lineStart, offset + text.Length, offset + text.Length);
        }
    }

    private static (int start, int endExclusive) TrimRange(string text, int start, int endExclusive)
    {
        while (start < endExclusive && char.IsWhiteSpace(text[start])) start++;
        while (endExclusive > start && char.IsWhiteSpace(text[endExclusive - 1])) endExclusive--;
        return (start, endExclusive);
    }

    private static int[] BuildLineStarts(string s)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }

    private static (int line, int col) ToLineCol(int[] lineStarts, int pos)
    {
        var idx = Array.BinarySearch(lineStarts, pos);
        if (idx < 0) idx = ~idx - 1;
        if (idx < 0) idx = 0;
        var lineStart = lineStarts[idx];
        return (idx + 1, pos - lineStart + 1);
    }

    private static readonly Regex GoOnly =
        new(@"^\s*go\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Sqlish =
        new(
            @"^\s*(?:/\*[\s\S]*?\*/|--[^\n]*\n|--[^\n]*$|\s)*" +
            @"(create|alter|drop|select|insert|update|delete|merge|with|if|begin|end|declare|throw|print)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SqlLineStart =
        new(@"^\s*(?:/\*|--|(create|alter|drop|select|insert|update|delete|merge|with|if|begin|declare)\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ModuleStart =
        new(@"^\s*(?:create|alter)\s+(?:or\s+alter\s+)?(?:procedure|proc|trigger|function|view)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
