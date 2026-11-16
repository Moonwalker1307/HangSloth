using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;

namespace HangSloth.Reporting;

public sealed record TextReportOptions
{
    public bool UseColor { get; init; }
    public bool AllThreads { get; init; }
    public int MaxFrames { get; init; } = 12;
}

/// Plain-language report for a terminal.
public static class TextReporter
{
    public static void Write(AnalysisReport report, TextWriter output, TextReportOptions? options = null)
    {
        options ??= new TextReportOptions();
        var c = new Palette(options.UseColor);
        var s = report.Snapshot;

        output.WriteLine(c.Bold("HangSloth") + c.Dim($" · {s.Source} · .NET {s.RuntimeVersion ?? "?"} · {Describe.Plural(s.Threads.Count, "managed thread")}"));
        output.WriteLine();

        var alarming = report.Findings.Any(f => f.Severity >= Severity.Warning);
        if (!s.HeapAvailable)
            WriteNoHeap(output, alarming, s.Warnings.Contains(SnapshotBuilder.HeapNotWalkableWarning), c);
        else if (report.Findings.Count == 0)
        {
            output.WriteLine(c.Green("No hang patterns found."));
            var blocked = s.Threads.Count(t => FrameClassifier.IsBlocked(FrameClassifier.Classify(t.Frames)));
            var running = s.Threads.Count(t => IsInAppCode(t) && !FrameClassifier.IsBlocked(FrameClassifier.Classify(t.Frames)));
            if (blocked > 0)
                output.WriteLine($"{Describe.Plural(blocked, "thread is", "threads are")} waiting on something, but nothing looks like a deadlock, starvation or a stuck task.");
            else if (running > 0)
                output.WriteLine($"No thread is waiting on something, but {Describe.Plural(running, "thread is", "threads are")} in your code: if the process is using CPU, look there first.");
            else
                output.WriteLine("No thread is waiting on something or running your code: the app looks idle.");
            output.WriteLine("If the app still seems hung, take a second dump a few seconds later and compare" + (options.AllThreads ? "." : ", or rerun with --all-threads."));
            output.WriteLine();
        }

        var n = 0;
        foreach (var f in report.Findings)
        {
            n++;
            var badge = f.Severity switch
            {
                Severity.Critical => c.Red("✖ " + f.Title),
                Severity.Warning => c.Yellow("▲ " + f.Title),
                _ => c.Cyan("● " + f.Title),
            };
            output.WriteLine($"{c.Bold($"{n}.")} {c.Bold(badge)}");
            WriteWrapped(output, f.Summary, "   ");
            foreach (var d in f.Details)
                WriteWrapped(output, d, "   • ", "     ");
            if (f.Advice is not null)
                WriteWrapped(output, c.Green("Fix: ") + f.Advice, "   ");
            output.WriteLine();
        }

        // Nothing alarming found: say what the app's own threads are doing, so an unexplained wait or a
        // loop that never ends is visible without a second capture.
        List<int> inCode = alarming ? [] : WriteThreadsInCode(output, report, options, c);

        var threadIds = options.AllThreads
            ? s.Threads.Select(t => t.ManagedId).ToList()
            : report.Findings.SelectMany(f => f.ThreadIds).Concat(inCode).Distinct().ToList();
        if (threadIds.Count > 0)
        {
            output.WriteLine(c.Bold(options.AllThreads ? "Threads" : report.Findings.Count == 0 ? "Stacks of the threads in your code" : "Stacks of the threads involved"));
            // Threads stopped in the same way at the same lines of app code (typical for starvation
            // and contention) are shown once, even if the framework frames below them differ.
            var groups = threadIds
                .Select(s.FindThread)
                .OfType<ThreadSnapshot>()
                .GroupBy(GroupKey)
                .ToList();
            var shownGroups = options.AllThreads ? groups : groups.Take(MaxGroups).ToList();
            foreach (var g in shownGroups)
                WriteThread(output, g.ToList(), options, c);
            var more = groups.Skip(shownGroups.Count).Sum(g => g.Count());
            if (more > 0)
                output.WriteLine(c.Dim($"   (+{Describe.Plural(more, "more thread")}; use --all-threads to see every stack)"));
        }

        foreach (var w in s.Warnings)
            output.WriteLine(c.Dim("note: " + w));
    }

