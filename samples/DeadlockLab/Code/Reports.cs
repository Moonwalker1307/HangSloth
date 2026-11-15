namespace DeadlockLab.Code;

public sealed class ReportService
{
    private readonly object _sync = new();
    private readonly List<string> _rows = ["a", "b", "c"];

    public string Generate()
    {
        lock (_sync)
        {
            // BUG: waits for work that needs the lock this thread is holding.
            var rows = Task.Run(CollectRows);
            return string.Join(",", rows.Result);
        }
    }

    private List<string> CollectRows()
    {
        lock (_sync)
        {
            return [.. _rows];
        }
    }
}

internal static class ReportScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        Console.WriteLine(new ReportService().Generate());
    }
}
