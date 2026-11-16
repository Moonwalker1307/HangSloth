using HangSloth.Model;

namespace HangSloth.Analysis;

/// Threads blocked in SemaphoreSlim.Wait on "mutex" semaphores (max 1) that are all taken. A SemaphoreSlim has no owner,
/// so a dump can't say who took one, and two taken semaphores with a waiter each are just as often two idle workers on
/// their own signals. The waiting code can say more: a thread whose own code took one of these semaphores earlier and
/// hasn't released it since holds it (<see cref="SemaphoreUse.Holds"/>). Only when each thread waits for one another
/// holds is it reported as threads taking them in opposite order. A thread whose code couldn't be followed, or whose code
/// doesn't show what the semaphore is, may hold any of them; a circle that needs such a thread is reported as a possible
/// deadlock, a Warning. Other waits are left to
/// <see cref="SemaphoreWaitDetector"/>, and a worker waiting for its signal is in no circle.
public sealed class SemaphoreDeadlockDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var threads = new Dictionary<int, ThreadSnapshot>();
        foreach (var t in snapshot.Threads) threads.TryAdd(t.ManagedId, t);
        var semaphores = new Dictionary<ulong, SemaphoreInfo>();
        foreach (var s in snapshot.Semaphores) semaphores.TryAdd(s.Object.Address, s);
        var taken = snapshot.Semaphores
            .Where(s => s.CurrentCount == 0 && s.MaxCount == 1)
            .Select(s => (Semaphore: s, Waiters: s.WaiterThreadIds
                .Where(id => !claimed.Contains(id) && !SemaphoreFacts.WaitsOnCollection(threads.GetValueOrDefault(id))
                             && SemaphoreFacts.Role(threads.GetValueOrDefault(id)?.SemaphoreUse, s) != SemaphoreRole.Signal)
                .ToList()))
            .Where(x => x.Waiters.Count > 0)
            .ToList();
        if (taken.Count < 2) yield break;

        // A thread is in the circle when following "waits for a semaphore that thread holds" from it leads back to it.
        // A thread on the way from one circle to another waits for one of them, but isn't part of the deadlock.
        var waitsFor = taken.SelectMany(x => x.Waiters.Select(id => (Id: id, x.Semaphore.Object.Address)))
            .GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First().Address);
        var uses = waitsFor.Keys.ToDictionary(id => id, id => threads.GetValueOrDefault(id)?.SemaphoreUse);
        var holds = waitsFor.Keys.ToDictionary(id => id, id => uses[id]?.Holds ?? []);
        var holders = waitsFor.Keys.SelectMany(id => holds[id].Select(h => (h.Address, Id: id))).Distinct().ToLookup(h => h.Address, h => h.Id);
        var proven = waitsFor.Keys.ToDictionary(id => id, id => holders[waitsFor[id]].Where(o => o != id && waitsFor.ContainsKey(o)).ToList());
        var circle = InCircles(proven);
        if (circle.Count >= 2)
            yield return Deadlock(threads, semaphores, snapshot, taken, circle, waitsFor, holds, possible: false);

        // Possibly blocked by a thread whose code couldn't be followed, or whose code doesn't show what it is: it may hold
        // what this one waits for (taken in another method, such as a lock class's Enter()), unless it waits for that same
        // one-slot semaphore itself. Every such thread blocks every waiter on another semaphore; one stand-in node that
        // they all are reached through keeps that from becoming an edge for each pair, so thousands of waiters cost little.
        var rest = waitsFor.Keys.Where(id => !circle.Contains(id)).ToHashSet();
        var mayHold = rest.Where(id => uses[id] is null || SemaphoreFacts.UnknownOnce(uses[id], semaphores[waitsFor[id]])).ToList();
        var mayHoldOn = mayHold.Select(id => waitsFor[id]).Distinct().Take(2).ToList();
        var maybe = rest.ToDictionary(id => id, id => proven[id].Where(rest.Contains).ToList());
        if (mayHold.Count > 0)
        {
            maybe[StandIn] = mayHold;
            foreach (var id in rest)
                if (mayHoldOn.Any(s => s != waitsFor[id])) maybe[id].Add(StandIn);
        }
        var possibleCircle = InCircles(maybe);
        possibleCircle.Remove(StandIn);
        if (possibleCircle.Count >= 2)
            yield return Deadlock(threads, semaphores, snapshot, taken, possibleCircle, waitsFor, holds, possible: true);
    }

    /// The node every thread that may hold any of the semaphores is reached through; no thread has this id.
    private const int StandIn = int.MinValue;

    private static Finding Deadlock(Dictionary<int, ThreadSnapshot> threads, Dictionary<ulong, SemaphoreInfo> semaphores, ProcessSnapshot snapshot,
                                    List<(SemaphoreInfo Semaphore, List<int> Waiters)> taken, HashSet<int> circle,
                                    Dictionary<int, ulong> waitsFor, Dictionary<int, IReadOnlyList<HeldSemaphore>> holds, bool possible)
    {
        taken = taken.Where(x => x.Waiters.Any(circle.Contains)).Select(x => (x.Semaphore, Waiters: x.Waiters.Where(circle.Contains).ToList())).ToList();
        var waitedInCircle = circle.Select(id => waitsFor[id]).ToHashSet();

        var waiters = taken.SelectMany(x => x.Waiters).Distinct().ToList();
        var anyoneElseRunning = SemaphoreFacts.AppCodeRunningOutside(snapshot, waiters);
        var details = taken
            .SelectMany(x => x.Waiters.Select(id => (Thread: threads[id], x.Semaphore)))
            .SelectMany(x => new[] { $"{x.Thread.Label} is waiting in {Describe.Where(FrameClassifier.BestUserFrame(x.Thread.Frames))} for {x.Semaphore.DisplayName} (0 of 1 free)." }
                .Concat(holds[x.Thread.ManagedId]
                    .Where(h => h.Address != waitsFor[x.Thread.ManagedId]
                        ? waitedInCircle.Contains(h.Address)
                        : circle.Any(o => o != x.Thread.ManagedId && waitsFor[o] == h.Address))
                    .Select(h => $"{x.Thread.Label} took {semaphores[h.Address].DisplayName} earlier in {h.Method}{(h.Location is null ? "" : $" ({h.Location})")} and doesn't release it before this wait.")))
            .ToList();
        var names = taken.Select(x => x.Semaphore.DisplayName).ToList();
        // Different semaphores that the dump can't tell apart: "a SemaphoreSlim and a SemaphoreSlim" would read as one.
        var alike = names.Count > 1 && names.Distinct().Count() == 1;
        var listed = alike ? $"{names.Count} different semaphores, each {names[0]}" : Describe.JoinAnd(names);
        var these = alike ? "these semaphores" : Describe.JoinAnd(names);
        var site = FrameClassifier.BestUserFrame(threads[waiters[0]].Frames);
        var who = Describe.JoinAnd(waiters.Select(id => threads[id].Label));
        var start = site?.Source is null ? "" : $" Start at {site.Source}.";

        if (possible)
        {
            var unknown = waiters.Where(id => threads[id].SemaphoreUse is null).Select(id => threads[id].Label).ToList();
            var unclear = waiters.Where(id => threads[id].SemaphoreUse is { } use && SemaphoreFacts.UnknownOnce(use, semaphores[waitsFor[id]]))
                .Select(id => threads[id].Label).ToList();
            var why = string.Join(", and ", new[]
            {
                unknown.Count == 0 ? null : $"the code that waits in {Describe.JoinAnd(unknown)} couldn't be followed (for example a helper method the runtime inlined)",
                unclear.Count == 0 ? null : $"the code that waits in {Describe.JoinAnd(unclear)} doesn't show whether it took one of them earlier (for example through a lock class's Enter())",
            }.OfType<string>());
            details.Add("Workers that each wait for a signal of their own, a SemaphoreSlim created with no free slot, look the same in a dump.");
            return new Finding
            {
                Id = "semaphore-deadlock",
                Severity = Severity.Warning,
                Title = "Possible deadlock: SemaphoreSlim locks taken in opposite order",
                Summary = $"{who} are each waiting for a SemaphoreSlim(1, 1) that is already taken ({listed}). If each of them holds the one another is waiting for, " +
                          $"they are deadlocked, but the dump can't confirm it: a SemaphoreSlim doesn't record who holds it, and {why}." + start,
                Details = details,
                Advice = "Check at the lines above whether the code holds one of these semaphores while it waits for another; if it does, take them in one consistent order everywhere, " +
                         "or replace them with a single semaphore.",
                ThreadIds = waiters,
                Location = site?.Source,
            };
        }

        details.Add(anyoneElseRunning
            ? "Some other app code is still running, so one of them may yet release a semaphore; if this persists across two dumps, it's a deadlock."
            : "No other thread is running app code that could release them.");
        return new Finding
        {
            Id = "semaphore-deadlock",
            Severity = anyoneElseRunning ? Severity.Warning : Severity.Critical,
            Title = "Deadlock: SemaphoreSlim locks taken in opposite order",
            Summary = $"{who} are each waiting for a SemaphoreSlim(1, 1) that is already taken " +
                      $"({listed}). A semaphore has no owner, so nothing reports this, but each of them most likely holds the one another is waiting for." +
                      start,
            Details = details,
            Advice = $"Take {these} in one consistent order everywhere, or replace them with a single semaphore. " +
                     "Unlike lock, SemaphoreSlim isn't tied to a thread, so neither the runtime nor a debugger will point at the holder.",
            ThreadIds = waiters,
            Location = site?.Source,
        };
    }

    /// The threads from which "blocked by" leads back to themselves: the members of every strongly connected group of two
    /// or more (Tarjan's algorithm, without recursion, so thousands of waiters cost little).
    private static HashSet<int> InCircles(Dictionary<int, List<int>> blockedBy)
    {
        var index = new Dictionary<int, int>();
        var low = new Dictionary<int, int>();
        var stack = new Stack<int>();
        var onStack = new HashSet<int>();
        var result = new HashSet<int>();
        foreach (var root in blockedBy.Keys)
        {
            if (index.ContainsKey(root)) continue;
            var work = new Stack<(int Node, int Next)>();
            work.Push((root, 0));
            index[root] = low[root] = index.Count;
            stack.Push(root);
            onStack.Add(root);
            while (work.TryPop(out var frame))
            {
                var (node, next) = frame;
                var edges = blockedBy[node];
                if (next < edges.Count)
                {
                    work.Push((node, next + 1));
                    var to = edges[next];
                    if (!index.ContainsKey(to))
                    {
                        index[to] = low[to] = index.Count;
                        stack.Push(to);
                        onStack.Add(to);
                        work.Push((to, 0));
                    }
                    else if (onStack.Contains(to))
                    {
                        low[node] = Math.Min(low[node], index[to]);
                    }
                    continue;
                }
                if (work.TryPeek(out var parent)) low[parent.Node] = Math.Min(low[parent.Node], low[node]);
                if (low[node] != index[node]) continue;
                var group = new List<int>();
                int member;
                do
                {
                    member = stack.Pop();
                    onStack.Remove(member);
                    group.Add(member);
                } while (member != node);
                if (group.Count(m => m != StandIn) >= 2) result.UnionWith(group);
            }
        }
        return result;
    }
}

