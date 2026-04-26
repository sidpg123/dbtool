using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Core.Tsql;

public static class TsqlParser
{
    public static TsqlParseResult Parse(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        var errorList = ToReadOnly(errors);
        var success = errorList.Count == 0;
        return new TsqlParseResult(success, fragment, errorList);
    }

    private static IReadOnlyList<ParseError> ToReadOnly(IList<ParseError>? errors)
    {
        if (errors is null || errors.Count == 0) return Array.Empty<ParseError>();
        return errors is IReadOnlyList<ParseError> ro ? ro : errors.ToArray();
    }
}
