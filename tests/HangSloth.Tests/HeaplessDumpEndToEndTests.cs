using HangSloth.Collection;
using Microsoft.Diagnostics.NETCore.Client;

namespace HangSloth.Tests;

/// A real dump without the managed heap (what `dotnet-dump collect --type Mini` writes) of a deadlocked app:
/// HangSloth must say it can't tell and exit with 1, not print the all-clear.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class HeaplessDumpEndToEndTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hangsloth-heapless-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_mini_dump_of_sync_over_async_is_not_reported_as_healthy()
    {
        var mini = Path.Combine(_dir, "orders-mini.dmp");
        var withHeap = Path.Combine(_dir, "orders-heap.dmp");
        Capture(mini, withHeap);

        Assert.False(DumpLoader.LoadDump(mini).HeapAvailable);
        // The same process dumped with its heap is judged complete (a small app still has hundreds of objects).
        Assert.True(DumpLoader.LoadDump(withHeap).HeapAvailable);

        var (exit, stdout, err) = CliTests.Run([mini, "--no-color"]);
        Assert.Equal(1, exit);
        Assert.DoesNotContain("No hang patterns found", stdout);
        Assert.Contains("\n▲ Can't tell: this dump has no managed heap\n", stdout.ReplaceLineEndings("\n"));
        Assert.StartsWith("hangsloth: this dump has no managed heap, so HangSloth cannot tell whether the app is hung.", err);
        // The stacks are still there: the blocked main thread is listed. Its managed id isn't 1 on every OS.
        // Not on Windows, where a mini dump can leave out the JIT's debug info that line numbers come from (see DumpLoader.CollectDump).
        if (!OperatingSystem.IsWindows())
            Assert.Matches(@"\n   Thread \d+ is blocked on \.Result in OrderController\.Get \(OrderController\.cs:10\)\.\n", stdout.ReplaceLineEndings("\n"));

        var (jsonExit, json, _) = CliTests.Run([mini, "--json"]);
        Assert.Equal(1, jsonExit);
        Assert.Contains("\"heapAvailable\": false,", json);
    }

    /// Dumps the deadlocked HangApp 'sync-over-async' twice: without the heap and with it.
    private static void Capture(string mini, string withHeap)
    {
        using var sample = Sample.Start(SampleApp.Build("HangApp"), ["sync-over-async"]);
        sample.WaitUntilReady(TimeSpan.FromSeconds(30));
        var client = new DiagnosticsClient(sample.Process.Id);
        client.WriteDump(DumpType.Normal, mini, logDumpGeneration: false);
        client.WriteDump(DumpType.WithHeap, withHeap, logDumpGeneration: false);
    }
}
