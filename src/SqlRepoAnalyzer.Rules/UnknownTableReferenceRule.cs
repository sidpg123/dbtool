using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

public sealed class UnknownTableReferenceRule : IRule
{
    public string Id => "schema.unknown_table";

    public IReadOnlyList<Finding> Evaluate(RuleContext ctx)
    {
        if (ctx.Schema is null) return Array.Empty<Finding>();
        if (ctx.Ast is null) return Array.Empty<Finding>();

        var visitor = new Visitor(ctx.Schema);
        ctx.Ast.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly SchemaModel _schema;
        public List<Finding> Findings { get; } = new();

        public Visitor(SchemaModel schema) => _schema = schema;

        public override void ExplicitVisit(NamedTableReference node)
        {
            var schema = node.SchemaObject.Identifiers.Count >= 2
                ? node.SchemaObject.Identifiers[^2].Value
                : "dbo";

            var name = node.SchemaObject.Identifiers[^1].Value;

            if (!_schema.TryGetTable(schema, name, out _))
            {
                Findings.Add(new Finding(
                    "schema.unknown_table",
                    Severity.Warn,
                    Confidence.Medium,
                    $"Referenced table not found in schema snapshot: {schema}.{name}",
                    Suggestion: "Verify schema snapshot freshness, default schema (`dbo`), or cross-database references."));
            }
        }
    }
}