/// A thread synchronously waiting on a SemaphoreSlim that has no free slots. The dump can't show who took the slots, so
/// this says who waits and on which semaphore, and how sure it is that the semaphore is a lock: a Warning only when the
/// waiting code uses it as one, or when that code couldn't be followed at all. A BlockingCollection waits on a private SemaphoreSlim of its own; those threads are about
/// the collection, not a semaphore the app took, so they're skipped here: <see cref="FullQueueDetector"/> reports a full
/// Add, and an idle Take is normal. So is a worker waiting in a loop on a semaphore used as a "work available" signal.
public sealed class SemaphoreWaitDetector : IDetector
{
    public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed)
    {
        var threads = new Dictionary<int, ThreadSnapshot>();
        foreach (var t in snapshot.Threads) threads.TryAdd(t.ManagedId, t);
        foreach (var s in snapshot.Semaphores.Where(s => s.CurrentCount == 0))
        {
            var roles = s.WaiterThreadIds
                .Where(id => !claimed.Contains(id) && !SemaphoreFacts.WaitsOnCollection(threads.GetValueOrDefault(id)))
                .Select(id => (Id: id, Role: SemaphoreFacts.Role(threads.GetValueOrDefault(id)?.SemaphoreUse, s)))
                .Where(w => w.Role != SemaphoreRole.Signal)
                .ToList();
            if (roles.Count == 0) continue;
            var waiters = roles.Select(w => w.Id).ToList();
            var asLock = roles.Any(w => w.Role == SemaphoreRole.Lock);
            // Code that couldn't be followed tells nothing either way: it keeps the severity a wait with no free slots has
            // always had, and says why it can't say more.
            var notFollowed = !asLock && waiters.Any(id => threads.GetValueOrDefault(id)?.SemaphoreUse is null);
            // A single wait whose code was read but doesn't show the role keeps that severity too: a lock taken through a
            // lock class or passed in looks like this. Only a loop, where a worker waits for its next signal, is Info.
            var once = roles.Any(w => w.Role == SemaphoreRole.Unknown && SemaphoreFacts.UnknownOnce(threads.GetValueOrDefault(w.Id)?.SemaphoreUse, s));
            var running = SemaphoreFacts.AppCodeRunningOutside(snapshot, waiters);
            var first = threads[waiters[0]];
            var site = FrameClassifier.BestUserFrame(first.Frames);
            var who = waiters.Count == 1 ? $"{first.Label} is" : $"{waiters.Count} threads are";
            yield return new Finding
            {
                Id = "semaphore-wait",
                Severity = (asLock || notFollowed || once) && !running ? Severity.Warning : Severity.Info,
                Title = SemaphoreFacts.NoFreeSlots,
                Summary = $"{who} waiting in {Describe.Where(site)} for {(s.FieldName is null ? "a SemaphoreSlim" : $"{s.DisplayName}, a SemaphoreSlim")} with {SemaphoreFacts.Slots(s)}." +
                          (running ? "" : " No other thread is running app code that could release it."),
                Details = asLock ? [SemaphoreFacts.NoOwner(s)]
                    : notFollowed ? [SemaphoreFacts.NoOwner(s), SemaphoreFacts.NotFollowed(s.DisplayName)]
                    : [SemaphoreFacts.NoOwner(s), SemaphoreFacts.MaybeSignal(s)],
                Advice = asLock
                    ? $"Find the code that takes {s.DisplayName} and make sure it calls Release() on every path (try/finally), and never waits for this thread while holding it."
                    : SemaphoreFacts.LockOrSignalAdvice(s),
                ThreadIds = waiters,
                Location = site?.Source,
            };
        }
    }
}

