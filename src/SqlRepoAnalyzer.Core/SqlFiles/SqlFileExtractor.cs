namespace SqlRepoAnalyzer.Core.SqlFiles;

using SqlRepoAnalyzer.Core.Queries;

public static class SqlFileExtractor
{
    public static IEnumerable<QueryCandidate> ExtractFromFile(string filePath)
    {
        // MVP statement splitter: semicolon-aware with basic string handling.
        // Not perfect; Phase 2+ can replace with ScriptDom for full fidelity.
        var text = File.ReadAllText(filePath);
        var (lineStarts, _) = BuildLineStarts(text);

        var start = 0;
        var inSingle = false;
        var inDouble = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '\'' && !inDouble)
            {
                // Handle escaped '' inside strings
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
                    yield return new QueryCandidate(
                        SourceKind.SqlFile,
                        filePath,
                        sl, sc, el, ec,
                        stmt
                    );
                }
                start = i + 1;
            }
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
        {
            var (sl, sc) = ToLineCol(lineStarts, start);
            var (el, ec) = ToLineCol(lineStarts, text.Length);
            yield return new QueryCandidate(
                SourceKind.SqlFile,
                filePath,
                sl, sc, el, ec,
                tail
            );
        }
    }

    private static (int[] lineStarts, int lineCount) BuildLineStarts(string s)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return (starts.ToArray(), starts.Count);
    }

    private static (int line, int col) ToLineCol(int[] lineStarts, int pos)
    {
        // Binary search for last line start <= pos
        var idx = Array.BinarySearch(lineStarts, pos);
        if (idx < 0) idx = ~idx - 1;
        if (idx < 0) idx = 0;
        var lineStart = lineStarts[idx];
        return (idx + 1, pos - lineStart + 1);
    }
}

