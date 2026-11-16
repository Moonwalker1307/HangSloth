using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Exit codes for a dump without the managed heap: "could not tell" is 1, like a failed check, and never 0.
[Collection("Cli")]
public class CliHeaplessDumpTests
{
    private const string NoHeapError = "hangsloth: this dump has no managed heap, so HangSloth cannot tell whether the app is hung. Take a full dump (hangsloth <pid>, dotnet-dump collect --type Full or createdump --full).";

    private static ProcessSnapshot BlockedMain(bool heap) => Process(
        Thread(1, [.. ResultWait, App("MyApp.OrderController", "Get", "OrderController.cs", 10)])) with { HeapAvailable = heap };

    private static ProcessSnapshot LockDeadlock() => Process(
        Thread(1, [.. MonitorEnter, App("MyApp.Ledger", "Post", "Ledger.cs", 20)]) with { WaitingForLock = 0x200 },
        Thread(2, [.. MonitorEnter, App("MyApp.Ledger", "Close", "Ledger.cs", 40)]) with { WaitingForLock = 0x100 }) with
    {
        HeapAvailable = false,
        Locks =
        [
            new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Ledger._entries", OwnerThreadId = 1, WaiterThreadIds = [2] },
            new MonitorLock { Object = new ObjectRef(0x200, "System.Object"), FieldName = "Ledger._totals", OwnerThreadId = 2, WaiterThreadIds = [1] },
        ],
    };

    private sealed class ThrowingDetector : IDetector
    {
        public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void A_heapless_dump_with_nothing_found_exits_with_1()
    {
        var (exit, stdout, err) = CliTests.Run(["mini.dmp", "--no-color"], (_, _) => BlockedMain(heap: false));
        Assert.Equal(1, exit);
        Assert.Contains("Can't tell: this dump has no managed heap", stdout);
        Assert.DoesNotContain("No hang patterns found", stdout);
        Assert.Equal(NoHeapError, err.TrimEnd());
    }

    [Fact]
    public void A_heapless_dump_as_json_also_exits_with_1()
    {
        var (exit, stdout, err) = CliTests.Run(["mini.dmp", "--json"], (_, _) => BlockedMain(heap: false));
        Assert.Equal(1, exit);
        Assert.Contains("\"heapAvailable\": false,", stdout);
        Assert.Equal(NoHeapError, err.TrimEnd());
    }

    [Fact]
    public void The_same_snapshot_with_the_heap_exits_with_0()
    {
        var (exit, stdout, err) = CliTests.Run(["full.dmp", "--no-color"], (_, _) => BlockedMain(heap: true));
        Assert.Equal(0, exit);
        Assert.Contains("No hang patterns found", stdout);
        Assert.Equal("", err);
    }

    [Fact]
    public void A_heapless_dump_with_a_finding_exits_with_2()
    {
        var (exit, stdout, err) = CliTests.Run(["mini.dmp", "--no-color"], (_, _) => LockDeadlock());
        Assert.Equal(2, exit);
        Assert.Contains("This dump has no managed heap", stdout);
        Assert.Contains("Ledger._entries", stdout);
        Assert.Equal("", err);
    }

    [Fact]
    public void A_heapless_dump_and_a_failed_check_say_both()
    {
        var (exit, _, err) = CliTests.Run(["mini.dmp", "--no-color"], (_, _) => BlockedMain(heap: false), () => new HangAnalyzer([new ThrowingDetector()]));
        Assert.Equal(1, exit);
        Assert.Equal(
            [NoHeapError, "hangsloth: the ThrowingDetector check failed too."],
            err.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')));
    }

    [Fact]
    public void A_dump_taken_mid_gc_exits_with_1_and_asks_for_another_dump()
    {
        var (exit, stdout, err) = CliTests.Run(["full.dmp", "--no-color"],
            (_, _) => BlockedMain(heap: false) with { Warnings = [Collection.SnapshotBuilder.HeapNotWalkableWarning] });
        Assert.Equal(1, exit);
        Assert.Contains("Can't tell: the managed heap in this dump could not be read", stdout);
        Assert.Equal("hangsloth: the managed heap in this dump could not be read, so HangSloth cannot tell whether the app is hung. Take another dump a moment later.", err.TrimEnd());
    }

    [Fact]
    public void The_usage_text_and_the_readme_give_the_same_exit_codes()
    {
        var (_, stdout, _) = CliTests.Run(["--help"]);
        const string line2 = "1 = error, or nothing found but the dump has no managed heap or a check failed.";
        Assert.Contains(line2, stdout);
        var readme = File.ReadAllText(Path.Combine(SampleApp.RepoRoot(), "README.md"));
        Assert.Contains("            " + line2, readme);
    }
}
