using System.Text;
using SqlRepoAnalyzer.Core.Queries;

namespace SqlRepoAnalyzer.Core.SqlFiles;

/// <summary>
/// Pulls likely T-SQL from C# string literals: verbatim (<c>@"…"</c>, <c>$@"…"</c>, <c>@$"…"</c>)
/// and, when chained with <c>+</c>, ordinary <c>"…"</c> fragments merged into one candidate.
/// Also merges chains that <b>start</b> with an ordinary <c>"…"</c> fragment (typical DAL concatenation).
/// Skips <c>//</c> and <c>/* */</c> outside literals so commented-out samples are not extracted.
/// </summary>
public static class CSharpEmbeddedSqlExtractor
{
    public static IEnumerable<QueryCandidate> ExtractFromFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var lineStarts = BuildLineStarts(text);

        foreach (var (sql, absStart, absEndExclusive) in EnumerateEmbeddedSqlChains(text))
        {
            var (sl, sc) = ToLineCol(lineStarts, absStart);
            var (el, ec) = ToLineCol(lineStarts, Math.Max(absStart, absEndExclusive - 1));

            yield return new QueryCandidate(
                SourceKind.CSharpEmbeddedSql,
                filePath,
                sl,
                sc,
                el,
                ec,
                sql);
        }

