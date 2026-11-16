using HangSloth.Model;

namespace HangSloth.Analysis;

/// Most thread pool threads are blocked, so queued work (often the very work they
/// are waiting for) can't run.
public sealed class ThreadPoolStarvationDetector : IDetector
{
    public const int MinimumBlockedThreads = 4;

    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var workers = snapshot.Threads.Where(t => t.IsThreadPoolWorker).ToList();
        var blocked = workers
            .Select(t => (Thread: t, Kind: FrameClassifier.Classify(t.Frames)))
            .Where(x => FrameClassifier.IsBlocked(x.Kind))
            .ToList();

        var pool = snapshot.ThreadPool;
        var queued = pool?.QueuedWorkItems;

        // A periodic timer whose callback outlasts its period: each tick takes another pool thread.
        var timerCallbacks = blocked.Where(x => FrameClassifier.TimerCallback(x.Thread.Frames) is not null)
            .GroupBy(x => FrameClassifier.TimerCallback(x.Thread.Frames) is { } f ? Names.ShortMethod(f.TypeName, f.MethodName) : "")
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        var timerGroup = timerCallbacks is not null && timerCallbacks.Count() >= MinimumBlockedThreads - 1 ? timerCallbacks : null;

        // Starvation needs work that waits for a thread. With the queues known to be empty, no thread waiting on a task
        // (whose continuation would need a pool thread) and no timer adding callbacks, the threads are only parked.
        var parked = queued == 0 && timerGroup is null && !blocked.Any(x => x.Kind == BlockingKind.TaskWait);
        if (parked)
        {
            // A consumer waiting in Take or GetConsumingEnumerable of an empty BlockingCollection is idle, like an idle worker.
            // With work queued it is not: it holds a thread the queued work needs, so then it counts as blocked.
            workers = workers.Where(t => t.WaitingOnCollection is not { Adding: false }).ToList();
            blocked = blocked.Where(x => x.Thread.WaitingOnCollection is not { Adding: false }).ToList();
        }

        if (blocked.Count < MinimumBlockedThreads || blocked.Count < workers.Count * 0.75)
            yield break;

        // Group by the method that blocks, not by line and label: optimized code can resolve the same call site
        // to an earlier line and lose the frame that tells .Result from .Wait(), splitting one cause into several.
        var groups = blocked
            .GroupBy(x => (Kind: x.Kind, Method: FrameClassifier.BestUserFrame(x.Thread.Frames) is { } f ? f.FullName : ""))
            .Select(g => Site.Of(g.Key.Kind, g.ToList()))
            .OrderByDescending(g => g.Threads.Count)
            .ToList();

        var top = groups[0];
        var where = groups.Count == 1
            ? $"all in {top.Call} at {top.Spot}"
            : $"{top.Threads.Count} of them in {top.Call} at {top.Spot}";

        if (parked)
        {
            yield return Parked(blocked, workers, groups, where, pool);
            yield break;
        }

        var summary = $"Thread pool starvation: {blocked.Count} of {workers.Count} thread pool threads are blocked ({where})";
        summary += queued > 0 ? $", and {Describe.Plural(queued.Value, "work item is", "work items are")} waiting in the queue." : ".";

        var details = new List<string>();
        foreach (var g in groups.Take(5))
            details.Add($"{Describe.Plural(g.Threads.Count, "thread")}: {g.Call} at {g.Where}.");

        // Blocked on database calls: say what they wait for, since the stack of an async call only shows .Result.
        var onDatabase = blocked.Where(x => x.Thread.Database?.Waiting == DatabaseWaitKind.Query).ToList();
        if (onDatabase.Count > 0)
        {
            var sql = onDatabase.Select(x => x.Thread.Database!.CommandText).OfType<string>().GroupBy(q => q).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            var throughAsync = onDatabase.All(x => x.Kind == BlockingKind.TaskWait) ? ", through async code," : "";
            details.Add($"{Describe.Plural(onDatabase.Count, "of them is", "of them are")} waiting{throughAsync} for {onDatabase[0].Thread.Database!.Provider}" +
                        (sql is null ? "." : $" to answer {Describe.Sql(sql)}.") +
                        " A slow query is enough: each request holds a thread for as long as the query runs.");
        }

