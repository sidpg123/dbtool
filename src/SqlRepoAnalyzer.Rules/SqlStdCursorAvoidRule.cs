using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: avoid cursors; prefer set-based logic.
/// </summary>
public sealed class SqlStdCursorAvoidRule : IRule
{
    public string Id => "sql.std.cursor_avoid";

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        if (ctx.Ast is null || !ctx.Parse!.Success) return Array.Empty<Finding>();

        var visitor = new Visitor();
        ctx.Ast.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        public List<Finding> Findings { get; } = new();

        public override void ExplicitVisit(DeclareCursorStatement node)
        {
            AddOnce();
        }

        public override void ExplicitVisit(OpenCursorStatement node)
        {
            AddOnce();
        }

        public override void ExplicitVisit(FetchCursorStatement node)
        {
            AddOnce();
        }

        public override void ExplicitVisit(CloseCursorStatement node)
        {
            AddOnce();
        }

        public override void ExplicitVisit(DeallocateCursorStatement node)
        {
            AddOnce();
        }

        private bool _once;

        private void AddOnce()
        {
            if (_once) return;
            _once = true;
            Findings.Add(new Finding(
                "sql.std.cursor_avoid",
                Severity.Warn,
                Confidence.High,
                "Cursor usage detected; coding standard prefers set-based operations.",
                Suggestion: "Replace with set-based SQL or a controlled WHILE loop only when strictly necessary."));
        }
    }
}
