using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Core.Tsql;

public static class TsqlParser
{
    public static TsqlParseResult Parse(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        var success = errors is { Count: 0 };
        return new TsqlParseResult(success, fragment, errors);
    }
}
