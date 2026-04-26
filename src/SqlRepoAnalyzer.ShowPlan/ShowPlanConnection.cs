using Microsoft.Data.SqlClient;

namespace SqlRepoAnalyzer.ShowPlan;

public static class ShowPlanConnection
{
    /// <summary>Safe for logs: server + database only.</summary>
    public static string Describe(string connectionString)
    {
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            return $"DataSource={b.DataSource};InitialCatalog={b.InitialCatalog ?? "(default)"};";
        }
        catch
        {
            return "(connection string not parseable)";
        }
    }
}
