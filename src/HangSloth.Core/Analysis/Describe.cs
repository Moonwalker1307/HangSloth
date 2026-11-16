using System.Globalization;
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
                   (reading.RowsRead is { } rows ? $" ({Number(rows)} rows so far)" : "") + $" in {at}";
        if (thread.WaitingOnCollection is { } c)
            return $"waiting to {(c.Adding ? "add to" : "take from")} {c.DisplayName} ({(c.Adding ? "full" : "empty")} BlockingCollection) in {at}";
        return kind switch
        {
            BlockingKind.Sleep => $"sleeping (Thread.Sleep) in {at}",
            BlockingKind.TaskWait when IsAsyncMainWait(thread.Frames) => thread.BlockedOnTask?.AwaitLocation is { } paused
                ? $"waiting for the app's async Main, which is paused at {paused}"
                : "waiting for the app's async Main to finish",
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

    /// "12,345": the same on every machine, whatever its language settings, so a report reads the same everywhere.
    public static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    /// "1.5", with at most one decimal and a point, on every machine.
    public static string Number(double n) => n.ToString("0.#", CultureInfo.InvariantCulture);

    public static string Plural(int n, string singular, string? plural = null) => n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";

    /// How many threads a sentence names before it says how many more there are.
    public const int MaxListed = 5;

    /// A list is cut only when that leaves out at least two: "and 1 more" would be longer than the one label.
    private static bool Fits(int count) => count <= MaxListed + 1;

    /// "Thread 4, Thread 5 and Thread 6", and for a long list the first few: "Thread 4, ..., Thread 8 and 25 more".
    public static string Labels(IEnumerable<ThreadSnapshot> threads)
    {
        var list = threads.ToList();
        return Fits(list.Count)
            ? JoinAnd(list.Select(t => t.Label))
            : string.Join(", ", list.Take(MaxListed).Select(t => t.Label)) + $" and {list.Count - MaxListed} more";
    }

    /// Threads in the same wait, as the subject of a sentence: the label of one thread, or
    /// "30 "import-worker" threads (Thread 5, ..., Thread 9 and 25 more)".
    public static string Many(IReadOnlyList<ThreadSnapshot> threads)
    {
        if (threads.Count == 1) return threads[0].Label;
        var name = threads[0].Name;
        if (name is { Length: > 0 } && threads.All(t => t.Name == name))
        {
            var ids = threads.Select(t => $"Thread {t.ManagedId}").ToList();
            var list = Fits(ids.Count) ? JoinAnd(ids) : string.Join(", ", ids.Take(MaxListed)) + $" and {ids.Count - MaxListed} more";
            return $"{threads.Count} \"{name}\" threads ({list})";
        }
        return $"{threads.Count} threads ({Labels(threads)})";
    }

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

    /// The entry point the compiler writes for async Main and top-level await: "&lt;Main&gt;" calls the app's Main and
    /// blocks on its task with GetAwaiter().GetResult() for as long as the app runs. The app didn't write that wait.
    public static bool IsAsyncMainWait(IReadOnlyList<StackFrameInfo> frames) =>
        FrameClassifier.FirstUserFrame(frames) is { MethodName: "<Main>" };

    /// How one link of a task chain leads to the next: an async method awaits it, a ContinueWith task runs
    /// after it, and a wrapper (Task.Run(async ...), WaitAsync) only waits for it.
    public static string Link(TaskInfo from) => from.Kind switch
    {
        TaskKind.Continuation => "which runs after",
        TaskKind.Wrapper => "which waits for",
        _ => "which awaits",
    } + (from.Awaiting is { } next ? FirstOf(next) : "");

    /// " the first of its 3 tasks:" before the task that stands for a Task.WhenAny or Task.WaitAny, so that the
    /// sentence doesn't read as if it waited for that one task only.
    public static string FirstOf(TaskInfo task) => task.FirstOf is { } n ? $" the first of its {n} tasks:" : "";

    /// A chain as a sentence should tell it: without the links that only repeat their neighbour.
    public static List<TaskInfo> ReadableChain(TaskInfo task)
    {
        // Task.Run(async ...) whose lambda hasn't reached its first await yet: the lambda is still running as
        // the Task.Run task behind the wrapper, so naming both would say "Task.Run" twice.
        var chain = Chain(task)
            .Where(t => !(t.Kind == TaskKind.Wrapper && t.Awaiting?.Kind is TaskKind.Delegate or TaskKind.Queued && t.Description.StartsWith("Task.Run(", StringComparison.Ordinal)))
            .ToList();
        // SemaphoreSlim.WaitAsync with a timeout or a token waits through an async method of its own and a
        // WaitAsync(...) wrapper. The code that called WaitAsync is the link before those, as with a plain WaitAsync().
        while (chain.Count >= 2 && chain[^1].Kind == TaskKind.SemaphoreWait && IsSemaphoreWaitPlumbing(chain[^2]))
            chain.RemoveAt(chain.Count - 2);
        return chain;
    }

    private static bool IsSemaphoreWaitPlumbing(TaskInfo task) =>
        task.Kind is TaskKind.Wrapper or TaskKind.Continuation
        || task.TypeName.Contains("System.Threading.SemaphoreSlim+<WaitUntilCountOrTimeoutAsync>", StringComparison.Ordinal);

    /// Walk an async chain from the waited task to the innermost thing it is waiting for.
    public static IEnumerable<TaskInfo> Chain(TaskInfo task)
    {
        for (var t = task; t is not null; t = t.Awaiting)
            yield return t;
    }
}
