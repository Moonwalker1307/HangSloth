namespace HangApp;

// Blocking on a task that only stands in for other work: Task.Run(async ...), WaitAsync and ContinueWith.
// The task the thread waits on says little; what matters is what the work behind it is waiting for.

static class TaskRunAsyncScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var outbox = new MailOutbox();
        Task.Run(async () => await outbox.FlushAsync()).Wait();
    }

    private sealed class MailOutbox
    {
        private readonly TaskCompletionSource _connected = new();

        public async Task FlushAsync()
        {
            await Task.Delay(10);
            // Waits for a connection that nothing ever reports.
            await _connected.Task;
        }
    }
}

static class TaskRunAsyncResultScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var quote = new TaskCompletionSource<decimal>();
        var total = Task.Run(async () => await quote.Task * 1.19m).Result;
        Console.WriteLine(total);
    }
}

static class WaitAsyncScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var approval = new TaskCompletionSource<bool>();
        // A second continuation on the same task, as logging code would add.
        approval.Task.ContinueWith(t => Console.WriteLine($"approved: {t.Result}"));
        approval.Task.WaitAsync(TimeSpan.FromHours(6)).Wait();
    }
}

static class UnwrapScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var stock = new TaskCompletionSource<int>();
        var count = Task.Factory.StartNew(async () =>
        {
            // Slow work before the first await keeps the outer task running, so Unwrap() returns a promise.
            Thread.Sleep(200);
            return await stock.Task;
        }, TaskCreationOptions.LongRunning).Unwrap().Result;
        Console.WriteLine(count);
    }
}

static class ContinueWithScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var export = new TaskCompletionSource();
        var rows = export.Task.ContinueWith(_ => 250).Result;
        Console.WriteLine(rows);
    }
}

static class LockTaskRunAsyncScenario
{
    public static void Run(bool lockBeforeFirstAwait)
    {
        Ready.SignalLater();
        new StockReport().Generate(lockBeforeFirstAwait);
    }

    /// Holds its lock while blocking on Task.Run(async ...), and the async lambda needs the same lock.
    private sealed class StockReport
    {
        private readonly object _sync = new();
        private int _rows;

        public void Generate(bool lockBeforeFirstAwait)
        {
            lock (_sync)
            {
                if (lockBeforeFirstAwait)
                    Task.Run(async () => { CollectRows(); await Task.Yield(); }).Wait();
                else
                    Task.Run(async () => { await Task.Yield(); CollectRows(); }).Wait();
            }
        }

        private void CollectRows()
        {
            lock (_sync) _rows++;
        }
    }
}

static class SemaphoreTokenScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var printer = new LabelPrinter();
        printer.Reserve();
        printer.PrintAsync().Wait();
    }

    private sealed class LabelPrinter
    {
        private readonly SemaphoreSlim _device = new(1, 1);
        private readonly CancellationTokenSource _shutdown = new();

        /// Takes the device and never gives it back.
        public void Reserve() => _device.Wait();

        public async Task PrintAsync()
        {
            await Task.Delay(10);
            // With a token, WaitAsync waits through a wrapper of its own instead of the queued waiter itself.
            await _device.WaitAsync(_shutdown.Token);
            _device.Release();
        }
    }
}

static class WhenAnyScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var paid = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        var expired = new TaskCompletionSource();
        // With more than two tasks, WhenAny uses a different promise than with exactly two.
        Task.WhenAny(paid.Task, cancelled.Task, expired.Task).Wait();
    }
}

static class WhenAnyTimeoutScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var paid = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        // The timeout ends the wait in six hours: a slow wait, not a task nobody completes.
        Task.WhenAny(paid.Task, cancelled.Task, Task.Delay(TimeSpan.FromHours(6))).Wait();
    }
}

static class WaitAnyTimeoutScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var reply = new TaskCompletionSource();
        Task.WaitAny(reply.Task, Task.Delay(TimeSpan.FromHours(6)));
    }
}

static class TaskRunReturnsTaskScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var receipt = new TaskCompletionSource<int>();
        // Not an async lambda: it only hands back a task that nothing completes.
        Task.Run(() => receipt.Task).Wait();
    }
}

static class ResumedMonitorWaitScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        new PrintSpooler().RunAsync().Wait();
    }

    private sealed class PrintSpooler
    {
        private readonly object _sync = new();

        /// Continues on a pool thread after its first await, and waits there for a Pulse nobody sends.
        public async Task RunAsync()
        {
            await Task.Yield();
            lock (_sync) Monitor.Wait(_sync);
        }
    }
}