    /// The warning for a dump without the managed heap. It replaces the all-clear, because without the heap
    /// most hangs cannot be seen at all. A full dump taken mid-GC has a heap that cannot be walked: it needs
    /// another dump, not a different kind of dump.
    private static void WriteNoHeap(TextWriter output, bool alarming, bool midGc, Palette c)
    {
        var title = midGc ? "the managed heap in this dump could not be read" : "this dump has no managed heap";
        output.WriteLine(c.Bold(c.Yellow(alarming ? "▲ " + char.ToUpperInvariant(title[0]) + title[1..] : "▲ Can't tell: " + title)));
        WriteWrapped(output, "HangSloth could not read the managed heap from this dump, so it only sees the threads' stacks. "
                             + "Without the heap it cannot see who holds a lock, what a task or async method is waiting for, "
                             + "semaphores, queues, connection pools, the thread pool or thread names, so most hangs go unnoticed."
                             + (alarming ? " The findings below come from the stacks alone, and there may be more." : ""), "   ");
        if (midGc)
        {
            WriteWrapped(output, "The dump was probably taken while a garbage collection was running.", "   ");
            WriteWrapped(output, c.Green("Fix: ") + "Take another dump a moment later.", "   ");
            output.WriteLine();
            return;
        }
        WriteWrapped(output, "Mini and triage dumps leave the heap out: createdump --normal or --triage, "
                             + "dotnet-dump collect --type Mini or Triage, and procdump on Windows without -ma.", "   ");
        WriteWrapped(output, c.Green("Fix: ") + "Take a full dump instead: run hangsloth <pid> on the running app, or use "
                             + "dotnet-dump collect --type Full, createdump --full (or --withheap) or procdump -ma.", "   ");
        output.WriteLine();
    }

    /// One line per group of app threads that no finding shows yet, at most MaxSummaryGroups of them.
    /// Returns the ids of the threads listed, whose stacks are printed below.
    private static List<int> WriteThreadsInCode(TextWriter output, AnalysisReport report, TextReportOptions options, Palette c)
    {
        var shown = report.Findings.SelectMany(f => f.ThreadIds).ToHashSet();
        var groups = report.Snapshot.Threads
            .Where(t => !shown.Contains(t.ManagedId) && IsInAppCode(t))
            .GroupBy(GroupKey)
            .Select(g => g.OrderBy(t => t.ManagedId).ToList())
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g[0].ManagedId)
            .ToList();
        if (groups.Count == 0) return [];

