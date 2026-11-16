using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// A SemaphoreSlim has no owner, so a dump alone can't say who holds one. "Deadlock", "opposite order" and "already
/// holds" are said only when the waiting code shows it took the semaphore (SemaphoreUse.Holds); a worker waiting in a
/// loop on a semaphore made as a signal is idle; and without either, the wait is described for what it is.
public class SemaphoreVerdictTests
{
    private static readonly StackFrameInfo[] SemaphoreWait =
        [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "WaitCore"), Fx("System.Threading.SemaphoreSlim", "Wait")];

    private static readonly ThreadSnapshot SleepingMain = Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 9)]);

    private static SemaphoreInfo Sem(ulong address, string name, int max = 1, int? initial = null, params int[] waiters) =>
        new() { Object = new ObjectRef(address, "System.Threading.SemaphoreSlim"), FieldName = name, CurrentCount = 0, MaxCount = max, InitialCount = initial, WaiterThreadIds = waiters };

    private static ThreadSnapshot Waiter(int id, string name, string type, string method, string file, int line, ulong semaphore, SemaphoreUse? use) =>
        Thread(id, [.. SemaphoreWait, App(type, method, file, line)], name) with { WaitingForSemaphore = semaphore, SemaphoreUse = use };

    private static SemaphoreUse Holds(ulong semaphore, string method, string file, int line) =>
        new() { ReleasesIt = true, Holds = [new HeldSemaphore(semaphore, method, new SourceLocation($"/src/{file}", line))] };

    private static readonly SemaphoreUse WorkerLoop = new() { WaitsInLoop = true };

    // ---------------------------------------------------------------- synchronous waits

    [Fact]
    public void Two_idle_workers_on_their_own_wake_up_signal_are_not_a_deadlock()
    {
        var snapshot = Process(SleepingMain,
            Waiter(4, "log-flusher", "MyApp.Flusher", "Run", "Flusher.cs", 16, 0x100, WorkerLoop),
            Waiter(5, "metrics-flusher", "MyApp.Flusher", "Run", "Flusher.cs", 16, 0x200, WorkerLoop)) with
        {
            Semaphores = [Sem(0x100, "Flusher._wakeUp", initial: 0, waiters: 4), Sem(0x200, "Flusher._wakeUp", initial: 0, waiters: 5)],
        };

        Assert.Empty(new HangAnalyzer().Analyze(snapshot).Findings);
    }

    [Fact]
    public void A_worker_waiting_in_a_loop_on_a_semaphore_without_a_maximum_is_still_reported()
    {
        // new SemaphoreSlim(0) released by a producer, or new SemaphoreSlim(4) used as a throttle, created somewhere the IL
        // didn't show: no maximum says nothing, so it is a wait the dump can't explain, not an idle worker.
        var snapshot = Process(SleepingMain, Waiter(4, "indexer", "MyApp.Indexer", "Run", "Indexer.cs", 33, 0x100, WorkerLoop)) with
        {
            Semaphores = [Sem(0x100, "Indexer._workAvailable", max: int.MaxValue, waiters: 4)],
        };

        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("A SemaphoreSlim can also be a signal, created with no free slots and released when there is work; if Indexer._workAvailable is one, this is only a wait for work. The dump can't tell which.", f.Details[1]);
    }

    [Fact]
    public void Crossed_waits_whose_code_took_the_other_semaphore_are_a_deadlock()
    {
        var snapshot = Process(SleepingMain,
            Waiter(4, "label-worker", "MyApp.Shipping", "PrintThenBook", "Shipping.cs", 17, 0x200, Holds(0x100, "Shipping.PrintThenBook", "Shipping.cs", 13)),
            Waiter(5, "carrier-worker", "MyApp.Shipping", "BookThenPrint", "Shipping.cs", 29, 0x100, Holds(0x200, "Shipping.BookThenPrint", "Shipping.cs", 25))) with
        {
            Semaphores = [Sem(0x100, "Shipping.Printer", waiters: 5), Sem(0x200, "Shipping.Carrier", waiters: 4)],
        };

        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);
        Assert.Equal("semaphore-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
        Assert.Equal([
            "Thread 5 (\"carrier-worker\") is waiting in Shipping.BookThenPrint (Shipping.cs:29) for Shipping.Printer (0 of 1 free).",
            "Thread 5 (\"carrier-worker\") took Shipping.Carrier earlier in Shipping.BookThenPrint (Shipping.cs:25) and doesn't release it before this wait.",
            "Thread 4 (\"label-worker\") is waiting in Shipping.PrintThenBook (Shipping.cs:17) for Shipping.Carrier (0 of 1 free).",
            "Thread 4 (\"label-worker\") took Shipping.Printer earlier in Shipping.PrintThenBook (Shipping.cs:13) and doesn't release it before this wait.",
            "No other thread is running app code that could release them.",
        ], f.Details);
        Assert.Equal([5, 4], f.ThreadIds);
    }

    [Fact]
    public void Two_taken_one_slot_semaphores_without_evidence_are_two_plain_waits()
    {
        // Each RunNow waits for its own gate, held by an async export awaiting something slow: nobody holds both.
        StackFrameInfo[] frames(string name) => [.. SemaphoreWait, App("MyApp.Exporter", "RunNow", "Exporter.cs", 18)];
        var lockUse = new SemaphoreUse { ReleasesIt = true };
        var snapshot = Process(SleepingMain,
            Thread(5, frames("orders-now"), "orders-now") with { WaitingForSemaphore = 0x100, SemaphoreUse = lockUse },
            Thread(6, frames("invoices-now"), "invoices-now") with { WaitingForSemaphore = 0x200, SemaphoreUse = lockUse }) with
        {
            Semaphores = [Sem(0x100, "Exporter._gate", initial: 1, waiters: 5), Sem(0x200, "Exporter._gate", initial: 1, waiters: 6)],
        };

        Assert.Empty(new SemaphoreDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        var findings = new HangAnalyzer().Analyze(snapshot).Findings;
        Assert.Equal(["semaphore-wait", "semaphore-wait"], findings.Select(f => f.Id));
        Assert.All(findings, f => Assert.Equal(Severity.Warning, f.Severity));
        Assert.All(findings, f => Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title));
        Assert.Equal("Thread 5 (\"orders-now\") is waiting in Exporter.RunNow (Exporter.cs:18) for Exporter._gate, a SemaphoreSlim with 0 of 1 slots free. No other thread is running app code that could release it.", findings[0].Summary);
    }

    [Fact]
    public void Only_the_threads_waiting_in_a_circle_are_the_deadlock()
    {
        var snapshot = Process(SleepingMain,
            Waiter(4, "a", "MyApp.A", "Run", "A.cs", 5, 0x200, Holds(0x100, "A.Run", "A.cs", 4)),
            Waiter(5, "b", "MyApp.B", "Run", "B.cs", 5, 0x300, Holds(0x200, "B.Run", "B.cs", 4)),
            Waiter(6, "c", "MyApp.C", "Run", "C.cs", 5, 0x100, Holds(0x300, "C.Run", "C.cs", 4)),
            Waiter(7, "d", "MyApp.D", "Run", "D.cs", 5, 0x400, new SemaphoreUse { ReleasesIt = true })) with
        {
            Semaphores = [Sem(0x100, "S.X", waiters: 6), Sem(0x200, "S.Y", waiters: 4), Sem(0x300, "S.Z", waiters: 5), Sem(0x400, "S.W", initial: 1, waiters: 7)],
        };

        var findings = new HangAnalyzer().Analyze(snapshot).Findings;
        var deadlock = Assert.Single(findings, f => f.Id == "semaphore-deadlock");
        Assert.Equal([6, 4, 5], deadlock.ThreadIds);
        Assert.StartsWith("Thread 6 (\"c\"), Thread 4 (\"a\") and Thread 5 (\"b\") are each waiting for a SemaphoreSlim(1, 1) that is already taken (S.X, S.Y and S.Z).", deadlock.Summary);
        // The fourth waits for a semaphore nobody in the circle holds: a plain wait.
        Assert.Equal([7], Assert.Single(findings, f => f.Id == "semaphore-wait").ThreadIds);
    }

    [Fact]
    public void A_thread_between_two_circles_is_not_part_of_the_deadlock()
    {
        // 4 and 5 wait for each other, and so do 7 and 8. Thread 6's code also took S.A, which 5 waits for, and 6 waits for
        // S.C, which 7 holds: it is stuck behind the second circle, but nothing leads from it back to itself.
        var snapshot = Process(SleepingMain,
            Waiter(4, "a", "MyApp.A", "Run", "A.cs", 5, 0x200, Holds(0x100, "A.Run", "A.cs", 4)),
            Waiter(5, "b", "MyApp.B", "Run", "B.cs", 5, 0x100, Holds(0x200, "B.Run", "B.cs", 4)),
            Waiter(6, "p", "MyApp.P", "Run", "P.cs", 5, 0x300, Holds(0x100, "P.Run", "P.cs", 4)),
            Waiter(7, "c", "MyApp.C", "Run", "C.cs", 5, 0x400, Holds(0x300, "C.Run", "C.cs", 4)),
            Waiter(8, "d", "MyApp.D", "Run", "D.cs", 5, 0x300, Holds(0x400, "D.Run", "D.cs", 4))) with
        {
            Semaphores = [Sem(0x100, "S.A", waiters: 5), Sem(0x200, "S.B", waiters: 4), Sem(0x300, "S.C", waiters: [6, 8]), Sem(0x400, "S.D", waiters: 7)],
        };

        var findings = new HangAnalyzer().Analyze(snapshot).Findings;
        var deadlock = Assert.Single(findings, f => f.Id == "semaphore-deadlock");
        Assert.Equal([5, 4, 8, 7], deadlock.ThreadIds);
        Assert.DoesNotContain(deadlock.Details, d => d.StartsWith("Thread 6 ", StringComparison.Ordinal));
        Assert.Equal([6], Assert.Single(findings, f => f.Id == "semaphore-wait").ThreadIds);
    }

    [Fact]
    public void A_wait_whose_code_says_nothing_keeps_the_warning_and_says_it_can_be_a_signal()
    {
        // A one-off wait on a semaphore made with no free slot: a startup signal not sent yet, or a gate never opened. Not
        // in a loop, so not a worker waiting for its next signal: the Warning a wait with no free slots always had.
        var snapshot = Process(SleepingMain, Waiter(4, "starter", "MyApp.Startup", "WaitForConfig", "Startup.cs", 21, 0x100, new SemaphoreUse())) with
        {
            Semaphores = [Sem(0x100, "Startup._configLoaded", initial: 0, waiters: 4)],
        };

        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal("Thread 4 (\"starter\") is waiting in Startup.WaitForConfig (Startup.cs:21) for Startup._configLoaded, a SemaphoreSlim with 0 of 1 slots free. No other thread is running app code that could release it.", f.Summary);
        Assert.Equal([
            "A SemaphoreSlim has no owner: whoever took the slot of Startup._configLoaded has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will.",
            "A SemaphoreSlim can also be a signal, created with no free slots and released when there is work; if Startup._configLoaded is one, this is only a wait for work. The dump can't tell which.",
        ], f.Details);
        Assert.Equal("If Startup._configLoaded is used as a lock, find the code that takes it and make sure it calls Release() on every path (try/finally); " +
                     "if it is a signal, check that the code that should call Release() when there is work still runs.", f.Advice);
    }

    [Fact]
    public void A_wait_whose_code_could_not_be_read_keeps_the_warning()
    {
        var snapshot = Process(SleepingMain, Waiter(4, "exporter", "MyApp.Exporter", "Export", "Exporter.cs", 17, 0x100, use: null)) with
        {
            Semaphores = [Sem(0x100, "Exporter._gate", waiters: 4)],
        };

        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("HangSloth couldn't follow the code that waits (for example a helper method the runtime inlined), so it can't tell whether Exporter._gate is used as a lock or as a signal.", f.Details[1]);
    }

    [Theory]
    // releases, loops, created with, maximum -> role
    [InlineData(true, true, 0, 1, "Lock")]                  // Wait; try { } finally { Release(); } in a loop
    [InlineData(false, false, 1, 1, "Lock")]                // made with a free slot, released elsewhere
    [InlineData(false, true, 0, 1, "Signal")]               // new(0, 1) wake-up signal
    [InlineData(false, true, null, int.MaxValue, "Unknown")] // new(0) or new(4) made somewhere unseen: no maximum proves nothing
    [InlineData(false, true, null, 1, "Unknown")]           // loop, but nothing says how it was made
    [InlineData(false, true, 1, int.MaxValue, "Lock")]      // made with a free slot and waited on in a loop: a throttle
    [InlineData(false, false, 0, 1, "Unknown")]             // one wait on a semaphore made with no free slot
    [InlineData(false, false, null, 1, "Unknown")]
    public void How_a_semaphore_is_used(bool releases, bool loops, int? initial, int max, string expected)
    {
        var use = new SemaphoreUse { ReleasesIt = releases, WaitsInLoop = loops };
        Assert.Equal(expected, SemaphoreFacts.Role(use, Sem(0x100, "S.X", max: max, initial: initial)).ToString());
    }

    [Fact]
    public void Without_the_code_a_semaphore_made_with_free_slots_is_still_a_lock()
    {
        Assert.Equal(SemaphoreRole.Lock, SemaphoreFacts.Role(null, Sem(0x100, "S.X", initial: 1)));
        Assert.Equal(SemaphoreRole.Unknown, SemaphoreFacts.Role(null, Sem(0x100, "S.X")));
    }

    // ---------------------------------------------------------------- async waits

    private const ulong Cache = 0x900;

    private static TaskInfo Node(ulong semaphore) =>
        new() { Address = 0x300, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()", Semaphore = semaphore };

    /// Main blocked on TariffCache.GetAsync, which awaits LoadAsync, which waits for TariffCache._gate.
    private static ProcessSnapshot GatedCache(SemaphoreUse? use, int max = 1, int asyncWaiters = 1, int? initial = 1)
    {
        var load = new TaskInfo { Address = 0x200, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "TariffCache.LoadAsync", ThisAddress = Cache, Awaiting = Node(0x100), AwaitLocation = new SourceLocation("/src/TariffCache.cs", 19) };
        var get = new TaskInfo { Address = 0x150, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "TariffCache.GetAsync", ThisAddress = Cache, Awaiting = load, AwaitLocation = new SourceLocation("/src/TariffCache.cs", 14) };
        return Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 49)]) with { BlockedOnTask = get, SemaphoreUse = use }) with
        {
            Semaphores = [Sem(0x100, "TariffCache._gate", max: max, initial: initial) with { ContainerAddress = Cache, AsyncWaiters = asyncWaiters }],
        };
    }

    private const string GatedCacheSummary =
        "Thread 1 is blocked in .Result at Program.cs:49, waiting for TariffCache.GetAsync (paused at TariffCache.cs:14), which awaits TariffCache.LoadAsync (paused at TariffCache.cs:19), " +
        "which is waiting to enter TariffCache._gate (SemaphoreSlim.WaitAsync(), 0 of 1 slots free).";

    private const string GatedCacheNoOwner =
        "A SemaphoreSlim has no owner: whoever took the slot of TariffCache._gate has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will.";

    [Fact]
    public void An_async_chain_whose_outer_method_never_takes_the_gate_is_queued_not_reentrant()
    {
        // LoadAsync takes and releases the gate; GetAsync, on the same object, never touches it. Another call holds it.
        var f = Assert.Single(new HangAnalyzer().Analyze(GatedCache(new SemaphoreUse { ReleasesIt = true })).Findings);
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal(GatedCacheSummary, f.Summary);
        Assert.Equal([GatedCacheNoOwner], f.Details);
        Assert.Equal("Find the code that took TariffCache._gate and make sure it calls Release() on every path (try/finally), and that it isn't waiting for this chain while holding it.", f.Advice);
    }

    [Fact]
    public void An_async_chain_whose_code_could_not_be_read_keeps_the_hint_without_the_verdict()
    {
        var f = Assert.Single(new HangAnalyzer().Analyze(GatedCache(use: null, initial: null)).Findings);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal([
            "TariffCache.GetAsync, earlier in this same async chain, runs on the same object that owns TariffCache._gate. If it took TariffCache._gate before calling TariffCache.LoadAsync, " +
            "the chain is waiting for itself: SemaphoreSlim isn't reentrant, so the second WaitAsync never completes.",
            GatedCacheNoOwner,
            "HangSloth couldn't follow the code that waits (for example a helper method the runtime inlined), so it can't tell whether TariffCache._gate is used as a lock or as a signal.",
        ], f.Details);
    }

    [Fact]
    public void An_async_chain_whose_outer_method_took_the_gate_is_a_deadlock()
    {
        var use = new SemaphoreUse { ReleasesIt = true, Holds = [new HeldSemaphore(0x100, "TariffCache.GetAsync", new SourceLocation("/src/TariffCache.cs", 12))] };
        var f = Assert.Single(new HangAnalyzer().Analyze(GatedCache(use)).Findings);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Deadlock: async code waiting for a SemaphoreSlim it already holds", f.Title);
        Assert.Equal(GatedCacheSummary, f.Summary);
        Assert.Equal("TariffCache.GetAsync took TariffCache._gate earlier (TariffCache.cs:12) and doesn't release it before the await it is paused at.", f.Details[1]);
    }

    [Fact]
    public void Holding_one_of_several_free_slots_is_not_yet_a_deadlock()
    {
        var use = new SemaphoreUse { ReleasesIt = true, Holds = [new HeldSemaphore(0x100, "TariffCache.GetAsync", null)] };
        var f = Assert.Single(new HangAnalyzer().Analyze(GatedCache(use, max: 3, asyncWaiters: 1)).Findings);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Async code waiting for a SemaphoreSlim it already holds", f.Title);
        Assert.Contains("TariffCache.GetAsync took TariffCache._gate earlier and doesn't release it before the await it is paused at.", f.Details);
    }

    [Fact]
    public void An_async_loop_waiting_for_a_signal_is_an_idle_pump()
    {
        var node = Node(0x100);
        var pump = new TaskInfo { Address = 0x200, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "MailPump.RunAsync", ThisAddress = Cache, Awaiting = node };
        var snapshot = Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 7)]) with { BlockedOnTask = pump, SemaphoreUse = WorkerLoop }) with
        {
            Semaphores = [Sem(0x100, "MailPump._ready", initial: 0) with { ContainerAddress = Cache, AsyncWaiters = 1 }],
        };

        Assert.Empty(new HangAnalyzer().Analyze(snapshot).Findings);
    }
}
