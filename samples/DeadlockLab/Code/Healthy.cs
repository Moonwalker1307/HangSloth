using System.Collections.Concurrent;

namespace DeadlockLab.Code;

internal static class HealthyScenario
{
    private static readonly object CounterLock = new();
    private static long _counter;
    private static readonly BlockingCollection<string> MailQueue = new();
    private static readonly BlockingCollection<int> InvoiceQueue = new(boundedCapacity: 10);

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
        // Idle consumers: nothing has been queued yet, so they wait for work. That's normal, not a hang.
        new Thread(() => { foreach (var mail in MailQueue.GetConsumingEnumerable()) Console.WriteLine(mail); }) { IsBackground = true, Name = "mail-sender" }.Start();
        new Thread(() => { while (true) Console.WriteLine(InvoiceQueue.Take()); }) { IsBackground = true, Name = "invoice-printer" }.Start();
        using var timer = new Timer(_ => { lock (CounterLock) _counter++; }, null, 0, 100);
        var pending = new TaskCompletionSource();
        _ = Task.Run(async () => { await pending.Task; }); // an idle async wait, nobody blocks on it
        Ready.SignalLater();
        Ready.Forever();
    }
}
