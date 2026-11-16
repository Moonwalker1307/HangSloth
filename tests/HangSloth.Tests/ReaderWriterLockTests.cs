using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// ReaderWriterLockSlim: who holds it for reading, an upgrade waiting for readers, and when that is a deadlock.
public class ReaderWriterLockTests
{
    private static readonly StackFrameInfo[] WriteWait = [Fx("System.Threading.ReaderWriterLockSlim", "WaitOnEvent"), Fx("System.Threading.ReaderWriterLockSlim", "TryEnterWriteLockCore")];
    private static readonly StackFrameInfo[] ReadWait = [Fx("System.Threading.ReaderWriterLockSlim", "WaitOnEvent"), Fx("System.Threading.ReaderWriterLockSlim", "TryEnterReadLockCore")];
    private static readonly StackFrameInfo[] Sleep = [Fx("System.Threading.Thread", "Sleep")];

    private static ThreadSnapshot Upgrader(int id = 5) =>
        Thread(id, [.. WriteWait, App("MyApp.PriceCatalog", "RefreshIfStale", "PriceCatalog.cs", 34)], "price-refresh") with { WaitingForLock = 0x200 };

    private static ThreadSnapshot Reader(int id, string name = "report-export") =>
        Thread(id, [.. Sleep, App("MyApp.PriceCatalog", "ExportReport", "PriceCatalog.cs", 19)], name);

    private static MonitorLock Rw(int? owner = null, int? upgrader = null, int readerCount = 0, int[]? readers = null, int[]? waiters = null) => new()
    {
        Object = new ObjectRef(0x200, "System.Threading.ReaderWriterLockSlim"),
        Kind = LockKind.ReaderWriter,
        FieldName = "PriceCatalog._rw",
        OwnerThreadId = owner,
        UpgraderThreadId = upgrader,
        ReaderCount = readerCount,
        ReaderThreadIds = readers ?? [],
        WaiterThreadIds = waiters ?? [],
    };

    private static List<Finding> Analyze(ProcessSnapshot snapshot) => new HangAnalyzer().Analyze(snapshot).Findings.ToList();

    [Theory]
    [InlineData(0x00000001u, 1)]
    [InlineData(0x00000003u, 3)]
    [InlineData(0x40000001u, 1)] // a writer is waiting
    [InlineData(0x60000002u, 2)] // writer and upgrader waiting: one reader plus the upgradeable holder
    [InlineData(0x80000000u, 0)] // the write lock is held
    [InlineData(0x0FFFFFFEu, 0x0FFFFFFE)]
    public void Reader_count_is_the_low_28_bits_of_owners(uint owners, int readers) =>
        Assert.Equal(readers, SnapshotBuilder.RwReaderCount(owners));