        // Most of them waiting for the same lock, semaphore or open transaction: that is what holds the pool, so name it and its holder.
        var cause = Cause.Of(snapshot, blocked);
        if (cause is not null) details.Add(cause.Sentence);

        if (pool is not null)
        {
            var atMax = workers.Count >= pool.MaxThreads;
            details.Add(atMax
                ? $"The pool is at its maximum of {pool.MaxThreads} threads, so it cannot add more. Nothing can run until a blocked thread is released."
                : $"The pool has {pool.ActiveWorkers} active workers (minimum {pool.MinThreads}). Above the minimum it only adds about one thread per second, so requests pile up.");
        }
        if (timerGroup is not null)
            details.Add($"{timerGroup.Count()} of the blocked threads are running the same timer callback ({timerGroup.Key}). A System.Threading.Timer doesn't wait for the previous callback: " +
                        "when the callback takes longer than the period, every tick takes another thread pool thread.");
        if (blocked.Any(x => x.Kind == BlockingKind.TaskWait && (WaitInsideCall(x.Thread.Frames) is null || FrameClassifier.SyncIoFrame(x.Thread.Frames) is null)))
            details.Add("Threads blocked on .Result/.Wait() are usually waiting for a task whose continuation needs a free thread pool thread to finish, which is exactly what's missing.");
        if (blocked.Any(x => x.Kind == BlockingKind.SyncIO))
            details.Add("A thread in synchronous I/O keeps its thread pool thread for as long as the other side takes to answer, and forever when it never does.");
        var syncIo = top.IoFrames is null ? null : FrameClassifier.SyncIoMethod(top.IoFrames);
        var asyncIo = top.IoFrames is null ? null : FrameClassifier.SyncIoAsyncAlternative(top.IoFrames);
        var lockAdvice = timerGroup is null && cause?.Owner is not null && top.Kind is BlockingKind.MonitorEnter or BlockingKind.ReaderWriterLock ? cause : null;

