using Microsoft.Data.SqlClient;

namespace DeadlockLab.SqlServer;

/// SQL Server scenarios need a server: set DEADLOCKLAB_SQLSERVER to a connection string
/// (default Server=localhost;User Id=sa;Password=Lab_Passw0rd!;TrustServerCertificate=true).
internal static class Sql
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("DEADLOCKLAB_SQLSERVER")
        ?? "Server=localhost;User Id=sa;Password=Lab_Passw0rd!;TrustServerCertificate=true;Application Name=DeadlockLab";

    public static SqlConnection Open()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public static int Execute(SqlConnection connection, string sql, SqlTransaction? tx = null)
    {
        using var command = new SqlCommand(sql, connection, tx) { CommandTimeout = 0 };
        return command.ExecuteNonQuery();
    }

    public static void Reset(string table, string columns, string fill)
    {
        using var c = Open();
        Execute(c, $"DROP TABLE IF EXISTS {table}; CREATE TABLE {table} ({columns}); {fill}");
    }
}