        foreach (var (sql, absStart, absEndExclusive) in EnumerateRegularStringLedSqlChains(text))
        {
            var (sl, sc) = ToLineCol(lineStarts, absStart);
            var (el, ec) = ToLineCol(lineStarts, Math.Max(absStart, absEndExclusive - 1));

            yield return new QueryCandidate(
                SourceKind.CSharpEmbeddedSql,
                filePath,
                sl,
                sc,
                el,
                ec,
                sql);
        }
    }

    private static IEnumerable<(string Sql, int AbsStart, int AbsEndExclusive)> EnumerateEmbeddedSqlChains(string t)
    {
        var pos = 0;
        while (pos < t.Length)
        {
            SkipWs(t, ref pos);
            SkipComments(t, ref pos);

            if (pos >= t.Length)
                break;

            if (!VerbatimOpensAt(t, pos))
            {
                pos++;
                continue;
            }

            var absStart = pos;
            var accum = new StringBuilder();
            if (!TryAppendVerbatimDecoded(t, ref pos, accum))
            {
                pos = absStart + 1;
                continue;
            }

            SkipWsAndComments(t, ref pos);
            while (pos < t.Length && t[pos] == '+')
            {
                pos++;
                SkipWsAndComments(t, ref pos);
                if (TryAppendVerbatimDecoded(t, ref pos, accum))
                {
                    SkipWsAndComments(t, ref pos);
                    continue;
                }

                if (TryAppendRegularStringDecoded(t, ref pos, accum))
                {
                    SkipWsAndComments(t, ref pos);
                    continue;
                }

                break;
            }

            var combined = accum.ToString().Trim();
            if (!SqlTextHeuristics.LooksLikeSql(combined))
                continue;

            yield return (combined, absStart, pos);
        }
    }

    /// <summary>SQL built from <c>"…"</c> first, then optional <c>+</c> with verbatim or regular fragments.</summary>
    private static IEnumerable<(string Sql, int AbsStart, int AbsEndExclusive)> EnumerateRegularStringLedSqlChains(string t)
    {
        var pos = 0;
        while (pos < t.Length)
        {
            SkipWs(t, ref pos);
            SkipComments(t, ref pos);

            if (pos >= t.Length)
                break;

            if (VerbatimOpensAt(t, pos))
            {
                var skipFrom = pos;
                var drain = new StringBuilder();
                if (TryAppendVerbatimDecoded(t, ref pos, drain))
                    continue;

                pos = skipFrom + 1;
                continue;
            }

            if (pos >= 1 && VerbatimOpensAt(t, pos - 1))
            {
                var openAt = pos - 1;
                var drain = new StringBuilder();
                if (TryAppendVerbatimDecoded(t, ref openAt, drain))
                {
                    pos = openAt;
                    continue;
                }
            }

            if (t[pos] != '"')
            {
                pos++;
                continue;
            }

            var absStart = pos;
            var accum = new StringBuilder();
            if (!TryAppendRegularStringDecoded(t, ref pos, accum))
            {
                pos = absStart + 1;
                continue;
            }

            SkipWsAndComments(t, ref pos);
            while (pos < t.Length && t[pos] == '+')
            {
                pos++;
                SkipWsAndComments(t, ref pos);
                if (TryAppendVerbatimDecoded(t, ref pos, accum))
                {
                    SkipWsAndComments(t, ref pos);
                    continue;
                }

                if (TryAppendRegularStringDecoded(t, ref pos, accum))
                {
                    SkipWsAndComments(t, ref pos);
                    continue;
                }

                break;
            }

            var combined = accum.ToString().Trim();
            if (!SqlTextHeuristics.LooksLikeSql(combined))
                continue;

            yield return (combined, absStart, pos);
        }
    }

    private static void SkipWs(string t, ref int pos)
    {
        while (pos < t.Length && char.IsWhiteSpace(t[pos]))
            pos++;
    }

    private static void SkipWsAndComments(string t, ref int pos)
    {
        while (true)
        {
            SkipWs(t, ref pos);
            var before = pos;
            SkipComments(t, ref pos);
            if (pos == before)
                break;
        }
    }

    /// <summary>Line/block comments only — call when not inside a string literal.</summary>
    private static void SkipComments(string t, ref int pos)
    {
        while (pos + 1 < t.Length && t[pos] == '/')
        {
            if (t[pos + 1] == '/')
            {
                pos += 2;
                while (pos < t.Length && t[pos] != '\n')
                    pos++;
                continue;
            }

            if (t[pos + 1] != '*')
                break;

            pos += 2;
            while (pos + 1 < t.Length && !(t[pos] == '*' && t[pos + 1] == '/'))
                pos++;

            if (pos + 1 < t.Length && t[pos] == '*' && t[pos + 1] == '/')
                pos += 2;
            else
                break;
        }
    }

    private static bool VerbatimOpensAt(string t, int idx)
    {
        if (idx + 3 <= t.Length && t[idx] == '$' && t[idx + 1] == '@' && t[idx + 2] == '"')
            return true;
        if (idx + 3 <= t.Length && t[idx] == '@' && t[idx + 1] == '$' && t[idx + 2] == '"')
            return true;
        return idx + 2 <= t.Length && t[idx] == '@' && t[idx + 1] == '"';
    }

    /// <summary>Reads one verbatim literal starting at <paramref name="pos"/> (must be on an opener) and appends decoded text.</summary>
    private static bool TryAppendVerbatimDecoded(string t, ref int pos, StringBuilder accum)
    {
        var i = pos;

        int contentIdx;
        if (i + 3 <= t.Length && t[i] == '$' && t[i + 1] == '@' && t[i + 2] == '"')
            contentIdx = i + 3;
        else if (i + 3 <= t.Length && t[i] == '@' && t[i + 1] == '$' && t[i + 2] == '"')
            contentIdx = i + 3;
        else if (i + 2 <= t.Length && t[i] == '@' && t[i + 1] == '"')
            contentIdx = i + 2;
        else
            return false;

        var j = contentIdx;
        while (j < t.Length)
        {
            if (t[j] != '"')
            {
                accum.Append(t[j]);
                j++;
                continue;
            }

            if (j + 1 < t.Length && t[j + 1] == '"')
            {
                accum.Append('"');
                j += 2;
                continue;
            }

            pos = j + 1;
            return true;
        }

        return false;
    }

    private static bool TryAppendRegularStringDecoded(string t, ref int pos, StringBuilder accum)
    {
        SkipWs(t, ref pos);
        if (pos >= t.Length || t[pos] != '"')
            return false;

        pos++;
        while (pos < t.Length)
        {
            var c = t[pos];
            if (c == '"' && BackslashRunBefore(t, pos) % 2 == 0)
            {
                pos++;
                return true;
            }

            if (c == '\\' && pos + 1 < t.Length)
            {
                var n = t[pos + 1];
                pos += 2;
                switch (n)
                {
                    case '"':
                        accum.Append('"');
                        continue;
                    case '\\':
                        accum.Append('\\');
                        continue;
                    case 'r':
                        accum.Append('\r');
                        continue;
                    case 'n':
                        accum.Append('\n');
                        continue;
                    case 't':
                        accum.Append('\t');
                        continue;
                    default:
                        accum.Append(n);
                        continue;
                }
            }

            accum.Append(c);
            pos++;
        }

        return false;
    }

    private static int BackslashRunBefore(string t, int quoteIdx)
    {
        var n = 0;
        for (var k = quoteIdx - 1; k >= 0 && t[k] == '\\'; k--)
            n++;
        return n;
    }

    private static int[] BuildLineStarts(string s)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
                starts.Add(i + 1);
        }

        return starts.ToArray();
    }

    private static (int line, int col) ToLineCol(int[] lineStarts, int absPos)
    {
        var idx = Array.BinarySearch(lineStarts, absPos);
        if (idx < 0) idx = ~idx - 1;
        if (idx < 0) idx = 0;
        var lineStart = lineStarts[idx];
        return (idx + 1, absPos - lineStart + 1);
    }
}
