using Npgsql;

namespace DeadlockLab.Database;

/// A report cache refreshes itself by loading the whole events table into memory, under the
/// cache lock. The SELECT returns two million rows, so the refresh takes minutes, and every request
/// that reads the cache queues behind the lock. Nothing is deadlocked; it's just very, very slow.
public sealed class EventReportCache
{
    private readonly object _cacheLock = new();
    private List<(long Id, string Kind)> _events = [];

    public void Refresh()
    {
        lock (_cacheLock)
        {
            using var connection = Db.Open();
            // BUG: no WHERE, no LIMIT, no paging: the whole history, every time.
            using var command = new NpgsqlCommand("SELECT id, kind FROM lab_page_events", connection);
            using var reader = command.ExecuteReader();
            var rows = new List<(long, string)>();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
                Thread.SpinWait(3000); // "parse and enrich" each event
            }
            _events = rows;
        }
    }

    public int CountKind(string kind)
    {
        lock (_cacheLock) return _events.Count(e => e.Kind == kind);
    }
}

internal static class LargeSelectScenario
{
    public static void Run()
    {
        Db.Reset("lab_page_events", "id bigint primary key, kind text", "(0, 'seed')");
        using (var c = Db.Open())
            Db.Execute(c, "INSERT INTO lab_page_events SELECT g, 'page_view' FROM generate_series(1, 2000000) g");
        var cache = new EventReportCache();
        new Thread(cache.Refresh) { Name = "cache-refresh", IsBackground = true }.Start();
        Thread.Sleep(300);
        for (var i = 0; i < 3; i++)
            new Thread(() => cache.CountKind("page_view")) { Name = $"request-{i}", IsBackground = true }.Start();
        Ready.SignalLater(2000);
        Ready.Forever();
    }
}
