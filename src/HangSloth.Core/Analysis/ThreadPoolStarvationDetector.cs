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

        if (blocked.Count < MinimumBlockedThreads || blocked.Count < workers.Count * 0.75)
            yield break;

        var pool = snapshot.ThreadPool;
        var queued = pool?.QueuedWorkItems;

        var groups = blocked
            .GroupBy(x => (Call: x.Kind == BlockingKind.TaskWait ? FrameClassifier.DescribeTaskWait(x.Thread.Frames) : KindName(x.Kind),
                           Site: Describe.Spot(FrameClassifier.BestUserFrame(x.Thread.Frames))))
            .OrderByDescending(g => g.Count())
            .ToList();

        var top = groups[0];
        var where = groups.Count == 1
            ? $"all in {top.Key.Call} at {top.Key.Site}"
            : $"{top.Count()} of them in {top.Key.Call} at {top.Key.Site}";

        var summary = $"Thread pool starvation: {blocked.Count} of {workers.Count} thread pool threads are blocked ({where})";
        summary += queued > 0 ? $", and {Describe.Plural(queued.Value, "work item")} are waiting in the queue." : ".";

        var details = new List<string>();
        foreach (var g in groups.Take(5))
            details.Add($"{Describe.Plural(g.Count(), "thread")}: {g.Key.Call} at {g.Key.Site}.");

        if (pool is not null)
        {
            var atMax = workers.Count >= pool.MaxThreads;
            details.Add(atMax
                ? $"The pool is at its maximum of {pool.MaxThreads} threads, so it cannot add more. Nothing can run until a blocked thread is released."
                : $"The pool has {pool.ActiveWorkers} active workers (minimum {pool.MinThreads}). Above the minimum it only adds about one thread per second, so requests pile up.");
        }
        if (blocked.Any(x => x.Kind == BlockingKind.TaskWait))
            details.Add("Threads blocked on .Result/.Wait() are usually waiting for a task whose continuation needs a free thread pool thread to finish, which is exactly what's missing.");

        var site = FrameClassifier.BestUserFrame(top.First().Thread.Frames);
        yield return new Finding
        {
            Id = "threadpool-starvation",
            Severity = queued > 0 || blocked.Count == workers.Count ? Severity.Critical : Severity.Warning,
            Title = "Thread pool starvation",
            Summary = summary,
            Details = details,
            Advice = top.Key.Call.StartsWith('.') || top.Key.Call.StartsWith("Task.", StringComparison.Ordinal)
                ? $"Stop blocking on async code in thread pool work: await instead of {top.Key.Call} in {top.Key.Site}. Raising ThreadPool.SetMinThreads only hides the problem."
                : $"Avoid blocking thread pool threads ({top.Key.Call} at {top.Key.Site}); move long blocking work to a dedicated thread or make it async.",
            ThreadIds = blocked.Select(x => x.Thread.ManagedId).ToList(),
            Location = site?.Source,
        };
    }

    private static string KindName(BlockingKind kind) => kind switch
    {
        BlockingKind.MonitorEnter => "lock (Monitor.Enter)",
        BlockingKind.MonitorWait => "Monitor.Wait",
        BlockingKind.Sleep => "Thread.Sleep",
        BlockingKind.Join => "Thread.Join",
        BlockingKind.SemaphoreWait => "SemaphoreSlim.Wait",
        BlockingKind.WaitHandle => "WaitHandle.WaitOne",
        _ => kind.ToString(),
    };
}
