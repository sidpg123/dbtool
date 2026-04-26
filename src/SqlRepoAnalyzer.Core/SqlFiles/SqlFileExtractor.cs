using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlRepoAnalyzer.Core.Queries;

namespace SqlRepoAnalyzer.Core.SqlFiles;

public static class SqlFileExtractor
{
    public static IEnumerable<QueryCandidate> ExtractFromFile(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var lineStarts = BuildLineStarts(text);

        foreach (var candidate in ExtractWithScriptDom(filePath, text, lineStarts))
            yield return candidate;
    }

    private static IEnumerable<QueryCandidate> ExtractWithScriptDom(string filePath, string text, int[] lineStarts)
    {
        // ScriptDom batch parsing understands SQL Server batch separators (GO) and T-SQL structure
        // far better than naive semicolon splitting (which breaks triggers/procs).
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);

        using var reader = new StringReader(text);
        var script = parser.Parse(reader, out var parseErrors) as TSqlScript;
        if (script is null)
        {
            foreach (var c in LegacySemicolonSplit(filePath, text, lineStarts))
                yield return c;
            yield break;
        }

        // If parsing produced errors, we still often get usable batches; only fall back if we got nothing.
        if (script.Batches.Count == 0)
        {
            foreach (var c in LegacySemicolonSplit(filePath, text, lineStarts))
                yield return c;
            yield break;
        }

        _ = parseErrors; // TODO(Phase2): surface parse errors per batch in queries.jsonl

        foreach (var batch in script.Batches)
        {
            var start = batch.StartOffset;
            var endExclusive = batch.StartOffset + batch.FragmentLength;
            if (start < 0 || endExclusive > text.Length) continue;

            var stmtText = text[start..endExclusive].Trim();
            if (stmtText.Length == 0) continue;
            if (!IsLikelySqlBatch(stmtText)) continue;

            var (sl, sc) = ToLineCol(lineStarts, start);
            var (el, ec) = ToLineCol(lineStarts, Math.Max(start, endExclusive - 1));

            yield return new QueryCandidate(
                SourceKind.SqlFile,
                filePath,
                sl, sc, el, ec,
                stmtText
            );
        }
    }

    private static bool IsLikelySqlBatch(string sql)
    {
        if (sql.Length < 6) return false;
        if (GoOnly.IsMatch(sql)) return false;
        return Sqlish.IsMatch(sql);
    }

    private static IEnumerable<QueryCandidate> LegacySemicolonSplit(string filePath, string text, int[] lineStarts)
    {
        var start = 0;
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '\'' && !inDouble)
            {
                if (inSingle && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inSingle = !inSingle;
            }
            else if (ch == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }

            if (ch == ';' && !inSingle && !inDouble)
            {
                var stmt = text[start..i].Trim();
                if (stmt.Length > 0)
                {
                    var (sl, sc) = ToLineCol(lineStarts, start);
                    var (el, ec) = ToLineCol(lineStarts, i);
                    yield return new QueryCandidate(SourceKind.SqlFile, filePath, sl, sc, el, ec, stmt);
                }
                start = i + 1;
            }
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
        {
            var (sl, sc) = ToLineCol(lineStarts, start);
            var (el, ec) = ToLineCol(lineStarts, text.Length);
            yield return new QueryCandidate(SourceKind.SqlFile, filePath, sl, sc, el, ec, tail);
        }
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
}
