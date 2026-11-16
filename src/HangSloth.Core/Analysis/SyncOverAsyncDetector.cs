using HangSloth.Model;

namespace HangSloth.Analysis;

/// A thread that owns a single-threaded SynchronizationContext blocks on a task whose
/// continuation needs that same context to run. Neither can make progress.
public sealed class SyncOverAsyncDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        foreach (var thread in snapshot.Threads)
        {
            if (claimed.Contains(thread.ManagedId) || thread.BlockedOnTask is not { } waited || thread.SynchronizationContext is not { } ctx)
                continue;

            var chain = Describe.Chain(waited).ToList();
            var elsewhere = RunsContinuationsElsewhere(ctx);
            // Every method in the chain that captured the context needs it to finish; the one already sitting
            // in the context's queue is where it is literally stuck. A context that runs continuations on other
            // threads is stuck only when one is already in its queue and none of its threads gets to it.
            var capturing = chain.Where(t => t.Resume?.SynchronizationContext.Address == ctx.Address && (!elsewhere || t.Resume!.Status == ResumeStatus.Queued)).ToList();
            var stuck = capturing.LastOrDefault(t => t.Resume!.Status == ResumeStatus.Queued) ?? capturing.FirstOrDefault();
            var call = FrameClassifier.DescribeTaskWait(thread.Frames);
            var site = FrameClassifier.BestUserFrame(thread.Frames);

            if (stuck is not null)
            {
                yield return Confirmed(thread, ctx, waited, stuck, capturing, call, site);
            }
            else if (!elsewhere && waited.State == TaskState.Pending && waited.Kind == TaskKind.AsyncMethod && ctx.TypeName != "System.Threading.SynchronizationContext")
            {
                yield return new Finding
                {
                    Id = "sync-over-async-risk",
                    Severity = Severity.Warning,
                    Title = "Likely sync-over-async",
                    Summary = $"{thread.Label} is blocking with {call} on {waited.Description} while it owns a {ctx.ShortTypeName}. " +
                              $"If anything inside that async call resumes on the captured context, this is a deadlock. In {Describe.Spot(site)}.",
                    Details = [$"The task is still running: {Describe.JoinAnd(chain.Select(Describe.Task))}."],
                    Advice = $"Use await instead of {call}, or ConfigureAwait(false) inside {waited.Description}.",
                    ThreadIds = [thread.ManagedId],
                    Location = site?.Source,
                };
            }
        }
    }

    /// Test runners' contexts that don't own a thread: xunit's AsyncTestSyncContext (v2 and v3) only counts a test's
    /// async work and posts each continuation on to the thread pool or to the context it wraps, and
    /// MaxConcurrencySyncContext runs continuations on a pool of threads of its own. A test blocking on async code
    /// under them is waiting, not deadlocked, unlike under a UI context or one that pumps a single thread.
    private static readonly string[] ContextsThatPostElsewhere = ["AsyncTestSyncContext", "MaxConcurrencySyncContext"];

    internal static bool RunsContinuationsElsewhere(ObjectRef ctx) =>
        ContextsThatPostElsewhere.Any(name => ctx.TypeName == name || ctx.TypeName.EndsWith("." + name, StringComparison.Ordinal));

    private static Finding Confirmed(ThreadSnapshot thread, ObjectRef ctx, TaskInfo waited, TaskInfo stuck, List<TaskInfo> capturing, string call, StackFrameInfo? site)
    {
        var details = new List<string>
        {
            $"{thread.Label} runs the {ctx.ShortTypeName}, and it is stuck inside {call} in {Describe.Where(site)}.",
            $"It is waiting for {Describe.Task(waited)}" +
            (stuck == waited ? "." : $", which is waiting for {Describe.Task(stuck)}."),
        };

        var awaitedName = stuck.Awaiting?.Description ?? "the operation it awaits";
        details.Add(stuck.Resume!.Status == ResumeStatus.Queued
            ? $"{awaitedName} has already finished, and the rest of {stuck.Description} has been posted to the {ctx.ShortTypeName}. It is sitting in that queue, waiting for {thread.Label}, which will never get to it."
            : $"When {awaitedName} finishes, the rest of {stuck.Description} has to run on the {ctx.ShortTypeName}, i.e. on {thread.Label}, which is blocked waiting for it.");

        var where = site?.Source is null ? "" : $", in {site.Source}";
        return new Finding
        {
            Id = "sync-over-async-deadlock",
            Severity = Severity.Critical,
            Title = "Deadlock: sync-over-async",
            Summary = $"{thread.Label} is blocking with {call} on a task that's waiting for the same SynchronizationContext. Classic sync-over-async{where}.",
            Details = details,
            Advice = $"Make the caller async and await the task instead of using {call}. " +
                     $"If that isn't possible yet, add .ConfigureAwait(false) to the awaits in {Describe.JoinAnd(capturing.Select(t => t.Description).Reverse())} (and everything it calls) so they don't need the blocked context.",
            ThreadIds = [thread.ManagedId],
            Location = site?.Source,
        };
    }
}
