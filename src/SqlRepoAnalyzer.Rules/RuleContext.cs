using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlRepoAnalyzer.Core.Queries;
using SqlRepoAnalyzer.Core.Tsql;

namespace SqlRepoAnalyzer.Rules;

public sealed class RuleContext
{
    public required QueryRecord Query { get; init; }
    public TsqlParseResult? Parse { get; init; }
    public TSqlFragment? Ast => Parse?.Fragment;
}
