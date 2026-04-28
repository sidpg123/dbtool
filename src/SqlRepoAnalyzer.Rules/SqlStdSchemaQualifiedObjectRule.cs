using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: use two-part names [schema].[object] for tables/views.
/// </summary>
public sealed class SqlStdSchemaQualifiedObjectRule : IRule
{
    public string Id => "sql.std.schema_qualified_object";

    private static readonly Regex TempName = new(@"^#", RegexOptions.Compiled);

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

        public override void ExplicitVisit(NamedTableReference node)
        {
            var ids = node.SchemaObject.Identifiers;
            var name = ids[^1].Value ?? "";
            if (TempName.IsMatch(name))
                return;

            if (ids.Count < 2)
            {
                Findings.Add(new Finding(
                    "sql.std.schema_qualified_object",
                    Severity.Info,
                    Confidence.Medium,
                    $"Table/view reference `{name}` should use two-part naming `[schema].[{name}]` per coding standard.",
                    Suggestion: "Qualify with schema (often `[dbo]`) to avoid wrong-default resolution."));
            }

            base.ExplicitVisit(node);
        }
    }
}
