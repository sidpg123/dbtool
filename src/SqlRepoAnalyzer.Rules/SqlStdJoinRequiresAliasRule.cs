using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: every object in a query must have an alias except single-object queries.
/// </summary>
public sealed class SqlStdJoinRequiresAliasRule : IRule
{
    public string Id => "sql.std.join_requires_alias";

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

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.FromClause?.TableReferences is not { Count: > 0 } refs)
            {
                base.ExplicitVisit(node);
                return;
            }

            var tables = new List<NamedTableReference>();
            foreach (var tr in refs)
                CollectNamed(tr, tables);

            if (tables.Count < 2)
            {
                base.ExplicitVisit(node);
                return;
            }

            foreach (var t in tables)
            {
                if (t.Alias is null)
                {
                    var name = t.SchemaObject.Identifiers[^1].Value ?? "?";
                    Findings.Add(new Finding(
                        "sql.std.join_requires_alias",
                        Severity.Info,
                        Confidence.Medium,
                        $"Multi-table query: object `{name}` should have a table alias per coding standard.",
                        Suggestion: "Add a short alias and use `alias.column` for all column references."));
                }
            }

            base.ExplicitVisit(node);
        }

        private static void CollectNamed(TableReference? tr, List<NamedTableReference> acc)
        {
            switch (tr)
            {
                case NamedTableReference n:
                    acc.Add(n);
                    return;
                case QualifiedJoin j:
                    CollectNamed(j.FirstTableReference, acc);
                    CollectNamed(j.SecondTableReference, acc);
                    return;
                case JoinParenthesisTableReference p:
                    CollectNamed(p.Join, acc);
                    return;
            }
        }
    }
}
