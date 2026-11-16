using Xunit.Sdk;

namespace DeadlockLab.Code;

/// A test run caught while two tests wait for a slow stock feed, blocking on it with .Result. xunit runs each test
/// under its AsyncTestSyncContext (the real one, from xunit's own package), which only keeps track of the test's async
/// work and posts every continuation to the thread pool, not back to the test's thread: slow, not deadlocked.
internal static class TestRunScenario
{
    public static void Run()
    {
        var feed = new StockFeed();
        RunTest("stock-feed-test", () => feed.ReadAllAsync().Result);
        RunTest("stock-feed-no-context-test", () => feed.ReadAllWithoutContextAsync().Result);
        Ready.SignalLater();
        Ready.Forever();
    }

    private static void RunTest(string name, Func<int> test) =>
        new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new AsyncTestSyncContext(null));
            Console.WriteLine($"{name}: {test()} items");
        }) { IsBackground = true, Name = name }.Start();

    private sealed class StockFeed
    {
        /// Answers after ten minutes: slow, but it does answer.
        public async Task<int> ReadAllAsync()
        {
            await Task.Delay(TimeSpan.FromMinutes(10));
            return 3;
        }

        public async Task<int> ReadAllWithoutContextAsync()
        {
            await Task.Delay(TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            return 3;
        }
    }
}
