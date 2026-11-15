using HangSloth.Analysis;
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

        if (report.Findings.Count == 0)
        {
            output.WriteLine(c.Green("No hang patterns found."));
            var blocked = s.Threads.Count(t => FrameClassifier.IsBlocked(FrameClassifier.Classify(t.Frames)));
            output.WriteLine($"{Describe.Plural(blocked, "thread is", "threads are")} waiting on something, but nothing looks like a deadlock, starvation or a stuck task.");
            output.WriteLine("If the app still seems hung, take a second dump a few seconds later and compare, or rerun with --all-threads.");
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

        var threadIds = options.AllThreads
            ? s.Threads.Select(t => t.ManagedId).ToList()
            : report.Findings.SelectMany(f => f.ThreadIds).Distinct().ToList();
        if (threadIds.Count > 0)
        {
            output.WriteLine(c.Bold(options.AllThreads ? "Threads" : "Stacks of the threads involved"));
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
