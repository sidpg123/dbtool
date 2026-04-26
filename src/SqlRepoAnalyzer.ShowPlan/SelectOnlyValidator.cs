using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlRepoAnalyzer.Core.Tsql;

namespace SqlRepoAnalyzer.ShowPlan;

/// <summary>
/// SHOWPLAN_XML is only invoked for batches that contain at least one SELECT and no disallowed statement types.
/// </summary>
public static class SelectOnlyValidator
{
    public static bool IsSelectOnly(string sql, out string? rejectReason)
    {
        rejectReason = null;
        var parse = TsqlParser.Parse(sql);
        if (!parse.Success)
        {
            rejectReason = "tsql_parse_error";
            return false;
        }

        if (parse.Fragment is not TSqlScript script)
        {
            rejectReason = "not_tsql_script";
            return false;
        }

        if (script.Batches is null || script.Batches.Count == 0)
        {
            rejectReason = "no_batches";
            return false;
        }

        var hasSelect = false;
        foreach (var batch in script.Batches)
        {
            foreach (var stmt in batch.Statements)
            {
                switch (stmt)
                {
                    case SelectStatement:
                        hasSelect = true;
                        continue;
                    case SetOnOffStatement:
                    case SetTransactionIsolationLevelStatement:
                        continue;
                    default:
                        rejectReason = $"disallowed_statement:{stmt.GetType().Name}";
                        return false;
                }
            }
        }

        if (!hasSelect)
        {
            rejectReason = "no_select";
            return false;
        }

        return true;
    }
}
