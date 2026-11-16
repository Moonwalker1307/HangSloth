using HangSloth.Model;

namespace HangSloth.Analysis;

/// Small phrasing helpers shared by the detectors.
internal static class Describe
{
    public static string Where(StackFrameInfo? frame)
    {
        if (frame is null) return "an unknown place";
        var method = Names.ShortMethod(frame.TypeName, frame.MethodName);
        return frame.Source is null ? method : $"{method} ({frame.Source})";
    }

    /// "OrderService.cs:88", or the method name when there's no PDB.
    public static string Spot(StackFrameInfo? frame) =>
        frame?.Source?.ToString() ?? (frame is null ? "unknown code" : Names.ShortMethod(frame.TypeName, frame.MethodName));

    public static string Task(TaskInfo task) =>
        task.AwaitLocation is null ? task.Description : $"{task.Description} (paused at {task.AwaitLocation})";

    /// <param name="withMethod">"in Refunds.Charge (Refunds.cs:15)" rather than just "in Refunds.cs:15".</param>
    public static string Activity(ThreadSnapshot thread, bool withMethod = false)
    {
        var kind = FrameClassifier.Classify(thread.Frames);
        var at = withMethod ? Where(FrameClassifier.BestUserFrame(thread.Frames)) : Spot(FrameClassifier.BestUserFrame(thread.Frames));
        if (thread.Database is { Waiting: DatabaseWaitKind.ReadingRows } reading)
            return $"reading the rows of {Sql(reading.CommandText)} from {reading.Provider}" +
                   (reading.RowsRead is { } rows ? $" ({rows:N0} rows so far)" : "") + $" in {at}";
        if (thread.WaitingOnCollection is { } c)
            return $"waiting to {(c.Adding ? "add to" : "take from")} {c.DisplayName} ({(c.Adding ? "full" : "empty")} BlockingCollection) in {at}";
        return kind switch
        {
            BlockingKind.Sleep => $"sleeping (Thread.Sleep) in {at}",
            BlockingKind.TaskWait => $"blocked on {FrameClassifier.DescribeTaskWait(thread.Frames)} in {at}",
            BlockingKind.MonitorEnter => $"itself waiting for another lock in {at}",
            BlockingKind.MonitorWait when FrameClassifier.FrameworkPrefix(thread.Frames).Any(f => f.TypeName == "System.Threading.ManualResetEventSlim")
                => $"waiting for a ManualResetEventSlim to be set in {at}",
            BlockingKind.MonitorWait => $"waiting in Monitor.Wait in {at}",
            BlockingKind.Join => $"waiting for another thread to finish (Thread.Join) in {at}",
            BlockingKind.SemaphoreWait => $"waiting on a SemaphoreSlim in {at}",
            BlockingKind.WaitHandle => $"waiting on a WaitHandle in {at}",
            BlockingKind.ReaderWriterLock => $"itself waiting for a ReaderWriterLockSlim in {at}",
            BlockingKind.TypeInitializer => $"waiting for another thread's static constructor in {at}",
            BlockingKind.Database => $"waiting for the database in {at}",
            BlockingKind.ConnectionPool when Db.IsConnecting(thread) => $"opening a new database connection in {at}",
            BlockingKind.ConnectionPool => $"waiting for a database connection from the pool in {at}",
            BlockingKind.Finalizers => $"waiting for the finalizer thread (GC.WaitForPendingFinalizers) in {at}",
            BlockingKind.CancellationCallback => $"waiting for a running cancellation callback (CancellationTokenRegistration.Dispose) in {at}",
            BlockingKind.ProcessExit => $"waiting for a child process to exit in {at}",
            BlockingKind.SyncIO => $"blocked in {FrameClassifier.DescribeSyncIo(thread.Frames)} in {at}",
            _ => $"running {at}",
        };
    }

    /// "Db.Open (Db.cs:26), called from AuditTrail.Write (PoolExhaustion.cs:26)": for helpers, the caller is what matters.
    public static string WhereWithCaller(IReadOnlyList<StackFrameInfo> frames)
    {
        var user = frames.Where(FrameClassifier.IsUserFrame).Take(2).ToList();
        if (user.Count == 0) return "an unknown place";
        var best = FrameClassifier.BestUserFrame(frames);
        return user.Count == 2 && best == user[0] ? $"{Where(user[0])}, called from {Where(user[1])}" : Where(best);
    }

