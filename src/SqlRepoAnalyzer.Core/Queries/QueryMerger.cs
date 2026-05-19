namespace SqlRepoAnalyzer.Core.Queries;

public static class QueryMerger
{
    public static IReadOnlyList<QueryRecord> MergeAndFingerprint(string repoRoot, IEnumerable<QueryCandidate> candidates)
    {
        var root = Path.GetFullPath(repoRoot);

        var recordsById = new Dictionary<string, MutableRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            var relPath = MakeRelative(root, c.FilePath);
            var occurrence = new QueryOccurrence(relPath, c.StartLine, c.StartCol, c.EndLine, c.EndCol);

            string fingerprint;
            string queryId;

            if (!string.IsNullOrWhiteSpace(c.SqlText))
            {
                var normalized = SqlFingerprint.Normalize(c.SqlText!);
                if (!string.IsNullOrWhiteSpace(c.ParameterBindingsJson))
                    normalized += "\n" + c.ParameterBindingsJson.Trim();
                fingerprint = SqlFingerprint.Sha256Hex(normalized);
                queryId = $"q_{fingerprint[..16]}";
            }
            else
            {
                // QueryBuilder sites: stable ID based on location + sourceKind.
                var locKey = $"{c.SourceKind}:{relPath}:{c.StartLine}:{c.StartCol}";
                fingerprint = SqlFingerprint.Sha256Hex(locKey);
                queryId = $"qb_{fingerprint[..16]}";
            }

            if (!recordsById.TryGetValue(queryId, out var mr))
            {
                mr = new MutableRecord
                {
                    QueryId = queryId,
                    Fingerprint = fingerprint,
                    SqlText = c.SqlText,
                    SourceKind = c.SourceKind,
                    Completeness = c.Completeness,
                    ParameterBindingsJson = c.ParameterBindingsJson,
                    Occurrences = new List<QueryOccurrence>(),
                    OccurrenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                };
                recordsById[queryId] = mr;
            }

            var occurrenceKey = $"{occurrence.FilePath}:{occurrence.StartLine}:{occurrence.StartCol}:{occurrence.EndLine}:{occurrence.EndCol}";
            if (mr.OccurrenceKeys.Add(occurrenceKey))
            {
                mr.Occurrences.Add(occurrence);
            }

            if (!string.IsNullOrWhiteSpace(c.ParameterBindingsJson) && string.IsNullOrWhiteSpace(mr.ParameterBindingsJson))
                mr.ParameterBindingsJson = c.ParameterBindingsJson;
        }

        return recordsById.Values
            .Select(m => new QueryRecord(
                m.QueryId,
                m.Fingerprint,
                m.SqlText,
                m.SourceKind,
                m.Completeness,
                m.Occurrences,
                m.ParameterBindingsJson))
            .OrderBy(r => r.QueryId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string MakeRelative(string root, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full;
        return Path.GetRelativePath(root, full);
    }

    private sealed class MutableRecord
    {
        public required string QueryId { get; init; }
        public required string Fingerprint { get; init; }
        public required string? SqlText { get; init; }
        public required SourceKind SourceKind { get; init; }
        public required string? Completeness { get; init; }
        public string? ParameterBindingsJson { get; set; }
        public required List<QueryOccurrence> Occurrences { get; init; }
        public required HashSet<string> OccurrenceKeys { get; init; }
    }
}

