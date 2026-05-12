using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: use two-part names [schema].[object] for tables/views.
/// Skips single-part references that match a CTE name in scope (WITH is not schema-qualified).
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
        return visitor.ToFindings();
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        private readonly Stack<HashSet<string>> _cteScopeFrames = new();
        private readonly Dictionary<string, List<(int Line, int Column)>> _tableHits = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<Finding> ToFindings()
        {
            var list = new List<Finding>(_tableHits.Count);
            foreach (var kv in _tableHits.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var (name, positions) = (kv.Key, kv.Value);
                list.Add(new Finding(
                    "sql.std.schema_qualified_object",
                    Severity.Info,
                    Confidence.Medium,
                    $"Table/view reference `{name}` should use two-part naming `[schema].[{name}]` per coding standard.",
                    Suggestion: "Qualify with schema (often `[dbo]`) to avoid wrong-default resolution.",
                    FindingEvidence.FromPositions(positions)));
            }

            return list;
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            var pushed = TryPushCtes(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            if (pushed) _cteScopeFrames.Pop();
        }

        public override void ExplicitVisit(InsertStatement node)
        {
            var pushed = TryPushCtes(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            if (pushed) _cteScopeFrames.Pop();
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var pushed = TryPushCtes(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            if (pushed) _cteScopeFrames.Pop();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var pushed = TryPushCtes(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            if (pushed) _cteScopeFrames.Pop();
        }

        public override void ExplicitVisit(MergeStatement node)
        {
            var pushed = TryPushCtes(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            if (pushed) _cteScopeFrames.Pop();
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            var ids = node.SchemaObject.Identifiers;
            var name = ids[^1].Value ?? "";
            if (TempName.IsMatch(name))
            {
                base.ExplicitVisit(node);
                return;
            }

            if (ids.Count < 2 && !IsLikelyCteReference(name) && ids[^1] is { } lastId)
            {
                var pos = FindingEvidence.Position(lastId);
                if (!_tableHits.TryGetValue(name, out var list))
                {
                    list = new List<(int Line, int Column)>();
                    _tableHits[name] = list;
                }

                list.Add(pos);
            }

            base.ExplicitVisit(node);
        }

        private bool TryPushCtes(WithCtesAndXmlNamespaces? with)
        {
            if (with?.CommonTableExpressions is not { Count: > 0 } list)
                return false;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cte in list)
            {
                var n = cte.ExpressionName?.Value;
                if (!string.IsNullOrWhiteSpace(n))
                    names.Add(n);
            }

            if (names.Count == 0)
                return false;

            _cteScopeFrames.Push(names);
            return true;
        }

        private bool IsLikelyCteReference(string name)
        {
            if (_cteScopeFrames.Count == 0)
                return false;

            foreach (var frame in _cteScopeFrames)
            {
                if (frame.Contains(name))
                    return true;
            }

            return false;
        }
    }
}
