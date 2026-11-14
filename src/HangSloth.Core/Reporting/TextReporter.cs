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
            : report.Findings.SelectMany(f => f.ThreadIds).Distinct().Take(64).ToList();
        if (threadIds.Count > 0)
        {
            output.WriteLine(c.Bold(options.AllThreads ? "Threads" : "Stacks of the threads involved"));
            // Threads with identical stacks (typical for starvation) are shown once.
            var groups = threadIds
                .Select(s.FindThread)
                .OfType<ThreadSnapshot>()
                .GroupBy(t => string.Join("|", t.Frames.Select(f => f.ToString())))
                .ToList();
            foreach (var g in groups)
                WriteThread(output, g.ToList(), options, c);
            var more = report.Findings.SelectMany(f => f.ThreadIds).Distinct().Count() - threadIds.Count;
            if (!options.AllThreads && more > 0)
                output.WriteLine(c.Dim($"   (+{more} more; use --all-threads to see every stack)"));
        }

        foreach (var w in s.Warnings)
            output.WriteLine(c.Dim("note: " + w));
    }

    private static void WriteThread(TextWriter output, IReadOnlyList<ThreadSnapshot> same, TextReportOptions options, Palette c)
    {
        var t = same[0];
        if (same.Count > 1)
        {
            var ids = string.Join(", ", same.Select(x => x.ManagedId));
            output.WriteLine($"   {c.Bold($"Threads {ids}")}{c.Dim($" · {same.Count} threads with the same stack" + (t.IsThreadPoolWorker ? " · thread pool" : ""))}");
        }
        var tags = new List<string>();
        if (t.IsThreadPoolWorker) tags.Add("thread pool");
        if (t.IsFinalizer) tags.Add("finalizer");
        if (t.SynchronizationContext is { } ctx) tags.Add(ctx.ShortTypeName);
        if (same.Count == 1)
            output.WriteLine($"   {c.Bold(t.Label)}{c.Dim($" · OS thread 0x{t.OSThreadId:x}" + (tags.Count > 0 ? " · " + string.Join(", ", tags) : ""))}");

        var frames = t.Frames;
        var shown = 0;
        foreach (var f in frames)
        {
            if (shown++ >= options.MaxFrames)
            {
                output.WriteLine(c.Dim($"        … {frames.Count - options.MaxFrames} more frames"));
                break;
            }
            var name = Names.ShortMethod(f.TypeName, f.MethodName);
            var line = f.Source is null ? name : $"{name}  {c.Cyan(f.Source.ToString())}";
            output.WriteLine(FrameClassifier.IsUserFrame(f) ? $"      {c.Bold("›")} {line}" : c.Dim($"        {name}"));
        }
        if (frames.Count == 0) output.WriteLine(c.Dim("        (no managed frames)"));
        output.WriteLine();
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
