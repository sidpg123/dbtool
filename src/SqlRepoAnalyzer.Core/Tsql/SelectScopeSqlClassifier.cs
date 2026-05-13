using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlRepoAnalyzer.Core.Tsql;

/// <summary>
/// Decides whether extracted SQL is suitable for <c>--query-scope select</c> inventory (read-oriented batches).
/// </summary>
public static class SelectScopeSqlClassifier
{
    /// <summary>
    /// True when the script parses and every top-level statement is allowed (SELECT, DECLARE variable, SET options, BEGIN…END of allowed statements) and at least one SELECT exists.
    /// </summary>
    public static bool IsSelectOnlyInventoryCandidate(string sql)
    {
        var parse = TsqlParser.Parse(sql);
        if (!parse.Success || parse.Fragment is not TSqlScript script)
            return false;
        if (script.Batches is null || script.Batches.Count == 0)
            return false;

        var hasSelect = false;
        foreach (var batch in script.Batches)
        {
            if (batch.Statements is null || batch.Statements.Count == 0)
                continue;

            foreach (var stmt in batch.Statements)
            {
                switch (stmt)
                {
                    case SelectStatement:
                        hasSelect = true;
                        break;
                    case DeclareVariableStatement:
                        break;
                    case SetOnOffStatement:
                    case SetTransactionIsolationLevelStatement:
                    case SetTextSizeStatement:
                    case SetErrorLevelStatement:
                    case SetVariableStatement:
                        break;
                    case BeginEndBlockStatement be:
                        if (be.StatementList?.Statements is null || be.StatementList.Statements.Count == 0)
                            return false;
                        if (!StatementsAreSelectInventory(be.StatementList.Statements, ref hasSelect))
                            return false;
                        break;
                    default:
                        return false;
                }
            }
        }

        return hasSelect;
    }

    private static bool StatementsAreSelectInventory(IList<TSqlStatement> statements, ref bool hasSelect)
    {
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case SelectStatement:
                    hasSelect = true;
                    break;
                case DeclareVariableStatement:
                    break;
                case SetOnOffStatement:
                case SetTransactionIsolationLevelStatement:
                case SetTextSizeStatement:
                case SetErrorLevelStatement:
                case SetVariableStatement:
                    break;
                case BeginEndBlockStatement be:
                    if (be.StatementList?.Statements is null || be.StatementList.Statements.Count == 0)
                        return false;
                    if (!StatementsAreSelectInventory(be.StatementList.Statements, ref hasSelect))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
