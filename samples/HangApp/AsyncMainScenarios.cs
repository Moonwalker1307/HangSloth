namespace HangApp;

// Entered through Program.cs's await, so these run inside the app's async Main: the runtime's entry point blocks
// the main thread on Main's task for as long as the app runs.

static class AsyncMainShutdownScenario
{
    /// How a console worker waits to be stopped: Main awaits a task that Ctrl+C completes. Healthy.
    public static Task WaitForShutdown()
    {
        var stopped = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopped.TrySetResult();
        };
        Ready.SignalLater();
        return stopped.Task;
    }
}

static class AsyncMainImportScenario
{
    /// A batch job whose async Main awaits an import that waits for an acknowledgement nobody sends.
    public static async Task RunAsync()
    {
        Ready.SignalLater();
        await new NightlyImport().RunAsync();
    }

    private sealed class NightlyImport
    {
        private readonly TaskCompletionSource<bool> _acknowledged = new();

        public async Task RunAsync()
        {
            await Task.Yield();
            await _acknowledged.Task;
        }
    }
}
