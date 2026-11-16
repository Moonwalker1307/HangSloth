using System.Text.RegularExpressions;
using DeadlockLab.Database;
using DeadlockLab.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DeadlockLab;

/// `DeadlockLab --lab-db postgres|sqlserver check|create <name>|drop <name>|list`, for the end-to-end tests: whether
/// the lab's own connection settings (DEADLOCKLAB_PG, DEADLOCKLAB_SQLSERVER or the defaults) log in, and a database
/// of their own for each test run, so that two runs on one server don't wait for each other's locks.
internal static partial class LabDatabases
{
    /// Databases made for test runs; `list` shows only these, and only these can be dropped.
    public const string Prefix = "hangsloth_run_";

    public static int Run(string kind, string command, string? name)
    {
        if (kind is not ("postgres" or "sqlserver") || command is not ("check" or "create" or "drop" or "list")
            || (command is "create" or "drop") != (name is not null)
            || (name is not null && !SafeName().IsMatch(name)))
        {
            Console.Error.WriteLine($"usage: DeadlockLab --lab-db postgres|sqlserver check|create <name>|drop <name>|list (names start with {Prefix})");
            return 2;
        }
        try
        {
            if (kind == "postgres") Postgres(command, name);
            else SqlServer(command, name);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
            return 1;
        }
    }

    private static void Postgres(string command, string? name)
    {
        using var connection = Db.Open("Timeout=10;Command Timeout=60;Pooling=false");
        switch (command)
        {
            case "check":
                Db.Execute(connection, "SELECT 1");
                break;
            case "create":
                Db.Execute(connection, $"CREATE DATABASE \"{name}\"");
                Console.WriteLine(new NpgsqlConnectionStringBuilder(Db.ConnectionString()) { Database = name }.ConnectionString);
                break;
            case "drop":
                Db.Execute(connection, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
                break;
            case "list":
                using (var list = new NpgsqlCommand($"SELECT datname FROM pg_database WHERE starts_with(datname, '{Prefix}')", connection))
                using (var reader = list.ExecuteReader())
                    while (reader.Read()) Console.WriteLine(reader.GetString(0));
                break;
        }
    }

    private static void SqlServer(string command, string? name)
    {
        var settings = new SqlConnectionStringBuilder(Sql.ConnectionString) { ConnectTimeout = 10, Pooling = false };
        using var connection = new SqlConnection(settings.ConnectionString);
        connection.Open();
        switch (command)
        {
            case "check":
                Sql.Execute(connection, "SELECT 1");
                break;
            case "create":
                Sql.Execute(connection, $"CREATE DATABASE [{name}]");
                Console.WriteLine(new SqlConnectionStringBuilder(Sql.ConnectionString) { InitialCatalog = name! }.ConnectionString);
                break;
            case "drop":
                Sql.Execute(connection, $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
                break;
            case "list":
                using (var list = new SqlCommand($"SELECT name FROM sys.databases WHERE name LIKE '{Prefix.Replace("_", "[_]")}%'", connection))
                using (var reader = list.ExecuteReader())
                    while (reader.Read()) Console.WriteLine(reader.GetString(0));
                break;
        }
    }

    [GeneratedRegex($"^{Prefix}[a-z0-9_]+$")]
    private static partial Regex SafeName();
}
