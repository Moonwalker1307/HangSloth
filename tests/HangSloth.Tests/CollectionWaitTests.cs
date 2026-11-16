using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// A BlockingCollection waits on a private SemaphoreSlim: an idle consumer is normal, a producer stuck on
/// a full collection is reported in collection terms, and neither is described as a semaphore of the app's.
public class CollectionWaitTests
{
    private static readonly StackFrameInfo[] SemaphoreCore =
        [Fx("System.Threading.Monitor", "Wait"), Fx("System.Threading.SemaphoreSlim", "WaitUntilCountOrTimeout"), Fx("System.Threading.SemaphoreSlim", "WaitCore")];

    private static readonly StackFrameInfo[] TakeWait =
        [.. SemaphoreCore, Fx("System.Collections.Concurrent.BlockingCollection`1", "TryTakeWithNoTimeValidation"), Fx("System.Collections.Concurrent.BlockingCollection`1", "Take")];

    private static readonly StackFrameInfo[] EnumerateWait =
        [.. SemaphoreCore, Fx("System.Collections.Concurrent.BlockingCollection`1", "TryTakeWithNoTimeValidation"), Fx("System.Collections.Concurrent.BlockingCollection`1", "GetConsumingEnumerable")];

    private static readonly StackFrameInfo[] AddWait =
        [.. SemaphoreCore, Fx("System.Collections.Concurrent.BlockingCollection`1", "TryAddWithNoTimeValidation"), Fx("System.Collections.Concurrent.BlockingCollection`1", "Add")];

    /// The main thread of a worker service, parked in host.Run().
    private static ThreadSnapshot HostMain() =>
        Thread(1, [Fx("System.Threading.ManualResetEventSlim", "Wait"), new() { TypeName = "Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions", MethodName = "Run", ModuleName = "/app/Microsoft.Extensions.Hosting.Abstractions.dll" },
            App("MyApp.Program", "Main", "Program.cs", 12)]);

    /// The collection's own semaphore, as SnapshotBuilder records it: created with one argument, so no maximum.
    private static SemaphoreInfo InnerSemaphore(ulong address, params int[] waiters) =>
        new() { Object = new ObjectRef(address, "System.Threading.SemaphoreSlim"), CurrentCount = 0, MaxCount = int.MaxValue, WaiterThreadIds = waiters };

    private static CollectionWait Outbox(bool adding, int? capacity, int? count) =>
        new() { Collection = new ObjectRef(0x300, "System.Collections.Concurrent.BlockingCollection<System.String>"), Adding = adding, Capacity = capacity, Count = count, FieldName = "MailQueue._outbox" };

    private static ProcessSnapshot IdleConsumer(StackFrameInfo[] wait, string method) =>
        Process(HostMain(), Thread(4, [.. wait, App("MyApp.MailQueue", method, "MailQueue.cs", 21)], "mail-consumer") with
        {
            WaitingForSemaphore = 0x500,
            WaitingOnCollection = Outbox(adding: false, capacity: null, count: 0),
        }) with { Semaphores = [InnerSemaphore(0x500, 4)] };

    public static TheoryData<string> ConsumerKinds => ["Take", "GetConsumingEnumerable"];

    private static ProcessSnapshot IdleConsumer(string kind) =>
        kind == "Take" ? IdleConsumer(TakeWait, "Consume") : IdleConsumer(EnumerateWait, "ConsumeAll");

