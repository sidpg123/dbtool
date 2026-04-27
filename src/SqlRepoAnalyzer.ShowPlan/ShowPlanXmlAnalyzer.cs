using System.Xml.Linq;
using SqlRepoAnalyzer.Rules;

namespace SqlRepoAnalyzer.ShowPlan;

/// <summary>
/// Lightweight rules over SHOWPLAN_XML (2004/07/showplan namespace).
/// </summary>
public static class ShowPlanXmlAnalyzer
{
    private static readonly XNamespace ShowplanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static IReadOnlyList<Finding> Analyze(string showPlanXml)
    {
        var findings = new List<Finding>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(showPlanXml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            findings.Add(new Finding(
                "plan.xml_parse_error",
                Severity.Warn,
                Confidence.High,
                $"Failed to parse SHOWPLAN_XML: {ex.Message}"));
            return findings;
        }

        var root = doc.Root;
        if (root is null) return findings;

        var relOps = root.Descendants(ShowplanNs + "RelOp").ToList();
        var tableScans = relOps.Count(e => string.Equals((string?)e.Attribute("PhysicalOp"), "Table Scan", StringComparison.OrdinalIgnoreCase));
        if (tableScans > 0)
        {
            findings.Add(new Finding(
                "plan.table_scan",
                Severity.Warn,
                Confidence.Medium,
                $"Estimated plan contains {tableScans} table scan operator(s).",
                Suggestion: "Review predicates, indexes, and cardinality estimates; consider covering or filtered indexes."));
        }

        var keyLookups = relOps.Count(e => string.Equals((string?)e.Attribute("PhysicalOp"), "Key Lookup", StringComparison.OrdinalIgnoreCase));
        if (keyLookups > 0)
        {
            findings.Add(new Finding(
                "plan.key_lookup",
                Severity.Warn,
                Confidence.Medium,
                $"Estimated plan contains {keyLookups} key lookup operator(s).",
                Suggestion: "If lookups dominate cost, consider a covering index or query rewrite.",
                Evidence: new Dictionary<string, object?> { ["needsMetadata"] = true }));
        }

        var missing = root.Descendants(ShowplanNs + "MissingIndexes").Any();
        if (missing)
        {
            findings.Add(new Finding(
                "plan.missing_index",
                Severity.Info,
                Confidence.Low,
                "Plan XML includes MissingIndexes metadata (optimizer suggestion, not a mandate).",
                Suggestion: "Validate with real workload and index design guidelines before adding indexes.",
                Evidence: new Dictionary<string, object?> { ["needsMetadata"] = true }));
        }

        var parallelism = relOps.Count(e => string.Equals((string?)e.Attribute("PhysicalOp"), "Parallelism", StringComparison.OrdinalIgnoreCase));
        if (parallelism > 0)
        {
            findings.Add(new Finding(
                "plan.parallelism",
                Severity.Info,
                Confidence.Low,
                $"Estimated plan includes {parallelism} parallelism operator(s)."));
        }

        return findings;
    }
}
