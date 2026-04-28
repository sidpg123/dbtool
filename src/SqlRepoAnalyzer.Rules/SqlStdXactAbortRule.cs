using System.Text.RegularExpressions;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: use SET XACT_ABORT ON for multi-statement transactions (text heuristic).
/// </summary>
public sealed class SqlStdXactAbortRule : IRule
{
    public string Id => "sql.std.xact_abort";

    private static readonly Regex HasTransaction = new(
        @"\b(BEGIN\s+TRAN(SACTION)?|BEGIN\s+DISTRIBUTED\s+TRAN)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HasXactAbortOn = new(
        @"\bSET\s+XACT_ABORT\s+ON\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        var sql = ctx.Query.SqlText;
        if (string.IsNullOrWhiteSpace(sql)) return Array.Empty<Finding>();

        if (!HasTransaction.IsMatch(sql)) return Array.Empty<Finding>();
        if (HasXactAbortOn.IsMatch(sql)) return Array.Empty<Finding>();

        return new[]
        {
            new Finding(
                Id,
                Severity.Info,
                Confidence.Low,
                "Transaction control detected without `SET XACT_ABORT ON`; coding standard recommends enabling it for multi-statement transactions.",
                Suggestion: "Add `SET XACT_ABORT ON` at the start of the batch when using explicit transactions.")
        };
    }
}