        yield return new Finding
        {
            Id = "threadpool-starvation",
            Severity = queued > 0 || blocked.Count == workers.Count ? Severity.Critical : Severity.Warning,
            Title = "Thread pool starvation",
            Summary = summary,
            Details = details,
            Advice = timerGroup is not null
                ? $"Make the timer callback ({timerGroup.Key}) non-overlapping: use a one-shot timer re-armed at the end of the callback (timer.Change(period, Timeout.Infinite)), " +
                  "a PeriodicTimer loop, or skip the tick when the previous one is still running (Monitor.TryEnter / Interlocked). And keep slow work out of the lock."
                : lockAdvice is not null
                ? lockAdvice.Advice!
                : top.Kind == BlockingKind.TaskWait && (top.Call.StartsWith('.') || top.Call.StartsWith("Task.", StringComparison.Ordinal))
                ? $"Stop blocking on async code in thread pool work: await instead of {top.Call} in {top.Spot}. Raising ThreadPool.SetMinThreads only hides the problem."
                : syncIo is not null && asyncIo is not null
                ? $"Don't do synchronous I/O on thread pool threads: await {asyncIo} instead of calling {syncIo} in {top.Spot}. Raising ThreadPool.SetMinThreads only hides the problem."
                : syncIo is not null
                ? $"Don't do synchronous I/O on thread pool threads ({syncIo} in {top.Spot}): do it on one dedicated thread and hand the results over. Raising ThreadPool.SetMinThreads only hides the problem."
                : $"Avoid blocking thread pool threads ({top.Call} at {top.Spot}); move long blocking work to a dedicated thread or make it async.",
            ThreadIds = cause?.Owner is { } owner ? [.. blocked.Select(x => x.Thread.ManagedId).Append(owner.ManagedId).Distinct()] : blocked.Select(x => x.Thread.ManagedId).ToList(),
            Location = lockAdvice is not null ? FrameClassifier.BestUserFrame(lockAdvice.Owner!.Frames)?.Source ?? top.Location : top.Location,
        };
    }

    /// Pool threads parked in long blocking calls while nothing waits for a thread: worth knowing, not a hang.
    private static Finding Parked(List<(ThreadSnapshot Thread, BlockingKind Kind)> blocked, List<ThreadSnapshot> workers, List<Site> groups, string where, ThreadPoolSnapshot? pool)
    {
        var details = groups.Take(5).Select(g => $"{Describe.Plural(g.Threads.Count, "thread")}: {g.Call} at {g.Where}.").ToList();
        details.Add("Nothing is queued, so no work is waiting for a thread: this is not starvation. But new work has to wait for a new thread" +
                    (pool is null ? "" : $", which the pool adds slowly once it is above its minimum of {pool.MinThreads}") + ".");
        return new Finding
        {
            Id = "threadpool-parked",
            Severity = Severity.Info,
            Title = "Thread pool threads parked in blocking calls",
            Summary = $"{blocked.Count} of {workers.Count} thread pool threads are parked in blocking calls ({where}), and nothing is waiting in the queue.",
            Details = details,
            Advice = "If these are long-lived loops (queue consumers, pollers, watchers), run them on dedicated threads (new Thread, or Task.Factory.StartNew with TaskCreationOptions.LongRunning) or make them async.",
            ThreadIds = blocked.Select(x => x.Thread.ManagedId).ToList(),
            Location = groups[0].Location,
        };
    }

    /// One lock, semaphore or open transaction that most of the blocked threads wait for, said in one sentence.
    /// Owner: the thread that holds the lock, when it is known; Advice: what to do about that lock.
    private sealed record Cause(string Sentence, ThreadSnapshot? Owner, string? Advice)
    {
        public static Cause? Of(ProcessSnapshot snapshot, List<(ThreadSnapshot Thread, BlockingKind Kind)> blocked)
        {
            string Who(int n) => n == blocked.Count ? "All of them are" : $"{n} of them are";
            bool Most(int n) => n * 2 > blocked.Count;

            var onLock = blocked.Where(x => x.Thread.WaitingForLock is not null).GroupBy(x => x.Thread.WaitingForLock!.Value)
                .OrderByDescending(g => g.Count()).FirstOrDefault();
            if (onLock is not null && Most(onLock.Count()) && snapshot.Locks.FirstOrDefault(l => l.Object.Address == onLock.Key) is { } l)
            {
                var owner = l.OwnerThreadId is { } id ? snapshot.FindThread(id) : null;
                if (owner is null)
                    return new Cause(l.Kind == LockKind.ReaderWriter
                        ? $"{Who(onLock.Count())} waiting for {Describe.Wanted(l)}, which {Describe.Plural(l.ReaderCount, "thread holds", "threads hold")} for reading."
                        : $"{Who(onLock.Count())} waiting for {Describe.Wanted(l)}, but the thread that took it no longer exists, so it is never released.", null, null);
                var ownerBlocked = FrameClassifier.IsBlocked(FrameClassifier.Classify(owner.Frames)) || owner.Database?.Waiting == DatabaseWaitKind.ReadingRows;
                var ownerSpot = Describe.Spot(FrameClassifier.BestUserFrame(owner.Frames));
                return new Cause(
                    $"{Who(onLock.Count())} waiting for {Describe.Wanted(l)}, held by {owner.Label}, which is {Describe.Activity(owner)}{(ownerBlocked ? " while holding it" : "")}.",
                    owner,
                    ownerBlocked
                        ? $"Don't do slow or blocking work while holding {Describe.Held(l)}, as {owner.Label} does in {ownerSpot}: copy what you need, release the lock, then do the slow part. " +
                          "Every thread pool thread that needs the lock waits until then; raising ThreadPool.SetMinThreads only adds more waiters."
                        : $"Keep the work inside {Describe.Held(l)} short ({owner.Label} is in {ownerSpot}), or use a finer-grained or reader/writer lock: every thread pool thread that needs it waits in line.");
            }

            var onSemaphore = blocked.Where(x => x.Thread.WaitingForSemaphore is not null && x.Thread.WaitingOnCollection is null)
                .GroupBy(x => x.Thread.WaitingForSemaphore!.Value).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (onSemaphore is not null && Most(onSemaphore.Count()) && snapshot.Semaphores.FirstOrDefault(s => s.Object.Address == onSemaphore.Key) is { } semaphore)
                return new Cause($"{Who(onSemaphore.Count())} waiting for {semaphore.DisplayName}, a SemaphoreSlim with {SemaphoreFacts.Slots(semaphore)}: they can only go on when the code holding its slots calls Release().", null, null);

            var onQuery = blocked.Count(x => Db.WaitingForQuery(x.Thread));
            if (Most(onQuery) && snapshot.Threads.FirstOrDefault(t => !Db.WaitingForQuery(t) && Db.InTransaction(t)) is { } blocker)
                return new Cause($"{blocker.Label} has an open transaction{Db.Session(Db.TransactionSession(blocker))} but isn't talking to the database: it is {Describe.Activity(blocker, withMethod: true)}. " +
                                 "The queries are most likely waiting for a lock that transaction holds.", null, null);
            return null;
        }
    }

    /// Blocked threads in one method, with the call and line most of them agree on.
    /// IoFrames: the stack of a thread in the synchronous I/O call named in Call, so the advice names the same call.
    private sealed record Site(BlockingKind Kind, List<(ThreadSnapshot Thread, BlockingKind Kind)> Threads, string Call, string Spot, string Where, SourceLocation? Location,
        IReadOnlyList<StackFrameInfo>? IoFrames)
    {
        public static Site Of(BlockingKind kind, List<(ThreadSnapshot Thread, BlockingKind Kind)> threads)
        {
            var frames = threads.Select(x => (x.Thread, Frame: FrameClassifier.BestUserFrame(x.Thread.Frames))).ToList();
            // A line and wait read from the app's IL is certain; one taken from the stack alone may be off in optimized code.
            var confirmed = frames.Where(x => x.Frame?.BlockingCall is not null && x.Frame.Source is not null).ToList();
            var basis = confirmed.Count > 0 ? confirmed : frames;

            var calls = basis.Select(x => WaitInsideCall(x.Thread.Frames) ?? FrameClassifier.DescribeTaskWait(x.Thread.Frames)).ToList();
            var known = calls.Where(c => c != FrameClassifier.UnknownTaskWait).ToList();
            var call = kind == BlockingKind.TaskWait ? MostCommon(known.Count > 0 ? known : calls)!
                : kind == BlockingKind.SyncIO ? MostCommon(basis.Select(x => FrameClassifier.DescribeSyncIo(x.Thread.Frames)))!
                : kind is BlockingKind.Database or BlockingKind.ConnectionPool ? MostCommon(basis.Select(x => DriverCall(x.Thread.Frames))) ?? KindName(kind)
                : NamedByCall.Contains(kind) ? MostCommon(basis.Select(x => AppCall(x.Thread.Frames))) ?? KindName(kind)
                : KindName(kind);
            var io = kind == BlockingKind.SyncIO ? basis.First(x => FrameClassifier.DescribeSyncIo(x.Thread.Frames) == call).Thread.Frames
                // A synchronous framework call that blocks on a task inside (HttpClient.Send): advise its async version.
                : kind == BlockingKind.TaskWait && basis.FirstOrDefault(x => WaitInsideCall(x.Thread.Frames) == call && FrameClassifier.SyncIoFrame(x.Thread.Frames) is not null) is { Thread: { } t }
                    ? t.Frames
                : null;

            var sources = basis.Select(x => x.Frame?.Source).Distinct().ToList();
            var frame = basis[0].Frame;
            if (sources.Count == 1 || confirmed.Count > 0)
            {
                var source = MostCommon(basis.Select(x => x.Frame?.Source));
                frame = basis.First(x => Equals(x.Frame?.Source, source)).Frame;
                return new Site(kind, threads, call, Describe.Spot(frame), Describe.Where(frame), frame?.Source, io);
            }
            // The threads disagree on the line and nothing confirms one: name the method rather than a line that may be wrong.
            var method = frame is null ? null : frame with { Source = null };
            return new Site(kind, threads, call, Describe.Spot(method), Describe.Where(method), null, io);
        }

        private static T? MostCommon<T>(IEnumerable<T> values) =>
            values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
    }

    /// Waits named after the framework method the app called ("ManualResetEventSlim.Wait", "HttpClient.Send")
    /// rather than the primitive inside it ("Monitor.Wait"). Database waits are named by DriverCall.
    private static readonly HashSet<BlockingKind> NamedByCall =
    [
        BlockingKind.MonitorWait, BlockingKind.Sleep, BlockingKind.Join, BlockingKind.SemaphoreWait, BlockingKind.WaitHandle,
        BlockingKind.ReaderWriterLock,
    ];

    /// The outermost framework frame, the one the app's own code called; a property getter is named as the property.
    /// Null when no app code is on the stack: then the outermost frame is the thread's own start.
    private static string? AppCall(IReadOnlyList<StackFrameInfo> frames) =>
        FrameClassifier.FirstUserFrame(frames) is not null && FrameClassifier.FrameworkPrefix(frames).LastOrDefault(f => !f.IsRuntimeFrame) is { } f
            ? Name(f)
            : null;

    /// The database driver's outermost frame ("NpgsqlCommand.ExecuteNonQuery"), not a helper above it such as Enumerable.ToList.
    private static string? DriverCall(IReadOnlyList<StackFrameInfo> frames) =>
        FrameClassifier.FrameworkPrefix(frames).LastOrDefault(f => !f.IsRuntimeFrame && FrameClassifier.DatabaseProvider(f.TypeName) is not null) is { } f
            ? Name(f)
            : null;

    private static string Name(StackFrameInfo f) =>
        Names.ShortMethod(f.TypeName, f.MethodName.StartsWith("get_", StringComparison.Ordinal) ? f.MethodName[4..] : f.MethodName);

    /// A task wait inside a framework method the app called (HttpClient.Send blocks on its own async code): that
    /// method, which is what the app's code shows. Null when the app itself blocks on the task.
    private static string? WaitInsideCall(IReadOnlyList<StackFrameInfo> frames)
    {
        if (FrameClassifier.FirstUserFrame(frames) is not { BlockingCall: null }) return null;
        var callee = FrameClassifier.FrameworkPrefix(frames).LastOrDefault(f => !f.IsRuntimeFrame);
        if (callee is null || callee.TypeName.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal)
                           || callee.TypeName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal))
            return null;
        return AppCall(frames);
    }

    private static string KindName(BlockingKind kind) => kind switch
    {
        BlockingKind.MonitorEnter => "lock (Monitor.Enter)",
        BlockingKind.MonitorWait => "Monitor.Wait",
        BlockingKind.Sleep => "Thread.Sleep",
        BlockingKind.Join => "Thread.Join",
        BlockingKind.SemaphoreWait => "SemaphoreSlim.Wait",
        BlockingKind.WaitHandle => "WaitHandle.WaitOne",
        BlockingKind.ReaderWriterLock => "ReaderWriterLockSlim",
        BlockingKind.TypeInitializer => "a static constructor",
        BlockingKind.Finalizers => "GC.WaitForPendingFinalizers",
        BlockingKind.CancellationCallback => "CancellationTokenRegistration.Dispose",
        BlockingKind.ProcessExit => "Process.WaitForExit",
        BlockingKind.Database => "a database call",
        BlockingKind.ConnectionPool => "the connection pool",
        BlockingKind.SyncIO => "synchronous I/O",
        _ => kind.ToString(),
    };
}
