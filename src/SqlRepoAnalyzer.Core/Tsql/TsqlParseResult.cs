using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Core.Tsql;

public sealed record TsqlParseResult(
    bool Success,
    TSqlFragment? Fragment,
    IReadOnlyList<ParseError> Errors
);
