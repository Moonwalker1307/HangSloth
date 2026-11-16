namespace HangApp;

// Tasks that are not waiting for anything inside them: one that was never started, one still waiting for a thread
// to run it, and Task.WhenAny of two tasks.

static class NeverStartedTaskScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        // Created, but nothing ever calls Start(): Wait() waits forever.
        var export = new Task(InvoiceExport.WriteFile);
        export.Wait();
    }

    private static class InvoiceExport
    {
        public static void WriteFile() => Console.WriteLine("invoices exported");
    }
}

static class QueuedTaskScenario
{
    public static void Run()
    {
        var printer = new LabelPrinterScheduler();
        var factory = new TaskFactory(printer);
        // The printer's only thread is stuck on the first job, so the second one waits in its queue.
        factory.StartNew(LabelPrinterScheduler.Jam);
        Ready.SignalLater();
        factory.StartNew(LabelPrinterScheduler.PrintLabel).Wait();
    }

    /// Runs its jobs one after another on a thread of its own, like a device that takes one job at a time.
    private sealed class LabelPrinterScheduler : TaskScheduler
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Task> _jobs = [];

        public LabelPrinterScheduler() =>
            new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) TryExecuteTask(job); }) { IsBackground = true, Name = "label-printer" }.Start();

        public static void Jam() => Thread.Sleep(Timeout.Infinite);

        public static void PrintLabel() => Console.WriteLine("label printed");

        protected override void QueueTask(Task task) => _jobs.Add(task);

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => _jobs.ToArray();
    }
}

static class TwoTaskWhenAnyScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var paid = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        // With exactly two tasks, WhenAny uses a promise of its own.
        Task.WhenAny(paid.Task, cancelled.Task).Wait();
    }
}

static class TwoTaskWhenAnyTimeoutScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        var reply = new TaskCompletionSource();
        // The timeout ends the wait in six hours: a slow wait, not a task nobody completes.
        Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromHours(6))).Wait();
    }
}
