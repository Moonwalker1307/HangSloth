using HangSloth.Analysis;
using HangSloth.Model;

namespace HangSloth.Tests;

/// The semaphore verdicts on real dumps of samples/DeadlockLab: what the waiting code shows (read from the IL in the dump)
/// decides between a deadlock, a plain wait and an idle worker, and the sentences say no more than that.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class SemaphoreVerdictEndToEndTests
{
    private static AnalysisReport Lab(string scenario, Func<AnalysisReport, bool>? until = null) =>
        SampleApp.Analyze(scenario, project: "DeadlockLab", until: until);

    private static string Label(AnalysisReport report, string thread) => report.Snapshot.Threads.Single(t => t.Name == thread).Label;

    [Fact]
    public void Workers_waiting_for_their_wake_up_signal_are_idle()
    {
        var report = Lab("healthy-signals");

        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("semaphore", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
        // What told them apart from a taken lock: each waits in a loop, never releases its semaphore, and the semaphore
        // was created with no free slot.
        foreach (var name in new[] { "invoice-flusher", "receipt-flusher", "reminder-mailer" })
        {
            var worker = report.Snapshot.Threads.Single(t => t.Name == name);
            var use = Assert.IsType<SemaphoreUse>(worker.SemaphoreUse);
            Assert.True(use.WaitsInLoop, name);
            Assert.False(use.ReleasesIt, name);
            Assert.Empty(use.Holds);
            Assert.Equal(0, report.Snapshot.FindSemaphore(worker.WaitingForSemaphore!.Value)!.InitialCount);
        }
    }

    [Fact]
    public void Semaphores_taken_in_opposite_order_are_a_deadlock_because_each_thread_took_the_other_one()
    {
        var report = Lab("semaphore-order", until: r => r.Findings.Any(f => f.Id == "semaphore-deadlock"));

        var f = Assert.Single(report.Findings, f => f.Id == "semaphore-deadlock");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
        Assert.Contains($"{Label(report, "label-worker")} took ShippingService.LabelPrinter earlier in ShippingService.PrintThenBook (Semaphores.cs:13) and doesn't release it before this wait.", f.Details);
        Assert.Contains($"{Label(report, "carrier-worker")} took ShippingService.CarrierApi earlier in ShippingService.BookThenPrint (Semaphores.cs:25) and doesn't release it before this wait.", f.Details);
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
    public void A_lock_that_is_never_released_is_a_warning_about_what_is_known()
    {
        var report = Lab("semaphore-leak");

        var f = Assert.Single(report.Findings);
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal("2 threads are waiting in InvoiceExport.Run (InvoiceExport.cs:17) for InvoiceExport._exportLock, a SemaphoreSlim with 0 of 1 slots free. " +
                     "No other thread is running app code that could release it.", f.Summary);
        Assert.Equal(["A SemaphoreSlim has no owner: whoever took the slot of InvoiceExport._exportLock has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will."], f.Details);
    }

    [Fact]
    public void A_call_queued_behind_a_slow_holder_is_not_called_a_reentry()
    {
        var report = Lab("semaphore-slow-holder");

        var f = Assert.Single(report.Findings);
        // The main thread's number differs between Linux and Windows.
        var main = report.Snapshot.Threads.Single(t => t.Frames.Any(fr => fr.TypeName.EndsWith(".TariffCacheScenario", StringComparison.Ordinal) && fr.MethodName == "Run")).Label;
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal($"{main} is blocked in .Result at TariffCache.cs:49, waiting for TariffCache.GetAsync (paused at TariffCache.cs:14), which awaits TariffCache.LoadAsync (paused at TariffCache.cs:19), " +
                     "which is waiting to enter TariffCache._gate (SemaphoreSlim.WaitAsync(), 0 of 1 slots free).", f.Summary);
        // GetAsync runs on the object that owns the gate, but its code never takes it: no "if it took" hint, no re-entry advice.
        Assert.Equal(["A SemaphoreSlim has no owner: whoever took the slot of TariffCache._gate has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will."], f.Details);
        Assert.Equal("Find the code that took TariffCache._gate and make sure it calls Release() on every path (try/finally), and that it isn't waiting for this chain while holding it.", f.Advice);
    }

    [Fact]
    public void Two_objects_of_one_class_taking_each_others_semaphore_are_a_deadlock()
    {
        // MoveTo's frame keeps both warehouses alive; the wait on other.Lock tells which one is `this`.
        var report = Lab("semaphore-transfer", until: r => r.Findings.Any(f => f.Id == "semaphore-deadlock"));

        var f = Assert.Single(report.Findings, f => f.Id == "semaphore-deadlock");
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
        // Both semaphores are Warehouse.Lock: each warehouse's name tells them apart.
        foreach (var (name, own, other) in new[] { ("move north->south", "north", "south"), ("move south->north", "south", "north") })
        {
            Assert.Contains($"{Label(report, name)} took Warehouse.Lock (Name = \"{own}\") earlier in Warehouse.MoveTo (StockTransfer.cs:15) and doesn't release it before this wait.", f.Details);
            Assert.Contains($"{Label(report, name)} is waiting in Warehouse.MoveTo (StockTransfer.cs:19) for Warehouse.Lock (Name = \"{other}\") (0 of 1 free).", f.Details);
        }
        Assert.DoesNotContain(report.Findings, x => x.Id == "semaphore-wait");
    }

    [Fact]
    public void A_wait_with_a_timeout_that_failed_took_nothing()
    {
        var report = Lab("semaphore-fast-path");

        var f = Assert.Single(report.Findings);
        // The main thread's number differs between Linux and Windows.
        var main = report.Snapshot.Threads.Single(t => t.Frames.Any(fr => fr.TypeName.EndsWith(".FreightQuotesScenario", StringComparison.Ordinal) && fr.MethodName == "Run")).Label;
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal($"{main} is blocked in .Result at FreightQuotes.cs:51, waiting for FreightQuotes.GetAsync (paused at FreightQuotes.cs:13), which awaits FreightQuotes.GetQueuedAsync (paused at FreightQuotes.cs:26), " +
                     "which is waiting to enter FreightQuotes._gate (SemaphoreSlim.WaitAsync(), 0 of 1 slots free).", f.Summary);
        // GetAsync's WaitAsync(0) returned false, and it awaits the slow path only then: it doesn't hold the gate.
        Assert.Equal(["A SemaphoreSlim has no owner: whoever took the slot of FreightQuotes._gate has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will."], f.Details);
        Assert.Empty(report.Snapshot.Threads.Single(t => t.Label == main).SemaphoreUse!.Holds);
    }
}
