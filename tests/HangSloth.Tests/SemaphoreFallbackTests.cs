using System.Reflection.Emit;
using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// When the code that waits can't be followed (in optimized code the JIT inlines small helpers such as a lock class's
/// Enter(), and the frame then shows only the caller), the verdict falls back to what the semaphore's counts show: never
/// less than a wait with no free slots always got, never Critical. A role needs proof: a semaphore made with free slots is
/// a lock, and only one made with none and waited on in a loop by code that never releases it is a signal.
public class SemaphoreFallbackTests
{
    private static readonly StackFrameInfo[] SemaphoreWait =
        [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "WaitCore"), Fx("System.Threading.SemaphoreSlim", "Wait")];

    private static readonly ThreadSnapshot SleepingMain = Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 9)]);

    private static readonly ThreadSnapshot BusyWorker = Thread(9, [App("MyApp.Reports", "Render", "Reports.cs", 40)], "report-render");

    private static SemaphoreInfo Sem(ulong address, string name, int max = 1, int? initial = null, params int[] waiters) =>
        new() { Object = new ObjectRef(address, "System.Threading.SemaphoreSlim"), FieldName = name, CurrentCount = 0, MaxCount = max, InitialCount = initial, WaiterThreadIds = waiters };

    private static ThreadSnapshot Waiter(int id, string name, string method, int line, ulong semaphore, SemaphoreUse? use) =>
        Thread(id, [.. SemaphoreWait, App("MyApp.Orders", method, "Orders.cs", line)], name) with { WaitingForSemaphore = semaphore, SemaphoreUse = use };

    private static readonly SemaphoreUse WorkerLoop = new() { WaitsInLoop = true };

    private static List<Finding> Analyze(ProcessSnapshot snapshot) => new HangAnalyzer().Analyze(snapshot).Findings.ToList();

    private const string NotFollowedGate =
        "HangSloth couldn't follow the code that waits (for example a helper method the runtime inlined), so it can't tell whether Orders._gate is used as a lock or as a signal.";

    // ---------------------------------------------------------------- a role needs proof

    [Fact]
    public void A_throttle_made_with_free_slots_is_a_lock_also_when_it_is_waited_on_in_a_loop()
    {
        // new SemaphoreSlim(4): no maximum, waited on in the dispatcher's loop, released by the jobs, every slot leaked.
        var snapshot = Process(SleepingMain, Waiter(4, "dispatcher", "Dispatch", 22, 0x100, WorkerLoop)) with
        {
            Semaphores = [Sem(0x100, "Orders._throttle", max: int.MaxValue, initial: 4, waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal(["A SemaphoreSlim has no owner: whoever took the slot of Orders._throttle has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will."], f.Details);
    }

    [Fact]
    public void A_loop_on_a_semaphore_whose_initial_count_is_unknown_is_reported_but_not_as_a_hang()
    {
        // The throttle was passed in, so the code that made it isn't known: it may be new SemaphoreSlim(0) or (4).
        var snapshot = Process(SleepingMain, Waiter(4, "dispatcher", "Dispatch", 22, 0x100, WorkerLoop)) with
        {
            Semaphores = [Sem(0x100, "Orders._throttle", max: int.MaxValue, waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("The dump can't tell which.", f.Details[1]);
    }

    // ---------------------------------------------------------------- a single wait whose code couldn't be followed

    [Fact]
    public void A_wait_whose_code_could_not_be_followed_is_a_warning_when_nothing_else_runs()
    {
        var snapshot = Process(SleepingMain, Waiter(4, "export", "Export", 17, 0x100, use: null)) with
        {
            Semaphores = [Sem(0x100, "Orders._gate", waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal("Thread 4 (\"export\") is waiting in Orders.Export (Orders.cs:17) for Orders._gate, a SemaphoreSlim with 0 of 1 slots free. No other thread is running app code that could release it.", f.Summary);
        Assert.Equal([
            "A SemaphoreSlim has no owner: whoever took the slot of Orders._gate has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will.",
            NotFollowedGate,
        ], f.Details);
        Assert.Equal("If Orders._gate is used as a lock, find the code that takes it and make sure it calls Release() on every path (try/finally); " +
                     "if it is a signal, check that the code that should call Release() when there is work still runs.", f.Advice);
    }

    [Fact]
    public void A_wait_whose_code_could_not_be_followed_is_info_while_other_app_code_runs()
    {
        var snapshot = Process(SleepingMain, BusyWorker, Waiter(4, "export", "Export", 17, 0x100, use: null)) with
        {
            Semaphores = [Sem(0x100, "Orders._gate", waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread 4 (\"export\") is waiting in Orders.Export (Orders.cs:17) for Orders._gate, a SemaphoreSlim with 0 of 1 slots free.", f.Summary);
        Assert.Equal(NotFollowedGate, f.Details[1]);
    }

    // ---------------------------------------------------------------- circles that can't be proven

    [Fact]
    public void Two_threads_whose_code_could_not_be_followed_are_one_possible_deadlock()
    {
        var snapshot = Process(SleepingMain,
            Waiter(4, "print-then-book", "PrintThenBook", 14, 0x200, use: null),
            Waiter(5, "book-then-print", "BookThenPrint", 26, 0x100, use: null)) with
        {
            Semaphores = [Sem(0x100, "Orders.Printer", waiters: 5), Sem(0x200, "Orders.Carrier", waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("semaphore-deadlock", f.Id);
        // Never Critical, also with nothing else running: two idle workers on signals of their own look the same.
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Possible deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
        Assert.Equal("Thread 5 (\"book-then-print\") and Thread 4 (\"print-then-book\") are each waiting for a SemaphoreSlim(1, 1) that is already taken (Orders.Printer and Orders.Carrier). " +
                     "If each of them holds the one another is waiting for, they are deadlocked, but the dump can't confirm it: a SemaphoreSlim doesn't record who holds it, " +
                     "and the code that waits in Thread 5 (\"book-then-print\") and Thread 4 (\"print-then-book\") couldn't be followed (for example a helper method the runtime inlined). " +
                     "Start at Orders.cs:26.", f.Summary);
        Assert.Equal([
            "Thread 5 (\"book-then-print\") is waiting in Orders.BookThenPrint (Orders.cs:26) for Orders.Printer (0 of 1 free).",
            "Thread 4 (\"print-then-book\") is waiting in Orders.PrintThenBook (Orders.cs:14) for Orders.Carrier (0 of 1 free).",
            "Workers that each wait for a signal of their own, a SemaphoreSlim created with no free slot, look the same in a dump.",
        ], f.Details);
        Assert.Equal("Check at the lines above whether the code holds one of these semaphores while it waits for another; if it does, " +
                     "take them in one consistent order everywhere, or replace them with a single semaphore.", f.Advice);
        Assert.Equal([5, 4], f.ThreadIds);
    }

    [Fact]
    public void A_proven_half_and_a_thread_that_could_not_be_followed_are_a_possible_deadlock_with_the_proven_line()
    {
        // Thread 4's code took the printer and waits for the carrier; thread 5 waits for the printer in code that can't be read.
        var took = new SemaphoreUse { ReleasesIt = true, Holds = [new HeldSemaphore(0x100, "Orders.PrintThenBook", new SourceLocation("/src/Orders.cs", 12))] };
        var snapshot = Process(SleepingMain,
            Waiter(4, "print-then-book", "PrintThenBook", 14, 0x200, took),
            Waiter(5, "book-then-print", "BookThenPrint", 26, 0x100, use: null)) with
        {
            Semaphores = [Sem(0x100, "Orders.Printer", initial: 1, waiters: 5), Sem(0x200, "Orders.Carrier", initial: 1, waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("semaphore-deadlock", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.StartsWith("Possible deadlock:", f.Title);
        Assert.Contains("the code that waits in Thread 5 (\"book-then-print\") couldn't be followed", f.Summary);
        Assert.Contains("Thread 4 (\"print-then-book\") took Orders.Printer earlier in Orders.PrintThenBook (Orders.cs:12) and doesn't release it before this wait.", f.Details);
        Assert.Equal([5, 4], f.ThreadIds);
    }

    [Fact]
    public void Two_threads_whose_code_was_read_and_holds_nothing_are_two_plain_waits()
    {
        // Both locks were made with their slot free, so both are locks, and the code that waits took nothing before it.
        var read = new SemaphoreUse();
        var snapshot = Process(SleepingMain,
            Waiter(4, "print-then-book", "PrintThenBook", 14, 0x200, read),
            Waiter(5, "book-then-print", "BookThenPrint", 26, 0x100, read)) with
        {
            Semaphores = [Sem(0x100, "Orders.Printer", initial: 1, waiters: 5), Sem(0x200, "Orders.Carrier", initial: 1, waiters: 4)],
        };

        var findings = Analyze(snapshot);
        Assert.Equal(["semaphore-wait", "semaphore-wait"], findings.Select(f => f.Id));
        Assert.All(findings, f => Assert.Equal(Severity.Warning, f.Severity));
    }

    [Fact]
    public void Two_threads_whose_code_was_read_but_shows_no_role_are_a_possible_deadlock()
    {
        // A lock class with an injected SemaphoreSlim(1, 1): the code that waits is its Enter(), which took nothing itself,
        // and the code that made the semaphore isn't known. Each may hold the other's through an earlier Enter().
        var read = new SemaphoreUse();
        var snapshot = Process(SleepingMain,
            Waiter(4, "print-then-book", "PrintThenBook", 14, 0x200, read),
            Waiter(5, "book-then-print", "BookThenPrint", 26, 0x100, read)) with
        {
            Semaphores = [Sem(0x100, "Orders.Printer", waiters: 5), Sem(0x200, "Orders.Carrier", waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("semaphore-deadlock", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Possible deadlock: SemaphoreSlim locks taken in opposite order", f.Title);
        Assert.Equal("Thread 5 (\"book-then-print\") and Thread 4 (\"print-then-book\") are each waiting for a SemaphoreSlim(1, 1) that is already taken (Orders.Printer and Orders.Carrier). " +
                     "If each of them holds the one another is waiting for, they are deadlocked, but the dump can't confirm it: a SemaphoreSlim doesn't record who holds it, " +
                     "and the code that waits in Thread 5 (\"book-then-print\") and Thread 4 (\"print-then-book\") doesn't show whether it took one of them earlier (for example through a lock class's Enter()). " +
                     "Start at Orders.cs:26.", f.Summary);
        Assert.Equal([5, 4], f.ThreadIds);
    }

    [Fact]
    public void A_possible_deadlock_names_both_kinds_of_code_it_could_not_check()
    {
        var snapshot = Process(SleepingMain,
            Waiter(4, "print-then-book", "PrintThenBook", 14, 0x200, new SemaphoreUse()),
            Waiter(5, "book-then-print", "BookThenPrint", 26, 0x100, use: null)) with
        {
            Semaphores = [Sem(0x100, "Orders.Printer", waiters: 5), Sem(0x200, "Orders.Carrier", waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Contains("a SemaphoreSlim doesn't record who holds it, and the code that waits in Thread 5 (\"book-then-print\") couldn't be followed (for example a helper method the runtime inlined), " +
                        "and the code that waits in Thread 4 (\"print-then-book\") doesn't show whether it took one of them earlier (for example through a lock class's Enter()).", f.Summary);
    }

    [Fact]
    public void A_single_wait_whose_code_shows_no_role_keeps_the_warning_and_a_loop_is_info()
    {
        Finding wait(SemaphoreUse use) => Assert.Single(Analyze(Process(SleepingMain, Waiter(4, "export", "Export", 17, 0x100, use)) with
        {
            Semaphores = [Sem(0x100, "Orders._gate", waiters: 4)],
        }));

        var once = wait(new SemaphoreUse());
        Assert.Equal(Severity.Warning, once.Severity);
        Assert.EndsWith("The dump can't tell which.", once.Details[1]);
        Assert.Equal(Severity.Info, wait(WorkerLoop).Severity);
    }

    [Fact]
    public void A_semaphore_made_closed_with_room_for_several_is_no_proven_signal()
    {
        // new SemaphoreSlim(0, 4): a throttle opened with Release(4) once the service is ready, or a bounded buffer's items.
        var snapshot = Process(SleepingMain, Waiter(4, "dispatcher", "Dispatch", 22, 0x100, WorkerLoop)) with
        {
            Semaphores = [Sem(0x100, "Orders._throttle", max: 4, initial: 0, waiters: 4)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.EndsWith("The dump can't tell which.", f.Details[1]);
        Assert.Equal(SemaphoreRole.Signal, SemaphoreFacts.Role(WorkerLoop, Sem(0x100, "Orders._wakeUp", max: 1, initial: 0)));
        Assert.Equal(SemaphoreRole.Signal, SemaphoreFacts.Role(WorkerLoop, Sem(0x100, "Orders._work", max: int.MaxValue, initial: 0)));
    }

    [Fact]
    public void A_signal_worker_next_to_a_thread_that_could_not_be_followed_is_not_a_circle()
    {
        var snapshot = Process(SleepingMain,
            Waiter(4, "flusher", "Flush", 30, 0x100, WorkerLoop),
            Waiter(5, "export", "Export", 17, 0x200, use: null)) with
        {
            Semaphores = [Sem(0x100, "Orders._wakeUp", initial: 0, waiters: 4), Sem(0x200, "Orders._gate", waiters: 5)],
        };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal([5], f.ThreadIds);
        Assert.Equal(NotFollowedGate, f.Details[1]);
    }

    [Fact]
    public void Threads_waiting_for_the_same_one_slot_semaphore_cannot_hold_it_for_each_other()
    {
        // 4 and 5 both wait for the gate, so neither holds it; 6's code was read, holds nothing and waits in a loop, as a
        // worker waiting for work does: no circle.
        var snapshot = Process(SleepingMain,
            Waiter(4, "export-1", "Export", 17, 0x100, use: null),
            Waiter(5, "export-2", "Export", 17, 0x100, use: null),
            Waiter(6, "print", "Print", 40, 0x200, WorkerLoop)) with
        {
            Semaphores = [Sem(0x100, "Orders._gate", waiters: [4, 5]), Sem(0x200, "Orders.Printer", waiters: 6)],
        };

        var findings = Analyze(snapshot);
        Assert.DoesNotContain(findings, f => f.Id == "semaphore-deadlock");
        Assert.Equal([4, 5], Assert.Single(findings, f => f.Severity == Severity.Warning).ThreadIds);
    }

    [Fact]
    public void A_proven_deadlock_stays_critical_next_to_a_thread_that_could_not_be_followed()
    {
        SemaphoreUse took(ulong semaphore, string method) => new() { ReleasesIt = true, Holds = [new HeldSemaphore(semaphore, method, new SourceLocation("/src/Orders.cs", 12))] };
        var snapshot = Process(SleepingMain,
            Waiter(4, "print-then-book", "PrintThenBook", 14, 0x200, took(0x100, "Orders.PrintThenBook")),
            Waiter(5, "book-then-print", "BookThenPrint", 26, 0x100, took(0x200, "Orders.BookThenPrint")),
            Waiter(6, "export", "Export", 17, 0x300, use: null)) with
        {
            Semaphores = [Sem(0x100, "Orders.Printer", waiters: 5), Sem(0x200, "Orders.Carrier", waiters: 4), Sem(0x300, "Orders._gate", waiters: 6)],
        };

        var findings = Analyze(snapshot);
        var deadlock = Assert.Single(findings, f => f.Id == "semaphore-deadlock");
        Assert.Equal(Severity.Critical, deadlock.Severity);
        Assert.Equal("Deadlock: SemaphoreSlim locks taken in opposite order", deadlock.Title);
        Assert.Equal([5, 4], deadlock.ThreadIds);
        Assert.Equal([6], Assert.Single(findings, f => f.Id == "semaphore-wait").ThreadIds);
    }

    [Fact]
    public void Many_threads_whose_code_could_not_be_followed_cost_little()
    {
        // 1,500 threads, each waiting for a one-slot semaphore of its own: every one may hold any other's.
        var threads = Enumerable.Range(10, 1500).Select(id => Waiter(id, $"worker-{id}", "Run", 5, (ulong)id * 0x10, use: null)).ToList();
        var snapshot = Process([SleepingMain, .. threads]) with
        {
            Semaphores = threads.Select(t => Sem(t.WaitingForSemaphore!.Value, $"Orders.Gate{t.ManagedId}", waiters: t.ManagedId)).ToList(),
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var f = Assert.Single(new SemaphoreDeadlockDetector().Detect(snapshot, new HashSet<int>()));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Equal(1500, f.ThreadIds.Count);
    }

    // ---------------------------------------------------------------- async

    [Fact]
    public void An_async_wait_on_a_semaphore_that_could_not_be_named_is_a_warning()
    {
        var node = new TaskInfo { Address = 0x300, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()" };
        var load = new TaskInfo { Address = 0x200, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "Orders.LoadAsync", Awaiting = node };
        var snapshot = Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 49)]) with { BlockedOnTask = load });

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal(["HangSloth couldn't follow the code that waits (for example a helper method the runtime inlined), so it can't tell whether a SemaphoreSlim is used as a lock or as a signal."], f.Details);
    }

    [Fact]
    public void An_async_wait_whose_code_shows_no_role_keeps_the_warning_and_a_loop_is_info()
    {
        // await gate.LockAsync() on a lock class handed its semaphore: LockAsync took nothing itself, and the code that made
        // the semaphore isn't known. One path of the caller forgot to dispose the lease.
        Finding wait(SemaphoreUse use)
        {
            var node = new TaskInfo { Address = 0x300, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()", Semaphore = 0x100 };
            var lockAsync = new TaskInfo { Address = 0x200, TypeName = "Box", Kind = TaskKind.AsyncMethod, Description = "AsyncGate.LockAsync", Awaiting = node };
            return Assert.Single(Analyze(Process(Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 49)]) with { BlockedOnTask = lockAsync, SemaphoreUse = use }) with
            {
                Semaphores = [Sem(0x100, "AsyncGate.<slot>P") with { AsyncWaiters = 1 }],
            }));
        }

        var once = wait(new SemaphoreUse());
        Assert.Equal("semaphore-wait", once.Id);
        Assert.Equal(Severity.Warning, once.Severity);
        Assert.EndsWith("The dump can't tell which.", once.Details[^1]);
        Assert.Equal(Severity.Info, wait(WorkerLoop).Severity);
    }

    // ---------------------------------------------------------------- which wait is in progress

    private static SnapshotBuilder.SemaphoreCall Call(int offset, ulong? address, SnapshotBuilder.SemaphoreCallKind kind = SnapshotBuilder.SemaphoreCallKind.Wait) =>
        new(offset, kind, address is null ? "ldarg.1" : null, address, null, false, (offset + 1, int.MaxValue));

    [Fact]
    public void A_wait_that_already_returned_is_not_the_wait_in_progress()
    {
        // A.Wait(); B.Wait(); A.Release(); Helper(); with Helper inlined and waiting on a third semaphore: the frame's
        // offset is past both waits, and neither is it.
        Assert.Null(SnapshotBuilder.CurrentWait([Call(10, 0xA00), Call(20, 0xB00)], 0xC00, 40));
        Assert.Null(SnapshotBuilder.CurrentWait([Call(10, null)], 0xC00, 40));
        // A wait resolved to another semaphore isn't this one, even ahead of the offset.
        Assert.Null(SnapshotBuilder.CurrentWait([Call(50, 0xA00)], 0xC00, 40));
        Assert.Equal(50, SnapshotBuilder.CurrentWait([Call(10, null), Call(50, null)], 0xC00, 40)?.Offset);
        Assert.Equal(30, SnapshotBuilder.CurrentWait([Call(10, 0xA00), Call(30, 0xC00)], 0xC00, 30)?.Offset);
        // Without an offset only a single wait known to be on the semaphore will do.
        Assert.Null(SnapshotBuilder.CurrentWait([Call(10, null)], 0xC00, -1));
        Assert.Equal(10, SnapshotBuilder.CurrentWait([Call(10, 0xC00), Call(20, 0xA00)], 0xC00, -1)?.Offset);
    }

    [Fact]
    public void A_return_address_outside_the_code_map_has_no_il_offset()
    {
        // The map of Ledger.MoveTo as ClrMD read it from a warmed-up dump: it describes another version of the code, and the
        // frame's return address (0x7000_005d) is in none of its entries. GetILOffset said 0, the start of the method.
        ILToNativeMap entry(int il, ulong start, ulong end) => new() { ILOffset = il, StartAddress = start, EndAddress = end };
        ILToNativeMap[] map = [entry(-2, 0x6fff_cfe0, 0x6fff_cff2), entry(0, 0x6fff_cff2, 0x6fff_d003), entry(11, 0x6fff_d003, 0x6fff_d009), entry(24, 0x6fff_d023, 0x6fff_d033)];
        Assert.Equal(-1, SnapshotBuilder.IlOffsetAt(map, 0x7000_005c));
        Assert.Equal(11, SnapshotBuilder.IlOffsetAt(map, 0x6fff_d005));
        Assert.Equal(-1, SnapshotBuilder.IlOffsetAt(map, 0x6fff_cfe4)); // the prolog
    }

    [Fact]
    public void A_call_that_already_returned_is_not_the_call_in_progress()
    {
        Assert.Null(SnapshotBuilder.CallInProgress([10, 20], 30));
        Assert.Equal(20, SnapshotBuilder.CallInProgress([10, 20], 15));
        Assert.Equal(10, SnapshotBuilder.CallInProgress([10], -1));
    }

    private static readonly Dictionary<int, string> Callees = new()
    {
        [1] = "WaitAsync", [2] = "GetAwaiter", [3] = "get_IsCompleted", [4] = "ConfigureAwait", [5] = "EnterAsync",
    };

    /// Lays out instructions one after another, at the offsets their sizes give them in IL.
    private static List<SnapshotBuilder.Instruction> Il(params (OpCode Op, int Operand)[] code)
    {
        var list = new List<SnapshotBuilder.Instruction>();
        var offset = 0;
        foreach (var (op, operand) in code)
        {
            list.Add(new SnapshotBuilder.Instruction(offset, op.Value, operand, null));
            offset += op.Size + op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineVar or OperandType.ShortInlineI or OperandType.ShortInlineBrTarget => 1,
                _ => 4,
            };
        }
        return list;
    }

    /// The state store an await suspends with: ldarg.0; ldc.i4.0; dup; stloc.0; stfld &lt;&gt;1__state.
    private static readonly (OpCode, int)[] Suspend = [(OpCodes.Ldarg_0, 0), (OpCodes.Ldc_I4_0, 0), (OpCodes.Dup, 0), (OpCodes.Stloc_0, 0), (OpCodes.Stfld, 9)];

    private static int? Awaited(List<SnapshotBuilder.Instruction> il) =>
        SnapshotBuilder.AwaitedCall(il, t => Callees.GetValueOrDefault(t), il[^4].Offset);

    [Fact]
    public void The_awaited_call_is_the_one_whose_awaiter_the_suspension_waits_for()
    {
        // await _gate.WaitAsync();
        var plain = Il([(OpCodes.Ldarg_0, 0), (OpCodes.Ldfld, 7), (OpCodes.Callvirt, 1), (OpCodes.Callvirt, 2), (OpCodes.Stloc_1, 0), (OpCodes.Ldloca_S, 1),
                        (OpCodes.Call, 3), (OpCodes.Brtrue_S, 0), .. Suspend]);
        Assert.Equal(plain[2].Offset, Awaited(plain));

        // await _gate.WaitAsync().ConfigureAwait(false);
        var configured = Il([(OpCodes.Ldarg_0, 0), (OpCodes.Ldfld, 7), (OpCodes.Callvirt, 1), (OpCodes.Ldc_I4_0, 0), (OpCodes.Callvirt, 4), (OpCodes.Stloc_2, 0), (OpCodes.Ldloca_S, 2),
                             (OpCodes.Call, 2), (OpCodes.Stloc_1, 0), (OpCodes.Ldloca_S, 1), (OpCodes.Call, 3), (OpCodes.Brtrue_S, 0), .. Suspend]);
        Assert.Equal(configured[2].Offset, Awaited(configured));

        // var t = _gate.WaitAsync(); await t;  -- the task went through a local: unknown.
        var local = Il([(OpCodes.Ldarg_0, 0), (OpCodes.Ldfld, 7), (OpCodes.Callvirt, 1), (OpCodes.Stloc_3, 0), (OpCodes.Ldloc_3, 0), (OpCodes.Callvirt, 2), (OpCodes.Stloc_1, 0),
                        (OpCodes.Ldloca_S, 1), (OpCodes.Call, 3), (OpCodes.Brtrue_S, 0), .. Suspend]);
        Assert.Null(Awaited(local));
    }

    [Fact]
    public void An_await_on_a_helper_is_not_an_earlier_wait_async()
    {
        // await _a.WaitAsync(); ... await _locks.EnterAsync();  -- paused at the second await, on a task the helper returned.
        var il = Il([(OpCodes.Ldarg_1, 0), (OpCodes.Callvirt, 1), (OpCodes.Pop, 0), (OpCodes.Ldarg_0, 0), (OpCodes.Ldfld, 7), (OpCodes.Callvirt, 5), (OpCodes.Callvirt, 2),
                     (OpCodes.Stloc_1, 0), (OpCodes.Ldloca_S, 1), (OpCodes.Call, 3), (OpCodes.Brtrue_S, 0), .. Suspend]);
        var paused = il[^4].Offset;
        var awaited = Awaited(il);
        Assert.Equal(il[5].Offset, awaited);
        var earlier = Call(il[1].Offset, null, SnapshotBuilder.SemaphoreCallKind.WaitAsync);
        Assert.Null(SnapshotBuilder.CurrentAwaitedWait([earlier], 0xC00, paused, awaited));
        Assert.Equal(earlier, SnapshotBuilder.CurrentAwaitedWait([earlier], 0xC00, paused, il[1].Offset));
    }
}
