using DeadlockLab.Http;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DeadlockLab.Database;

/// Startup opens the first database connection to a server that accepts the TCP connection and then never
/// answers (a server stuck while starting, a proxy that swallows traffic). The pool is empty, not full:
/// the thread waits for its own new connection to be established. These need no database: a silent local
/// listener stands in for the server.
public static class StartupCheck
{
    public static void OpenPostgres(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open(); // BUG: Timeout=0 waits forever for a server that doesn't answer
    }

    public static void OpenSqlServer(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open(); // BUG: Connect Timeout=0 waits forever for a server that doesn't answer
    }
}

internal static class DbConnectStallScenario
{
    public static void Run()
    {
        var server = new LabServer(LabServer.Mode.Silent);
        Ready.SignalLater(1000);
        StartupCheck.OpenPostgres($"Host=127.0.0.1;Port={server.Port};Username=orders;Password=orders;Database=orders;Timeout=0");
    }
}

internal static class SqlConnectStallScenario
{
    public static void Run()
    {
        var server = new LabServer(LabServer.Mode.Silent);
        Ready.SignalLater(1000);
        StartupCheck.OpenSqlServer($"Data Source=127.0.0.1,{server.Port};User Id=orders;Password=orders;Connect Timeout=0;Encrypt=false");
    }
}