    [Theory]
    [MemberData(nameof(ConsumerKinds))]
    public void An_idle_consumer_on_an_empty_collection_is_not_a_finding(string kind)
    {
        var snapshot = IdleConsumer(kind);
        Assert.Empty(new SemaphoreWaitDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Empty(new FullQueueDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Empty(new HangAnalyzer().Analyze(snapshot).Findings);
    }

    [Theory]
    [MemberData(nameof(ConsumerKinds))]
    public void An_idle_consumer_is_described_as_waiting_to_take_from_the_collection(string kind)
    {
        var consumer = IdleConsumer(kind).FindThread(4)!;
        var method = kind == "Take" ? "Consume" : "ConsumeAll";
        Assert.Equal($"waiting to take from MailQueue._outbox (empty BlockingCollection) in MailQueue.{method} (MailQueue.cs:21)", Describe.Activity(consumer, withMethod: true));
    }

    private static ProcessSnapshot FullOutbox(int? count, params ThreadSnapshot[] others) => FullOutbox(4, count, others);

    private static ProcessSnapshot FullOutbox(int? capacity, int? count, params ThreadSnapshot[] others) =>
        Process([Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Program", "Main", "Program.cs", 9)]), .. others,
            Thread(4, [.. AddWait, App("MyApp.Outbox", "Produce", "Outbox.cs", 30)], "mail-producer") with
            {
                WaitingForSemaphore = 0x500,
                WaitingOnCollection = Outbox(adding: true, capacity: capacity, count: count),
            }]) with { Semaphores = [InnerSemaphore(0x500, 4)] };

    [Fact]
    public void A_producer_stuck_on_a_full_collection_is_a_warning_in_collection_terms()
    {
        var report = new HangAnalyzer().Analyze(FullOutbox(count: 4));
        var f = Assert.Single(report.Findings);
        Assert.Equal("full-queue", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for room in a full BlockingCollection", f.Title);
        Assert.Equal("Thread 4 (\"mail-producer\") is waiting in Outbox.Produce (Outbox.cs:30) to add to MailQueue._outbox (full BlockingCollection), which holds 4 items, its bounded capacity. " +
                     "No other thread is running app code that could take items out.", f.Summary);
        Assert.Equal(["Add on a bounded BlockingCollection blocks until another thread takes an item out with Take, TryTake or GetConsumingEnumerable."], f.Details);
        Assert.Equal("Check that the code that takes items out of MailQueue._outbox is still running and hasn't stopped or thrown. " +
                     "If producers can outpace it, use TryAdd with a timeout and handle a full queue, or raise the bounded capacity.", f.Advice);
        Assert.Equal([4], f.ThreadIds);
        Assert.DoesNotContain("SemaphoreSlim", f.Summary + f.Advice + string.Join(" ", f.Details));
    }

    [Fact]
    public void A_producer_on_a_full_collection_is_only_info_while_other_app_code_runs()
    {
        var report = new HangAnalyzer().Analyze(FullOutbox(4, Thread(6, [App("MyApp.Mailer", "Send", "Mailer.cs", 40)], "mailer")));
        var f = Assert.Single(report.Findings);
        Assert.Equal("full-queue", f.Id);
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread 4 (\"mail-producer\") is waiting in Outbox.Produce (Outbox.cs:30) to add to MailQueue._outbox (full BlockingCollection), which holds 4 items, its bounded capacity. " +
                     "Other app code is still running and may yet take items out; if this persists across two dumps, nothing is taking items out.", f.Summary);
    }

    [Fact]
    public void A_full_collection_whose_count_could_not_be_read_still_names_its_capacity()
    {
        var f = Assert.Single(new FullQueueDetector().Detect(FullOutbox(count: null), new HashSet<int>()));
        Assert.StartsWith("Thread 4 (\"mail-producer\") is waiting in Outbox.Produce (Outbox.cs:30) to add to MailQueue._outbox (full BlockingCollection), which is at its bounded capacity of 4 items.", f.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3)]
    public void A_full_collection_whose_capacity_could_not_be_read_is_still_reported(int? count)
    {
        var f = Assert.Single(new HangAnalyzer().Analyze(FullOutbox(capacity: null, count)).Findings);
        Assert.Equal("full-queue", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        var fill = count is null ? "" : ", which holds 3 items";
        Assert.Equal($"Thread 4 (\"mail-producer\") is waiting in Outbox.Produce (Outbox.cs:30) to add to MailQueue._outbox (full BlockingCollection){fill}. " +
                     "No other thread is running app code that could take items out.", f.Summary);
    }

    [Fact]
    public void A_full_collection_with_an_unreadable_count_is_still_reported()
    {
        var f = Assert.Single(new FullQueueDetector().Detect(FullOutbox(count: -1), new HashSet<int>()));
        Assert.StartsWith("Thread 4 (\"mail-producer\") is waiting in Outbox.Produce (Outbox.cs:30) to add to MailQueue._outbox (full BlockingCollection), which is at its bounded capacity of 4 items.", f.Summary);
    }

    [Fact]
    public void A_producer_on_a_collection_with_room_is_not_reported()
    {
        Assert.Empty(new FullQueueDetector().Detect(FullOutbox(count: 3), new HashSet<int>()));
        Assert.Empty(new SemaphoreWaitDetector().Detect(FullOutbox(count: 3), new HashSet<int>()));
    }

    [Fact]
    public void Producers_on_the_same_full_collection_are_one_finding()
    {
        var second = Thread(5, [.. AddWait, App("MyApp.Outbox", "Produce", "Outbox.cs", 30)], "mail-producer-2") with { WaitingOnCollection = Outbox(adding: true, capacity: 4, count: 4) };
        var f = Assert.Single(new FullQueueDetector().Detect(FullOutbox(4, second), new HashSet<int>()));
        Assert.StartsWith("2 threads are waiting in Outbox.Produce (Outbox.cs:30) to add to MailQueue._outbox (full BlockingCollection)", f.Summary);
        Assert.Equal([5, 4], f.ThreadIds);
    }

    [Fact]
    public void A_producer_holding_a_lock_the_consumer_needs_is_still_a_bounded_queue_deadlock()
    {
        var snapshot = FullOutbox(4, Thread(7, [.. MonitorEnter, App("MyApp.MailQueue", "Drain", "MailQueue.cs", 44)], "mail-sender") with { WaitingForLock = 0x100 }) with
        {
            Locks = [new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "Outbox._sync", OwnerThreadId = 4, WaiterThreadIds = [7] }],
        };
        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);
        Assert.Equal("bounded-queue-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
    }

    [Fact]
    public void Collection_waiters_are_not_a_semaphore_deadlock()
    {
        var snapshot = Process(
            Thread(4, [.. TakeWait, App("MyApp.A", "Consume", "A.cs", 1)]) with { WaitingForSemaphore = 0x500, WaitingOnCollection = Outbox(false, 1, 0) },
            Thread(5, [.. AddWait, App("MyApp.B", "Produce", "B.cs", 1)]) with { WaitingForSemaphore = 0x600, WaitingOnCollection = Outbox(true, 1, 1) with { Collection = new ObjectRef(0x310, "System.Collections.Concurrent.BlockingCollection<System.Int32>") } }) with
        {
            Semaphores = [InnerSemaphore(0x500, 4) with { MaxCount = 1 }, InnerSemaphore(0x600, 5) with { MaxCount = 1 }],
        };
        Assert.Empty(new SemaphoreDeadlockDetector().Detect(snapshot, new HashSet<int>()));
    }

    [Fact]
    public void A_plain_SemaphoreSlim_waiter_is_reported_as_before()
    {
        var snapshot = Process(HostMain(), Thread(4, [.. SemaphoreCore, Fx("System.Threading.SemaphoreSlim", "Wait"), App("MyApp.Exporter", "Export", "Exporter.cs", 17)], "exporter") with { WaitingForSemaphore = 0x500, SemaphoreUse = new() { ReleasesIt = true } }) with
        {
            Semaphores = [new SemaphoreInfo { Object = new ObjectRef(0x500, "System.Threading.SemaphoreSlim"), FieldName = "Exporter._gate", CurrentCount = 0, MaxCount = 2, WaiterThreadIds = [4] }],
        };
        var f = Assert.Single(new HangAnalyzer().Analyze(snapshot).Findings);
        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Waiting for a SemaphoreSlim with no free slots", f.Title);
        Assert.Equal("Thread 4 (\"exporter\") is waiting in Exporter.Export (Exporter.cs:17) for Exporter._gate, a SemaphoreSlim with 0 of 2 slots free. No other thread is running app code that could release it.", f.Summary);
        Assert.Equal(["A SemaphoreSlim has no owner: whoever took the slot of Exporter._gate has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will."], f.Details);
        Assert.Equal("Find the code that takes Exporter._gate and make sure it calls Release() on every path (try/finally), and never waits for this thread while holding it.", f.Advice);
        Assert.Equal([4], f.ThreadIds);
    }
}