    /// The lock as its owner holds it: "the lock on X", or "the write lock on X" (or "the upgradeable read
    /// lock on X") for a ReaderWriterLockSlim.
    public static string Held(MonitorLock l) =>
        l.Kind != LockKind.ReaderWriter ? $"the lock on {l.DisplayName}"
        : l.UpgraderThreadId is { } upgrader && upgrader == l.OwnerThreadId ? $"the upgradeable read lock on {l.DisplayName}"
        : $"the write lock on {l.DisplayName}";

    /// Every lock a thread holds: as owner, and ReaderWriterLockSlim read locks the dump shows it holding.
    public static List<string> HeldBy(ProcessSnapshot snapshot, int thread) =>
        snapshot.Locks.Where(l => l.OwnerThreadId == thread).Select(Held)
            .Concat(snapshot.Locks.Where(l => l.Kind == LockKind.ReaderWriter && l.OwnerThreadId != thread && l.ReaderThreadIds.Contains(thread))
                .Select(l => $"the read lock on {l.DisplayName}"))
            .ToList();

    /// What a thread waits for on a lock and who holds it up: "for the lock on X, which Thread 5 holds", or
    /// for a ReaderWriterLockSlim held up by a reader "to upgrade to the write lock on X, which Thread 4 holds for reading".
    public static string WaitFor(MonitorLock l, int waiter, int holder, string holderLabel)
    {
        if (l.Kind != LockKind.ReaderWriter || l.OwnerThreadId == holder || !l.ReaderThreadIds.Contains(holder))
            return $"for {Wanted(l)}, which {holderLabel} holds";
        var upgrading = l.UpgraderThreadId == waiter;
        return $"{(upgrading ? "to upgrade to" : "for")} the write lock on {l.DisplayName} (a ReaderWriterLockSlim), which {holderLabel} holds for reading";
    }

    /// The lock as a thread waits for it.
    public static string Wanted(MonitorLock l) => l.Kind == LockKind.ReaderWriter ? $"{l.DisplayName} (a ReaderWriterLockSlim)" : $"the lock on {l.DisplayName}";

    /// SQL squeezed onto one line and kept short enough for a sentence.
    public static string Sql(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "a command";
        var oneLine = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"`{(oneLine.Length > 160 ? oneLine[..157] + "..." : oneLine)}`";
    }

    public static string Plural(int n, string singular, string? plural = null) => n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";

    public static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => string.Join(", ", list.Take(list.Count - 1)) + $" and {list[^1]}",
        };
    }

    /// An async method of the app itself (not of the BCL or a library like a database driver).
    /// "Thread 5 is running X's continuation inline, inside the SetResult call in Y (file:line)."
    public static string? InlineContinuation(ThreadSnapshot t) =>
        FrameClassifier.InlineContinuation(t.Frames) is var (continuation, completer)
            ? $"{t.Label} is running the rest of {Names.ShortMethod(continuation.TypeName, continuation.MethodName)} inline: {Where(completer)} completed the task it was awaiting, " +
              "and the await continued right there, on the same thread, with every lock the completing code holds."
            : null;

    public const string InlineContinuationAdvice =
        " The second lock is taken by an await continuation that ran inline inside SetResult: create the TaskCompletionSource with " +
        "TaskCreationOptions.RunContinuationsAsynchronously, or complete it after releasing the lock.";

    public static bool IsAppAsyncMethod(TaskInfo task)
    {
        if (task.Kind != TaskKind.AsyncMethod) return false;
        const string marker = "AsyncStateMachineBox<";
        var at = task.TypeName.LastIndexOf(marker, StringComparison.Ordinal);
        return at >= 0 && !FrameClassifier.IsFrameworkType(task.TypeName[(at + marker.Length)..]);
    }

    /// Walk an async chain from the waited task to the innermost thing it is waiting for.
    public static IEnumerable<TaskInfo> Chain(TaskInfo task)
    {
        for (var t = task; t is not null; t = t.Awaiting)
            yield return t;
    }
}