/// What the code that waits uses a semaphore for, as far as its IL and the semaphore's creation show.
internal enum SemaphoreRole
{
    /// Can't tell: a lock someone hasn't released and a signal nobody has sent yet look the same.
    Unknown,
    /// A lock or throttle: the waiting code releases it itself, or it was created with free slots that are all taken now.
    Lock,
    /// A "work available" signal: created with no free slot (room for one, or no maximum), waited on in a loop and never
    /// released by the waiter.
    Signal,
}

internal static class SemaphoreFacts
{
    public const string NoFreeSlots = "Waiting for a SemaphoreSlim with no free slots";

    public static string Slots(SemaphoreInfo s) =>
        s.MaxCount == int.MaxValue ? $"{s.CurrentCount} slots free" : $"{s.CurrentCount} of {s.MaxCount} slots free";

    public static string NoOwner(SemaphoreInfo s) =>
        $"A SemaphoreSlim has no owner: whoever took the slot of {s.DisplayName} has to call Release(). If that code is itself waiting, directly or through this thread, nothing ever will.";

    public static string MaybeSignal(SemaphoreInfo s) =>
        $"A SemaphoreSlim can also be a signal, created with no free slots and released when there is work; if {s.DisplayName} is one, this is only a wait for work. The dump can't tell which.";

