using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlRepoAnalyzer.ShowPlan;

public sealed record ShowPlanExecutionResult(bool Success, string? Xml, string? ErrorMessage);

public static class ShowPlanExecutor
{
    /// <summary>
    /// Runs SET SHOWPLAN_XML ON, returns estimated showplan XML (statement is not executed for result rows).
    /// Always attempts SET SHOWPLAN_XML OFF in a finally block.
    /// </summary>
    public static async Task<ShowPlanExecutionResult> CaptureShowPlanXmlAsync(
        string connectionString,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var showplanOn = false;
        try
        {
            await using (var setOn = new SqlCommand("SET SHOWPLAN_XML ON;", conn)
                          {
                              CommandTimeout = commandTimeoutSeconds
                          })
            {
                await setOn.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                showplanOn = true;
            }

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeoutSeconds };
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var sb = new StringBuilder();
            var any = false;
            do
            {
                if (!reader.HasRows) continue;

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (reader.FieldCount == 0) continue;
                    var cell = reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString();
                    if (string.IsNullOrWhiteSpace(cell)) continue;
                    if (any) sb.AppendLine();
                    sb.Append(cell);
                    any = true;
                }
            } while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            var xml = sb.ToString();
            if (string.IsNullOrWhiteSpace(xml))
                return new ShowPlanExecutionResult(false, null, "empty_showplan_xml");

            return new ShowPlanExecutionResult(true, xml, null);
        }
        catch (Exception ex)
        {
            return new ShowPlanExecutionResult(false, null, ex.Message);
        }
        finally
        {
            if (showplanOn && conn.State == ConnectionState.Open)
            {
                try
                {
                    await using var setOff = new SqlCommand("SET SHOWPLAN_XML OFF;", conn)
                    {
                        CommandTimeout = commandTimeoutSeconds
                    };
                    await setOff.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }
    }
}
