using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Builds <see cref="Finding"/> evidence with <c>occurrenceCount</c> and <c>occurrences</c> (line/column within analyzed SQL text).
/// </summary>
public static class FindingEvidence
{
    /// <summary>1-based line and column as produced by ScriptDom.</summary>
    public static (int Line, int Column) Position(TSqlFragment fragment) =>
        (fragment.StartLine, fragment.StartColumn);

    public static IReadOnlyDictionary<string, object?> FromPositions(IReadOnlyList<(int Line, int Column)> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count == 0)
            throw new ArgumentException("At least one position is required.", nameof(positions));

        var sorted = positions.OrderBy(p => p.Line).ThenBy(p => p.Column).ToList();
        var occList = new List<object?>(sorted.Count);
        foreach (var p in sorted)
        {
            occList.Add(new Dictionary<string, object?>
            {
                ["line"] = p.Line,
                ["column"] = p.Column
            });
        }

        return new Dictionary<string, object?>
        {
            ["occurrenceCount"] = sorted.Count,
            ["occurrences"] = occList
        };
    }
}