    public static string LockOrSignalAdvice(SemaphoreInfo s) =>
        $"If {s.DisplayName} is used as a lock, find the code that takes it and make sure it calls Release() on every path (try/finally); " +
        "if it is a signal, check that the code that should call Release() when there is work still runs.";

    /// Each role needs proof. The waiting code releases it, or it was made with free slots that are all taken now (also
    /// `new SemaphoreSlim(maxJobs)`, sized when the app starts): a lock (a throttle waited on in a loop too). Made with no
    /// free slot, with room for one signal or without a maximum, and waited on in a loop by code that never releases it: a
    /// signal. No maximum proves nothing, `new SemaphoreSlim(4)` has none either, and `new SemaphoreSlim(0, 4)` can be a
    /// throttle opened later with Release(4); anything else is unknown.
    public static SemaphoreRole Role(SemaphoreUse? use, SemaphoreInfo s)
    {
        if (use is { ReleasesIt: true } || s.InitialCount > 0 || s.SizedAtRunTime == true) return SemaphoreRole.Lock;
        return s.InitialCount == 0 && s.MaxCount is (1 or int.MaxValue) && use is { WaitsInLoop: true } ? SemaphoreRole.Signal : SemaphoreRole.Unknown;
    }

    /// The waiting code was read, doesn't show whether the semaphore is a lock or a signal, and doesn't wait in a loop
    /// (where a worker waits for its next signal): it may be a lock taken somewhere the code that was read doesn't show.
    public static bool UnknownOnce(SemaphoreUse? use, SemaphoreInfo s) =>
        use is { WaitsInLoop: false } && Role(use, s) == SemaphoreRole.Unknown;

    /// The code that waits couldn't be followed, so nothing is known of how it uses the semaphore.
    public static string NotFollowed(string name) =>
        $"HangSloth couldn't follow the code that waits (for example a helper method the runtime inlined), so it can't tell whether {name} is used as a lock or as a signal.";

    /// Waiting inside BlockingCollection.Add or Take, whose SemaphoreSlim is the collection's own.
    public static bool WaitsOnCollection(ThreadSnapshot? thread) => thread?.WaitingOnCollection is not null;

    /// Is any thread other than these running (not blocked, not idle) the app's own code?
    /// A thread in synchronous I/O counts: when the I/O returns it carries on and can release things.
    public static bool AppCodeRunningOutside(ProcessSnapshot snapshot, IReadOnlyCollection<int> except)
    {
        var skip = except.ToHashSet();
        return snapshot.Threads.Any(t => !skip.Contains(t.ManagedId)
                                         && FrameClassifier.FirstUserFrame(t.Frames) is not null
                                         && FrameClassifier.Classify(t.Frames) is BlockingKind.None or BlockingKind.SyncIO
                                         && t.BlockedOnTask is null);
    }
}
