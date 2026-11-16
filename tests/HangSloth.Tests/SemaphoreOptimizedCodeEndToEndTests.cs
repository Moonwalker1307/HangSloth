using HangSloth.Analysis;
using HangSloth.Model;

namespace HangSloth.Tests;

/// The semaphore verdicts on real dumps of optimized code, as a process that has run for hours has it: with
/// DOTNET_TieredCompilation=0 the JIT optimizes from the first call and inlines small methods (a lock class's Enter(), a
/// constructor), and ClrMD shows only the frames left. The proven verdicts stay, and what can't be followed falls back
/// to a hedged one instead of to nothing.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class SemaphoreOptimizedCodeEndToEndTests
{
    private static readonly Dictionary<string, string?> Optimized = new() { ["DOTNET_TieredCompilation"] = "0" };

    private static AnalysisReport Lab(string scenario, Func<AnalysisReport, bool>? until = null, bool optimized = true) =>
        SampleApp.Analyze(scenario, project: "DeadlockLab", until: until, environment: optimized ? Optimized : null);

    private static string Label(AnalysisReport report, string thread) => report.Snapshot.Threads.Single(t => t.Name == thread).Label;

    private const string NoOwnerOf = "A SemaphoreSlim has no owner: whoever took the slot of {0} has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will.";

    [Fact]
    public void Semaphores_taken_in_opposite_order_are_still_a_deadlock()
    {
        var report = Lab("semaphore-order", until: r => r.Findings.Any(f => f.Id == "semaphore-deadlock"));

        var f = Assert.Single(report.Findings, f => f.Id == "semaphore-deadlock");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
        Assert.Contains($"{Label(report, "label-worker")} took ShippingService.LabelPrinter earlier in ShippingService.PrintThenBook (Semaphores.cs:13) and doesn't release it before this wait.", f.Details);
        Assert.Contains($"{Label(report, "carrier-worker")} took ShippingService.CarrierApi earlier in ShippingService.BookThenPrint (Semaphores.cs:25) and doesn't release it before this wait.", f.Details);
    }

    [Fact]
    public void Two_objects_of_one_class_taking_each_others_semaphore_are_still_a_deadlock()
    {
        var report = Lab("semaphore-transfer", until: r => r.Findings.Any(f => f.Id == "semaphore-deadlock"));

        var f = Assert.Single(report.Findings, f => f.Id == "semaphore-deadlock");
        Assert.Equal(Severity.Critical, f.Severity);
        // Both semaphores are Warehouse.Lock: each warehouse's name tells them apart.
        foreach (var (name, own, other) in new[] { ("move north->south", "north", "south"), ("move south->north", "south", "north") })
        {
            Assert.Contains($"{Label(report, name)} took Warehouse.Lock (Name = \"{own}\") earlier in Warehouse.MoveTo (StockTransfer.cs:15) and doesn't release it before this wait.", f.Details);
            Assert.Contains($"{Label(report, name)} is waiting in Warehouse.MoveTo (StockTransfer.cs:19) for Warehouse.Lock (Name = \"{other}\") (0 of 1 free).", f.Details);
        }
        Assert.DoesNotContain(report.Findings, x => x.Id == "semaphore-wait");
    }

    [Fact]
    public void An_async_cache_that_takes_its_gate_twice_is_still_a_deadlock()
    {
        var report = Lab("async-semaphore-reentry");

        var f = Assert.Single(report.Findings, f => f.Id == "semaphore-wait");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: async code waiting for a SemaphoreSlim it already holds", f.Title);
        Assert.Contains("PriceCache.GetAsync took PriceCache._gate earlier (Semaphores.cs:57) and doesn't release it before the await it is paused at.", f.Details);
    }

    [Fact]
    public void A_throttle_whose_calls_nest_is_still_a_deadlock()
    {
        var report = Lab("throttle-reentry", until: r => r.Findings.Any(f => f.Severity == Severity.Critical));

        var f = Assert.Single(report.Findings, f => f.Id == "semaphore-wait");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Contains("PartnerApi.GetOrderAsync took PartnerApi._throttle earlier (Throttle.cs:12) and doesn't release it before the await it is paused at.", f.Details);
        Assert.Contains(f.Details, d => d.StartsWith("PartnerApi._throttle has 3 slots and 3 async callers waiting for one.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lock_that_is_never_released_is_still_a_warning()
    {
        var report = Lab("semaphore-leak");

        var f = Assert.Single(report.Findings);
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("2 threads are waiting in InvoiceExport.Run (InvoiceExport.cs:17) for InvoiceExport._exportLock, a SemaphoreSlim with 0 of 1 slots free. " +
                     "No other thread is running app code that could release it.", f.Summary);
        Assert.Equal([string.Format(NoOwnerOf, "InvoiceExport._exportLock")], f.Details);
    }

    [Fact]
    public void A_call_queued_behind_a_slow_holder_is_still_a_plain_wait()
    {
        var report = Lab("semaphore-slow-holder");

        var f = Assert.Single(report.Findings);
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal([string.Format(NoOwnerOf, "TariffCache._gate")], f.Details);
    }

    [Fact]
    public void A_wait_with_a_timeout_that_failed_still_took_nothing()
    {
        var report = Lab("semaphore-fast-path");

        var f = Assert.Single(report.Findings);
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal([string.Format(NoOwnerOf, "FreightQuotes._gate")], f.Details);
        var main = report.Snapshot.Threads.Single(t => t.ManagedId == Assert.Single(f.ThreadIds));
        Assert.Empty(main.SemaphoreUse!.Holds);
    }

    [Fact]
    public void Workers_waiting_for_their_wake_up_signal_are_still_idle()
    {
        // The flushers' constructor is inlined into the scenario, so the JIT never compiled it on its own; how the
        // semaphores were made is read from the module's image in the dump.
        var report = Lab("healthy-signals");

        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("semaphore", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
        foreach (var name in new[] { "invoice-flusher", "receipt-flusher", "reminder-mailer" })
        {
            var worker = report.Snapshot.Threads.Single(t => t.Name == name);
            Assert.Equal(0, report.Snapshot.FindSemaphore(worker.WaitingForSemaphore!.Value)!.InitialCount);
        }
    }

    [Fact]
    public void Locks_taken_in_opposite_order_through_an_inlined_lock_class_are_a_possible_deadlock()
    {
        var report = Lab("semaphore-lock-class");

        var f = Assert.Single(report.Findings);
        Assert.Equal("semaphore-deadlock", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Possible deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
        var picking = Label(report, "picking");
        var packing = Label(report, "packing");
        // The order of the two threads follows the order of the semaphores on the heap.
        Assert.True(f.Summary.Contains($"the code that waits in {picking} and {packing} couldn't be followed (for example a helper method the runtime inlined).", StringComparison.Ordinal)
                    || f.Summary.Contains($"the code that waits in {packing} and {picking} couldn't be followed (for example a helper method the runtime inlined).", StringComparison.Ordinal), f.Summary);
        Assert.Contains(f.Details, d => d.StartsWith($"{picking} is waiting in DispatchDesk.Pick (LockClass.cs:26) for ", StringComparison.Ordinal));
        Assert.Contains(f.Details, d => d.StartsWith($"{packing} is waiting in DispatchDesk.Pack (LockClass.cs:38) for ", StringComparison.Ordinal));
        Assert.Equal("Workers that each wait for a signal of their own, a SemaphoreSlim created with no free slot, look the same in a dump.", f.Details[^1]);
        Assert.All(f.ThreadIds, id => Assert.Null(report.Snapshot.FindThread(id)!.SemaphoreUse));
    }

    [Fact]
    public void Locks_taken_through_a_lock_class_in_cold_code_are_two_waits_in_Enter()
    {
        // Enter() is a frame of its own here: its code waits and took nothing, so this is no proof either way.
        var report = Lab("semaphore-lock-class", optimized: false);

        Assert.DoesNotContain(report.Findings, f => f.Id == "semaphore-deadlock");
        var waits = report.Findings.Where(f => f.Id == "semaphore-wait").ToList();
        Assert.Equal(2, waits.Count);
        Assert.All(waits, f => Assert.Equal(Severity.Warning, f.Severity));
        Assert.All(waits, f => Assert.Contains("waiting in SlotLock.Enter (LockClass.cs:9) for SlotLock._slot", f.Summary));
        Assert.All(waits, f => Assert.Equal([string.Format(NoOwnerOf, "SlotLock._slot")], f.Details));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Locks_taken_in_opposite_order_through_a_lock_class_handed_its_semaphore_are_a_possible_deadlock(bool optimized)
    {
        // Cold code waits in Enter(), which took nothing, on a semaphore whose creation isn't in the class; optimized code
        // inlines Enter() into the services. Neither shows the role, so neither is two plain waits nor nothing.
        var report = Lab("semaphore-injected-lock", until: r => r.Findings.Any(f => f.Id == "semaphore-deadlock"), optimized: optimized);

        var f = Assert.Single(report.Findings);
        Assert.Equal("semaphore-deadlock", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Possible deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
        Assert.Equal(2, f.ThreadIds.Count);
        Assert.DoesNotContain(f.Details, d => d.Contains(" earlier in ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lock_taken_inside_an_if_that_did_not_run_is_not_held()
    {
        // The plain save's `if (exclusive) _tableLock.Wait();` didn't run, so it holds nothing: two waits, no circle.
        var report = Lab("semaphore-optional-lock", until: r => r.Findings.Count(f => f.Id == "semaphore-wait") == 2);

        Assert.DoesNotContain(report.Findings, f => f.Id == "semaphore-deadlock");
        Assert.DoesNotContain(report.Findings, f => f.Details.Any(d => d.Contains(" earlier in ", StringComparison.Ordinal)));
        var save = report.Snapshot.Threads.Single(t => t.Name == "save");
        Assert.Empty(save.SemaphoreUse?.Holds ?? []);
        Assert.All(report.Findings.Where(f => f.Id == "semaphore-wait"), f => Assert.Equal(Severity.Warning, f.Severity));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Moves_queued_behind_two_stocktakes_are_no_deadlock(bool optimized)
    {
        // Optimized code keeps only the dispatch bay alive on the frame of the move. Read as `this`, its gate, the one
        // the thread waits for, would pass for the gate the move took, and two moves into one bay for a circle. Two of
        // the moves take the dispatch bay as a parameter of its own class, which is derived from the loading bay's.
        var report = Lab("semaphore-queued-moves", until: r => r.Findings.Count(f => f.Id == "semaphore-wait") == 2, optimized: optimized);

        Assert.DoesNotContain(report.Findings, f => f.Id == "semaphore-deadlock");
        var waits = report.Findings.Where(f => f.Id == "semaphore-wait").ToList();
        Assert.Equal(2, waits.Count);
        Assert.All(waits, f => Assert.Equal(Severity.Warning, f.Severity));
        Assert.All(waits, f => Assert.Equal(2, f.ThreadIds.Count));
        var moves = report.Snapshot.Threads.Where(t => t.Name?.StartsWith("move-", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(4, moves.Count);
        Assert.All(moves, move => Assert.DoesNotContain(move.SemaphoreUse?.Holds ?? [], h => h.Address == move.WaitingForSemaphore));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_lock_given_back_through_an_extension_method_is_not_held(bool optimized)
    {
        var report = Lab("semaphore-release-helper", until: r => r.Findings.Count(f => f.Id == "semaphore-wait") == 2, optimized: optimized);

        Assert.DoesNotContain(report.Findings, f => f.Id == "semaphore-deadlock");
        Assert.DoesNotContain(report.Findings, f => f.Details.Any(d => d.Contains(" earlier in ", StringComparison.Ordinal)));
        foreach (var name in new[] { "counting", "packing" })
            Assert.Empty(report.Snapshot.Threads.Single(t => t.Name == name).SemaphoreUse?.Holds ?? []);
        Assert.All(report.Findings.Where(f => f.Id == "semaphore-wait"), f => Assert.Equal(Severity.Warning, f.Severity));
    }
}
