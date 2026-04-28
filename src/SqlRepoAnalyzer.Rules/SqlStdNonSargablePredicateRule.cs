using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Rules;

/// <summary>
/// Coding standard: scalar/built-in functions and casts in predicates harm SARGability (avoid on indexed columns).
/// Heuristic: flags common function calls inside WHERE/HAVING search conditions.
/// </summary>
public sealed class SqlStdNonSargablePredicateRule : IRule
{
    public string Id => "sql.std.non_sargable_predicate";

    private static readonly HashSet<string> SuspiciousFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "LEFT", "RIGHT", "SUBSTRING", "LOWER", "UPPER", "LTRIM", "RTRIM",
        "YEAR", "MONTH", "DAY", "DATEADD", "DATEDIFF", "DATEPART",
        "ISNULL", "COALESCE", "NULLIF",
        "LEN", "REPLACE", "CHARINDEX", "PATINDEX", "FORMAT",
        "ABS", "ROUND", "FLOOR", "CEILING",
        "CONVERT", "PARSE", "TRY_CONVERT", "TRY_PARSE",
        "NEWID", "NEWSEQUENTIALID",
        "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME"
    };

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
            if (node.WhereClause?.SearchCondition is { } where)
            {
                var scan = new PredicateScanVisitor(SuspiciousFunctions);
                where.Accept(scan);
                foreach (var fn in scan.FunctionNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    Findings.Add(new Finding(
                        "sql.std.non_sargable_predicate",
                        Severity.Warn,
                        Confidence.Low,
                        $"Predicate may contain function `{fn}` which often prevents index seeks (non-SARGable).",
                        Suggestion: "Avoid applying functions to indexed columns in WHERE/JOIN; filter on raw columns or use persisted/computed columns as appropriate."));
                }
            }

            if (node.HavingClause?.SearchCondition is { } having)
            {
                var scan = new PredicateScanVisitor(SuspiciousFunctions);
                having.Accept(scan);
                foreach (var fn in scan.FunctionNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    Findings.Add(new Finding(
                        "sql.std.non_sargable_predicate",
                        Severity.Warn,
                        Confidence.Low,
                        $"HAVING may contain function `{fn}` which can hurt sargability/performance.",
                        Suggestion: "Prefer filtering on raw columns before aggregate when possible."));
                }
            }

            base.ExplicitVisit(node);
        }

        /// <summary>Scan joins for ON clauses.</summary>
        public override void ExplicitVisit(QualifiedJoin node)
        {
            if (node.SearchCondition is { } onCond)
            {
                var scan = new PredicateScanVisitor(SuspiciousFunctions);
                onCond.Accept(scan);
                foreach (var fn in scan.FunctionNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    Findings.Add(new Finding(
                        "sql.std.non_sargable_predicate",
                        Severity.Warn,
                        Confidence.Low,
                        $"Join predicate may contain function `{fn}` which often prevents efficient joins.",
                        Suggestion: "Avoid wrapping join keys with functions; align types and compare base columns."));
                }
            }

            base.ExplicitVisit(node);
        }
    }

    private sealed class PredicateScanVisitor : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _suspicious;
        public List<string> FunctionNames { get; } = new();

        public PredicateScanVisitor(HashSet<string> suspicious) => _suspicious = suspicious;

        public override void ExplicitVisit(FunctionCall node)
        {
            var name = node.FunctionName?.Value;
            if (!string.IsNullOrEmpty(name) && _suspicious.Contains(name))
                FunctionNames.Add(name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CastCall node)
        {
            FunctionNames.Add("CAST");
            base.ExplicitVisit(node);
        }
    }
}
