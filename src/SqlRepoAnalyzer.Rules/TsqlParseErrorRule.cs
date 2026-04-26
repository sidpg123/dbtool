using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

public sealed class TsqlParseErrorRule : IRule
{
    public string Id => "tsql.parse_error";

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.Query.SqlText)) return Array.Empty<Finding>();
        if (ctx.Parse is null) return Array.Empty<Finding>();

        if (ctx.Parse.Success) return Array.Empty<Finding>();

        var first = ctx.Parse.Errors.FirstOrDefault();
        var msg = first is null
            ? "T-SQL parse failed."
            : $"T-SQL parse error at line {first.Line}, column {first.Column}: {first.Message}";

        return new[]
        {
            new Finding(
                Id,
                Severity.Warn,
                Confidence.High,
                msg,
                Suggestion: "Fix SQL syntax/dialect issues; suggestions may be incomplete until parsing succeeds.")
        };
    }
}