    [Fact]
    public void An_upgrade_waiting_for_a_slow_reader_is_a_wait_not_a_deadlock()
    {
        var snapshot = Process(Upgrader(), Reader(4)) with { Locks = [Rw(owner: 5, upgrader: 5, readerCount: 2, readers: [4], waiters: [5])] };

        var findings = Analyze(snapshot);
        var f = Assert.Single(findings);
        Assert.Equal("lock-contention", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Upgrade to a write lock waiting for readers", f.Title);
        Assert.Equal("Thread 5 (\"price-refresh\") is waiting to upgrade to the write lock on PriceCatalog._rw (a ReaderWriterLockSlim) while 1 other thread holds it for reading: " +
                     "Thread 4 (\"report-export\"). The upgrade goes ahead as soon as that reader leaves.", f.Summary);
        Assert.Equal(["Waiting at: PriceCatalog.cs:34.", "Thread 4 (\"report-export\") holds the read lock and is sleeping (Thread.Sleep) in PriceCatalog.ExportReport (PriceCatalog.cs:19)."], f.Details);
        Assert.Equal("An upgradeable read lock can only become a write lock once every reader has left. Keep the work inside read locks short " +
                     "(don't sleep, block or wait for other threads while holding one), or take the write lock directly when you know you'll write.", f.Advice);
        Assert.Equal([5, 4], f.ThreadIds);
        Assert.Equal(19, f.Location?.Line);
    }

    [Fact]
    public void An_upgrade_with_readers_the_dump_does_not_name_counts_only_the_others()
    {
        var snapshot = Process(Upgrader()) with { Locks = [Rw(owner: 5, upgrader: 5, readerCount: 3, waiters: [5])] };

        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread 5 (\"price-refresh\") is waiting to upgrade to the write lock on PriceCatalog._rw (a ReaderWriterLockSlim) while 2 other threads hold it for reading. " +
                     "Readers aren't recorded per thread, so look for code that entered the read lock and is slow or blocked before ExitReadLock.", f.Summary);
        Assert.Empty(new LockDeadlockDetector().Detect(snapshot, new HashSet<int>()));
    }

    [Fact]
    public void Threads_queued_behind_an_upgrade_are_named()
    {
        var writer = Thread(6, [.. WriteWait, App("MyApp.PriceCatalog", "SetPrice", "PriceCatalog.cs", 47)], "price-update") with { WaitingForLock = 0x200 };
        var snapshot = Process(Upgrader(), Reader(4), writer) with { Locks = [Rw(owner: 5, upgrader: 5, readerCount: 2, readers: [4], waiters: [5, 6])] };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("Upgrade to a write lock waiting for readers", f.Title);
        Assert.Contains("Thread 6 (\"price-update\") is queued behind the upgradeable read lock that Thread 5 (\"price-refresh\") holds.", f.Details);
        Assert.Equal("Waiting at: PriceCatalog.cs:34 and PriceCatalog.cs:47.", f.Details[0]);
        Assert.Equal([5, 6, 4], f.ThreadIds);
    }

    [Fact]
    public void A_writer_behind_one_reader_is_not_an_orphaned_lock()
    {
        var writer = Thread(6, [.. WriteWait, App("MyApp.PriceCatalog", "SetPrice", "PriceCatalog.cs", 47)], "price-update") with { WaitingForLock = 0x200 };
        var snapshot = Process(writer, Reader(4)) with { Locks = [Rw(readerCount: 1, readers: [4], waiters: [6])] };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("Writer waiting behind readers", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 6 (\"price-update\") is waiting for PriceCatalog._rw (a ReaderWriterLockSlim), which 1 thread holds for reading: Thread 4 (\"report-export\").", f.Summary);
        Assert.Contains("Thread 4 (\"report-export\") holds the read lock and is sleeping (Thread.Sleep) in PriceCatalog.ExportReport (PriceCatalog.cs:19).", f.Details);
        Assert.Equal("A writer has to wait until every reader has left, and readers that come later queue behind it. Keep the work inside read locks short: " +
                     "don't sleep, block or wait for other threads while holding one.", f.Advice);
    }

    [Fact]
    public void A_writer_behind_readers_the_dump_does_not_name_keeps_the_hint_and_gets_reader_advice()
    {
        var writer = Thread(4, [.. WriteWait, App("MyApp.Config", "Set", "Config.cs", 7)]) with { WaitingForLock = 0x200 };
        var snapshot = Process(writer) with { Locks = [Rw(readerCount: 3, waiters: [4]) with { FieldName = "Config._rw" }] };

        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Writer waiting behind readers", f.Title);
        Assert.Equal("Thread 4 is waiting for Config._rw (a ReaderWriterLockSlim), which 3 threads hold for reading. " +
                     "Readers aren't recorded per thread, so look for code that entered the read lock and is slow or blocked before ExitReadLock.", f.Summary);
        Assert.StartsWith("A writer has to wait until every reader has left", f.Advice);
    }

    [Fact]
    public void Some_readers_named_out_of_more()
    {
        var writer = Thread(6, [.. WriteWait, App("MyApp.PriceCatalog", "SetPrice", "PriceCatalog.cs", 47)], "price-update") with { WaitingForLock = 0x200 };
        var snapshot = Process(writer, Reader(4)) with { Locks = [Rw(readerCount: 2, readers: [4], waiters: [6])] };

        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Thread 6 (\"price-update\") is waiting for PriceCatalog._rw (a ReaderWriterLockSlim), which 2 threads hold for reading, among them Thread 4 (\"report-export\").", f.Summary);
    }

    [Fact]
    public void A_lock_with_no_readers_left_was_caught_while_being_handed_over()
    {
        var writer = Thread(6, [.. WriteWait, App("MyApp.PriceCatalog", "SetPrice", "PriceCatalog.cs", 47)], "price-update") with { WaitingForLock = 0x200 };
        var f = Assert.Single(new LockContentionDetector().Detect(Process(writer) with { Locks = [Rw(waiters: [6])] }, new HashSet<int>()));
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("Thread 6 (\"price-update\") is waiting for PriceCatalog._rw (a ReaderWriterLockSlim), but no thread holds it any more: the dump most likely caught the moment it was handed over.", f.Summary);

        var upgrade = Assert.Single(new LockContentionDetector().Detect(Process(Upgrader()) with { Locks = [Rw(owner: 5, upgrader: 5, readerCount: 1, waiters: [5])] }, new HashSet<int>()));
        Assert.Equal(Severity.Info, upgrade.Severity);
        Assert.Equal("Thread 5 (\"price-refresh\") is waiting to upgrade to the write lock on PriceCatalog._rw (a ReaderWriterLockSlim), but no other thread holds it for reading any more: " +
                     "the dump most likely caught the moment the last reader left.", upgrade.Summary);
    }

    [Fact]
    public void An_upgrade_waiting_for_a_reader_that_waits_for_the_upgrader_is_a_deadlock()
    {
        var upgrader = Thread(5, [.. WriteWait, App("MyApp.PriceCatalog", "Refresh", "PriceCatalog.cs", 34)], "price-refresh") with { WaitingForLock = 0x200 };
        var reader = Thread(4, [.. MonitorEnter, App("MyApp.PriceCatalog", "ExportReport", "PriceCatalog.cs", 21)], "report-export") with { WaitingForLock = 0x100 };
        var snapshot = Process(upgrader, reader) with
        {
            Locks =
            [
                new MonitorLock { Object = new ObjectRef(0x100, "System.Object"), FieldName = "PriceCatalog._auditLock", OwnerThreadId = 5, WaiterThreadIds = [4] },
                Rw(owner: 5, upgrader: 5, readerCount: 2, readers: [4], waiters: [5]),
            ],
        };

        var findings = Analyze(snapshot);
        var f = Assert.Single(findings);
        Assert.Equal("lock-deadlock", f.Id);
        Assert.Equal(Severity.Critical, f.Severity);
        Assert.Equal("Thread 4 (\"report-export\") and Thread 5 (\"price-refresh\") are each waiting for a lock the other one holds, so neither can ever continue. Start at PriceCatalog.cs:21.", f.Summary);
        Assert.Equal(
        [
            "Thread 4 (\"report-export\") holds the read lock on PriceCatalog._rw and is waiting in PriceCatalog.ExportReport (PriceCatalog.cs:21) for the lock on PriceCatalog._auditLock, which Thread 5 (\"price-refresh\") holds.",
            "Thread 5 (\"price-refresh\") holds the lock on PriceCatalog._auditLock and the upgradeable read lock on PriceCatalog._rw and is waiting in PriceCatalog.Refresh (PriceCatalog.cs:34) " +
            "to upgrade to the write lock on PriceCatalog._rw (a ReaderWriterLockSlim), which Thread 4 (\"report-export\") holds for reading.",
        ], f.Details);
        Assert.Equal([4, 5], f.ThreadIds);
    }

    [Fact]
    public void Two_upgraders_each_reading_the_others_lock_are_a_deadlock()
    {
        var prices = Rw(owner: 4, upgrader: 4, readerCount: 2, readers: [5], waiters: [4]) with { Object = new ObjectRef(0x200, "System.Threading.ReaderWriterLockSlim"), FieldName = "Catalog.Prices" };
        var stock = Rw(owner: 5, upgrader: 5, readerCount: 2, readers: [4], waiters: [5]) with { Object = new ObjectRef(0x300, "System.Threading.ReaderWriterLockSlim"), FieldName = "Catalog.Stock" };
        var snapshot = Process(
            Thread(4, [.. WriteWait, App("MyApp.Catalog", "SyncPrices", "Catalog.cs", 10)], "price-sync") with { WaitingForLock = 0x200 },
            Thread(5, [.. WriteWait, App("MyApp.Catalog", "SyncStock", "Catalog.cs", 20)], "stock-sync") with { WaitingForLock = 0x300 }) with { Locks = [prices, stock] };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("lock-deadlock", f.Id);
        Assert.Equal("Thread 4 (\"price-sync\") holds the upgradeable read lock on Catalog.Prices and the read lock on Catalog.Stock and is waiting in Catalog.SyncPrices (Catalog.cs:10) " +
                     "to upgrade to the write lock on Catalog.Prices (a ReaderWriterLockSlim), which Thread 5 (\"stock-sync\") holds for reading.", f.Details[0]);
    }

    [Fact]
    public void A_writer_behind_a_reader_that_blocks_on_a_task_the_writer_runs_is_a_wait_cycle()
    {
        var writer = Thread(6, [.. WriteWait, App("MyApp.Catalog", "Import", "Catalog.cs", 30)], "import") with { WaitingForLock = 0x200, RunningTask = 0x900 };
        var task = new TaskInfo { Address = 0x900, TypeName = "System.Threading.Tasks.Task", Description = "Task.Run(Catalog.Import)", State = TaskState.Pending };
        var reader = Thread(4, [.. ResultWait, App("MyApp.Catalog", "Report", "Catalog.cs", 12)], "report") with { BlockedOnTask = task };
        var snapshot = Process(writer, reader) with { Locks = [Rw(readerCount: 1, readers: [4], waiters: [6]) with { FieldName = "Catalog._rw" }] };

        var f = Assert.Single(Analyze(snapshot));
        Assert.Equal("wait-cycle", f.Id);
        Assert.Contains("Thread 6 (\"import\") is waiting in Catalog.Import (Catalog.cs:30) for the write lock on Catalog._rw (a ReaderWriterLockSlim), which Thread 4 (\"report\") holds for reading.", f.Details);
    }

    [Fact]
    public void A_reader_queued_behind_a_waiting_writer_is_not_waiting_for_the_readers()
    {
        var lateReader = Thread(7, [.. ReadWait, App("MyApp.PriceCatalog", "ExportReport", "PriceCatalog.cs", 17)], "late-report") with { WaitingForLock = 0x200 };
        Assert.Empty(WaitGraph.BlockingReaders(lateReader, Rw(readerCount: 1, readers: [4], waiters: [7])));
        Assert.Equal([4], WaitGraph.BlockingReaders(Upgrader(), Rw(owner: 5, upgrader: 5, readerCount: 2, readers: [4], waiters: [5])));
        // A writer queued behind the upgradeable holder waits for it, not for the readers.
        var writer = Thread(6, [.. WriteWait, App("MyApp.PriceCatalog", "SetPrice", "PriceCatalog.cs", 47)]) with { WaitingForLock = 0x200 };
        Assert.Empty(WaitGraph.BlockingReaders(writer, Rw(owner: 5, upgrader: 5, readerCount: 2, readers: [4], waiters: [5, 6])));
    }

    [Fact]
    public void A_thread_entering_upgradeable_mode_behind_a_waiting_writer_is_not_waiting_for_the_readers()
    {
        StackFrameInfo[] upgradeableWait = [Fx("System.Threading.ReaderWriterLockSlim", "WaitOnEvent"), Fx("System.Threading.ReaderWriterLockSlim", "TryEnterUpgradeableReadLockCore")];
        var lateRefresh = Thread(7, [.. upgradeableWait, App("MyApp.PriceCatalog", "RefreshIfStale", "PriceCatalog.cs", 30)], "late-refresh") with { WaitingForLock = 0x200 };
        Assert.Empty(WaitGraph.BlockingReaders(lateRefresh, Rw(readerCount: 1, readers: [4], waiters: [6, 7])));
    }

    [Fact]
    public void Readers_queued_behind_a_waiting_writer_are_named_apart_from_it()
    {
        var writer = Thread(6, [.. WriteWait, App("MyApp.PriceCatalog", "SetPrice", "PriceCatalog.cs", 47)], "price-update") with { WaitingForLock = 0x200 };
        var late = Thread(7, [.. ReadWait, App("MyApp.PriceCatalog", "ExportReport", "PriceCatalog.cs", 17)], "late-report") with { WaitingForLock = 0x200 };
        var snapshot = Process(Reader(4), writer, late) with { Locks = [Rw(readerCount: 1, readers: [4], waiters: [6, 7])] };

        var f = Assert.Single(new LockContentionDetector().Detect(snapshot, new HashSet<int>()));
        Assert.Equal("Writer waiting behind readers", f.Title);
        Assert.Equal("Thread 6 (\"price-update\") is waiting for PriceCatalog._rw (a ReaderWriterLockSlim), which 1 thread holds for reading: Thread 4 (\"report-export\").", f.Summary);
        Assert.Contains("Thread 7 (\"late-report\") is queued behind the waiting writer: a reader or upgrader that comes after a waiting writer waits for it.", f.Details);
        Assert.Equal([6, 7, 4], f.ThreadIds);
    }
}
