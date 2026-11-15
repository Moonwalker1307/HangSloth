namespace DeadlockLab.Code;

internal static class HealthyScenario
{
    private static readonly object CounterLock = new();
    private static long _counter;

    public static void Run()
    {
        for (var i = 0; i < 3; i++)
        {
            new Thread(() =>
            {
                while (true)
                {
                    lock (CounterLock) _counter++;
                    Thread.Sleep(20);
                }
            }) { IsBackground = true, Name = $"ticker-{i}" }.Start();
        }
        using var timer = new Timer(_ => { lock (CounterLock) _counter++; }, null, 0, 100);
        var pending = new TaskCompletionSource();
        _ = Task.Run(async () => { await pending.Task; }); // an idle async wait, nobody blocks on it
        Ready.SignalLater();
        Ready.Forever();
    }
}