        output.WriteLine(c.Bold("Threads in your code"));
        var listed = groups.Take(MaxSummaryGroups).ToList();
        foreach (var g in listed)
        {
            var who = g.Count == 1 ? $"{g[0].Label} is"
                : g.Count > MaxIdsListed ? $"{g.Count} threads ({string.Join(", ", g.Take(MaxIdsListed).Select(t => t.ManagedId))} and more) are"
                : $"Threads {Describe.JoinAnd(g.Select(t => t.ManagedId.ToString()))} are";
            WriteWrapped(output, $"{who} {InCodeActivity(g[0])}.", "   ", "     ");
        }
        var more = groups.Skip(listed.Count).Sum(g => g.Count);
        if (more > 0)
            output.WriteLine(c.Dim($"   (+{Describe.Plural(more, "more thread")} in your code" + (options.AllThreads ? ")" : "; use --all-threads to see every stack)")));
        output.WriteLine();
        return listed.SelectMany(g => g).Select(t => t.ManagedId).ToList();
    }

    /// A thread that is in the app's own code, and not parked in app.Run() or a test runner's wait. Idle pool
    /// workers, the finalizer and the runtime's own threads have no app frames.
    private static bool IsInAppCode(ThreadSnapshot t) =>
        !t.IsFinalizer && t.Frames.Any(FrameClassifier.IsUserFrame) && FrameClassifier.Classify(t.Frames) != BlockingKind.Idle;

    /// What the thread is doing. A thread that is in a framework call HangSloth does not know (a regex match,
    /// a sort) is not called "running": the call it is in is named instead. Lock waits are worded
    /// here because the detectors' wording ("itself waiting for another lock") follows a lock it already holds.
    private static string InCodeActivity(ThreadSnapshot t)
    {
        var kind = FrameClassifier.Classify(t.Frames);
        var at = Describe.Where(FrameClassifier.BestUserFrame(t.Frames));
        if (kind == BlockingKind.None
            && FrameClassifier.FrameworkPrefix(t.Frames).LastOrDefault(f => !f.IsRuntimeFrame) is { } call)
            return $"in {Names.ShortMethod(call.TypeName, call.MethodName)}, called from {at}";
        if (t.Database is not { Waiting: DatabaseWaitKind.ReadingRows } && t.WaitingOnCollection is null)
        {
            if (kind == BlockingKind.MonitorEnter) return $"waiting to enter a lock in {at}";
            if (kind == BlockingKind.ReaderWriterLock) return $"waiting for a ReaderWriterLockSlim in {at}";
        }
        return Describe.Activity(t, withMethod: true);
    }

    private static void WriteThread(TextWriter output, IReadOnlyList<ThreadSnapshot> same, TextReportOptions options, Palette c)
    {
        var t = same[0];
        if (same.Count > 1)
        {
            var ids = string.Join(", ", same.Take(MaxIdsListed).Select(x => x.ManagedId)) + (same.Count > MaxIdsListed ? $" and {same.Count - MaxIdsListed} more" : "");
            output.WriteLine($"   {c.Bold($"Threads {ids}")}{c.Dim($" · {same.Count} threads stopped at the same place" + (t.IsThreadPoolWorker ? " · thread pool" : ""))}");
        }
        var tags = new List<string>();
        if (t.IsThreadPoolWorker) tags.Add("thread pool");
        if (t.IsFinalizer) tags.Add("finalizer");
        if (t.SynchronizationContext is { } ctx) tags.Add(ctx.ShortTypeName);
        if (same.Count == 1)
            output.WriteLine($"   {c.Bold(t.Label)}{c.Dim($" · OS thread 0x{t.OSThreadId:x}" + (tags.Count > 0 ? " · " + string.Join(", ", tags) : ""))}");

        var frames = VisibleFrames(t.Frames);
        // Database drivers and async plumbing can stack dozens of framework frames on top of the app's
        // code. Keep the top few (what it's blocked in) and the last one (how the app got there).
        var prefix = frames.TakeWhile(f => !FrameClassifier.IsUserFrame(f)).Count();
        var skipFrom = prefix > LongPrefix ? 4 : int.MaxValue;
        var skipTo = prefix > LongPrefix ? prefix - 1 : int.MaxValue;
        var shown = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            if (i == skipFrom)
            {
                output.WriteLine(c.Dim($"        … {skipTo - skipFrom} framework frames"));
                i = skipTo - 1;
                continue;
            }
            var f = frames[i];
            if (shown++ >= options.MaxFrames)
            {
                output.WriteLine(c.Dim($"        … {frames.Count - i} more frames"));
                break;
            }
            var name = Names.ShortMethod(f.TypeName, f.MethodName);
            var line = f.Source is null ? name : $"{name}  {c.Cyan(f.Source.ToString())}";
            output.WriteLine(FrameClassifier.IsUserFrame(f) ? $"      {c.Bold("›")} {line}" : c.Dim($"        {name}"));
        }
        if (frames.Count == 0) output.WriteLine(c.Dim("        (no managed frames)"));
        output.WriteLine();
    }

    private const int MaxGroups = 12;
    private const int MaxSummaryGroups = 8;
    private const int LongPrefix = 8;
    private const int MaxIdsListed = 10;

    /// What a thread is blocked on plus the app frames it's in. Threads without app frames
    /// are grouped by their whole visible stack.
    private static string GroupKey(ThreadSnapshot t)
    {
        var visible = VisibleFrames(t.Frames);
        var user = visible.Where(FrameClassifier.IsUserFrame).ToList();
        var frames = user.Count > 0 ? user : visible;
        return FrameClassifier.Classify(t.Frames) + "|" + string.Join("|", frames.Select(f => $"{Names.ShortMethod(f.TypeName, f.MethodName)}@{f.Source}"));
    }

    /// Frames as a reader sees them: a P/Invoke stub and the method that calls it read as the
    /// same name twice, so consecutive frames that print identically are shown once.
    private static List<StackFrameInfo> VisibleFrames(IReadOnlyList<StackFrameInfo> frames)
    {
        var result = new List<StackFrameInfo>(frames.Count);
        string? previous = null;
        foreach (var f in frames)
        {
            var key = $"{Names.ShortMethod(f.TypeName, f.MethodName)}@{f.Source}";
            if (key != previous) result.Add(f);
            previous = key;
        }
        return result;
    }

    private static void WriteWrapped(TextWriter output, string text, string firstIndent, string? restIndent = null)
    {
        const int width = 100;
        restIndent ??= new string(' ', firstIndent.Length);
        var line = firstIndent;
        var visible = firstIndent.Length;
        foreach (var word in text.Split(' '))
        {
            var wordLen = Palette.VisibleLength(word);
            if (visible + wordLen + 1 > width && visible > restIndent.Length)
            {
                output.WriteLine(line.TrimEnd());
                line = restIndent;
                visible = restIndent.Length;
            }
            line += word + " ";
            visible += wordLen + 1;
        }
        output.WriteLine(line.TrimEnd());
    }

    private sealed class Palette(bool enabled)
    {
        private string Wrap(string code, string s) => enabled ? $"\u001b[{code}m{s}\u001b[0m" : s;
        public string Bold(string s) => Wrap("1", s);
        public string Dim(string s) => Wrap("2", s);
        public string Red(string s) => Wrap("31", s);
        public string Green(string s) => Wrap("32", s);
        public string Yellow(string s) => Wrap("33", s);
        public string Cyan(string s) => Wrap("36", s);

        public static int VisibleLength(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\u001b\\[[0-9;]*m", "").Length;
    }
}
