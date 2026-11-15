using Npgsql;

namespace DeadlockLab.Database;

internal static class Db
{
    public static string ConnectionString(string? extra = null)
    {
        var cs = Environment.GetEnvironmentVariable("DEADLOCKLAB_PG")
                 ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";
        var builder = new NpgsqlConnectionStringBuilder(cs) { ApplicationName = "DeadlockLab" };
        if (extra is not null)
        {
            foreach (var part in extra.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                builder[kv[0].Trim()] = kv[1].Trim();
            }
        }
        return builder.ConnectionString;
    }

    public static NpgsqlConnection Open(string? extra = null)
    {
        var connection = new NpgsqlConnection(ConnectionString(extra));
        connection.Open();
        return connection;
    }

    public static int Execute(NpgsqlConnection connection, string sql, NpgsqlTransaction? tx = null)
    {
        using var command = new NpgsqlCommand(sql, connection, tx);
        return command.ExecuteNonQuery();
    }

    /// Recreate a scenario's table. Each scenario has its own table, so scenarios can run side by side.
    public static void Reset(string table, string columns, string rows)
    {
        using var c = Open();
        Execute(c, $"DROP TABLE IF EXISTS {table}; CREATE TABLE {table} ({columns}); INSERT INTO {table} VALUES {rows};");
    }
}
