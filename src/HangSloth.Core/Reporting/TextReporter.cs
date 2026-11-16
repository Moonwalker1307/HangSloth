using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using System.Text;
using System.Text.RegularExpressions;

namespace HangSloth.Reporting;

public sealed record TextReportOptions
{
    public bool UseColor { get; init; }
    public bool AllThreads { get; init; }
    /// How many framework frames one stack may show once runs of them are collapsed; the app's own frames
    /// are always shown (up to 40 per stack). int.MaxValue shows every frame.
    public int MaxFrames { get; init; } = 12;
    /// Plain ASCII markers instead of the severity glyphs, and \uXXXX for any other non-ASCII character:
    /// for a console that cannot show UTF-8.
    public bool Ascii { get; init; }
}

/// Plain-language report for a terminal.
public static class TextReporter
{
    public static void Write(AnalysisReport report, TextWriter output, TextReportOptions? options = null)
    {
        options ??= new TextReportOptions();
        // Everything that comes from the dump is made safe first (TerminalText.Clean), then the report is
        // rendered, then the finished text is checked once more: only our own colour codes may be left in it.
        var buffer = new StringWriter { NewLine = "\n" };
        Render(TerminalText.Clean(report, options.Ascii), buffer, options);
        var text = TerminalText.Guard(buffer.ToString(), options.UseColor, options.Ascii);
        if (options.UseColor) text = text.Replace(TerminalText.Mark, '\u001b');
        output.Write(output.NewLine == "\n" ? text : text.Replace("\n", output.NewLine));
    }

    private static void Render(AnalysisReport report, TextWriter output, TextReportOptions options)
    {
        var c = new Palette(options.UseColor, options.Ascii);
        var s = report.Snapshot;

        output.WriteLine(c.Bold("HangSloth") + c.Dim($" {c.Dot} {s.Source} {c.Dot} .NET {s.RuntimeVersion ?? "?"} {c.Dot} {Describe.Plural(s.Threads.Count, "managed thread")}"));
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
                Severity.Critical => c.Red(c.Cross + " " + f.Title),
                Severity.Warning => c.Yellow(c.Triangle + " " + f.Title),
                _ => c.Cyan(c.Circle + " " + f.Title),
            };
            output.WriteLine($"{c.Bold($"{n}.")} {c.Bold(badge)}");
            WriteWrapped(output, f.Summary, "   ");
            foreach (var d in f.Details)
                WriteWrapped(output, d, $"   {c.Bullet} ", "     ");
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
        output.WriteLine(c.Bold(c.Yellow(alarming ? c.Triangle + " " + char.ToUpperInvariant(title[0]) + title[1..] : c.Triangle + " Can't tell: " + title)));
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
            output.WriteLine($"   {c.Bold($"Threads {ids}")}{c.Dim($" {c.Dot} {same.Count} threads stopped at the same place" + (t.IsThreadPoolWorker ? $" {c.Dot} thread pool" : ""))}");
        }
        var tags = new List<string>();
        if (t.IsThreadPoolWorker) tags.Add("thread pool");
        if (t.IsFinalizer) tags.Add("finalizer");
        if (t.SynchronizationContext is { } ctx) tags.Add(ctx.ShortTypeName);
        if (same.Count == 1)
            output.WriteLine($"   {c.Bold(t.Label)}{c.Dim($" {c.Dot} OS thread 0x{t.OSThreadId:x}" + (tags.Count > 0 ? $" {c.Dot} " + string.Join(", ", tags) : ""))}");

        var frames = VisibleFrames(t.Frames);
        // The app's own frames are always shown: the outer callers (the controller action, the job's entry
        // point) are what a reader needs to find the code. Runs of framework frames are what gets cut:
        // database drivers and async plumbing can stack dozens of them on top of the app's code.
        var everything = options.MaxFrames == int.MaxValue;
        var budget = options.MaxFrames;
        var appShown = 0;
        void Frame(StackFrameInfo f)
        {
            var name = Names.ShortMethod(f.TypeName, f.MethodName);
            var line = f.Source is null ? name : $"{name}  {c.Cyan($"{TerminalText.Clean(f.Source.FileName, options.Ascii)}:{f.Source.Line}")}";
            output.WriteLine(FrameClassifier.IsUserFrame(f) ? $"      {c.Bold(c.Frame)} {line}" : c.Dim($"        {name}"));
        }
        for (var i = 0; i < frames.Count;)
        {
            if (FrameClassifier.IsUserFrame(frames[i]))
            {
                var left = frames.Count - i;
                if (!everything && appShown >= MaxAppFrames && left > 1)
                {
                    output.WriteLine(c.Dim($"        {c.Ellipsis} {Describe.Plural(left, "more frame")}"));
                    break;
                }
                Frame(frames[i++]);
                appShown++;
                continue;
            }
            var end = i;
            while (end < frames.Count && !FrameClassifier.IsUserFrame(frames[end])) end++;
            var run = end - i;
            // Above the first app frame keep the top few (what it's blocked in) and the last one (how the app
            // got there); a run further down is collapsed whole. A single frame is never replaced by a line.
            int top = run, bottom = 0;
            if (!everything && i == 0 && (run > LongPrefix || run > budget))
                (top, bottom) = (Math.Min(4, Math.Max(budget - 1, 1)), 1);
            else if (!everything && i > 0 && (run > MaxPlainRun || run > budget))
                (top, bottom) = (0, 0);
            var hidden = run - top - bottom;
            if (hidden < 2) (top, bottom, hidden) = (run, 0, 0);
            for (var k = 0; k < top; k++) Frame(frames[i + k]);
            if (hidden > 0)
                output.WriteLine(c.Dim($"        {c.Ellipsis} {Describe.Plural(hidden, "framework frame")}"));
            for (var k = 0; k < bottom; k++) Frame(frames[end - bottom + k]);
            budget -= top + bottom;
            i = end;
        }
        if (frames.Count == 0) output.WriteLine(c.Dim("        (no managed frames)"));
        output.WriteLine();
    }

    private const int MaxGroups = 12;
    private const int MaxSummaryGroups = 8;
    private const int LongPrefix = 8;
    private const int MaxPlainRun = 2;
    private const int MaxAppFrames = 40;
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

    private sealed class Palette(bool enabled, bool ascii)
    {
        public string Cross => ascii ? "X" : "✖";
        public string Triangle => ascii ? "!" : "▲";
        public string Circle => ascii ? "i" : "●";
        public string Bullet => ascii ? "-" : "•";
        public string Frame => ascii ? ">" : "›";
        public string Dot => ascii ? "|" : "·";
        public string Ellipsis => ascii ? "..." : "…";

        /// The colour codes are written with TerminalText.Mark instead of ESC, so that a real ESC is never one of ours
        /// until the guard has checked the finished text (Write turns the marks into ESC last).
        private string Wrap(string code, string s) => enabled ? $"{TerminalText.Mark}[{code}m{s}{TerminalText.Mark}[0m" : s;
        public string Bold(string s) => Wrap("1", s);
        public string Dim(string s) => Wrap("2", s);
        public string Red(string s) => Wrap("31", s);
        public string Green(string s) => Wrap("32", s);
        public string Yellow(string s) => Wrap("33", s);
        public string Cyan(string s) => Wrap("36", s);

        public static int VisibleLength(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\uE000\\[[0-9;]*m", "").Length;
    }
}

/// Makes text from a dump safe to print on a terminal. A dump comes from another machine or customer, and
/// thread names, type and method names, SQL, URLs and command lines in it can carry escape sequences that
/// move the cursor, recolour or retitle the terminal, or newlines that forge a line of the report.
internal static class TerminalText
{
    /// Stands in for ESC in the report's own colour codes while the text is being built (a private-use
    /// character, which Clean escapes when it comes from the dump).
    public const char Mark = '\uE000';

    /// Tab, CR and LF become a space; every other control character (C0, DEL, C1, which includes the one-byte
    /// CSI), the line and paragraph separators, lone surrogates and the bidirectional overrides and isolates
    /// become \xNN or \uNNNN. With <paramref name="ascii"/> so does every other non-ASCII character.
    /// Returns the same instance when there is nothing to change.
    public static string Clean(string text, bool ascii = false)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            string? replacement = null;
            if (ch is '\t' or '\r' or '\n') replacement = " ";
            else if (char.IsControl(ch)) replacement = ch < 0x100 ? $"\\x{(int)ch:X2}" : $"\\u{(int)ch:X4}";
            else if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                // A valid pair is one character: kept, or both halves escaped.
                if (ascii) { sb ??= new StringBuilder(text.Length).Append(text, 0, i); sb.Append($"\\u{(int)ch:X4}\\u{(int)text[i + 1]:X4}"); }
                else sb?.Append(ch).Append(text[i + 1]);
                i++;
                continue;
            }
            else if (char.IsSurrogate(ch) || ch == Mark || IsInvisibleFormat(ch) || (ascii && ch > 0x7E)) replacement = $"\\u{(int)ch:X4}";

            if (replacement is null) { sb?.Append(ch); continue; }
            sb ??= new StringBuilder(text.Length).Append(text, 0, i);
            sb.Append(replacement);
        }
        return sb?.ToString() ?? text;
    }

    private static bool IsInvisibleFormat(char ch) =>
        ch is '\u2028' or '\u2029' or '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    /// The report with every string a reader could see made safe. The snapshot and the findings keep their
    /// identity when nothing in them needed changing.
    public static AnalysisReport Clean(AnalysisReport report, bool ascii)
    {
        string? C(string? s) => s is null ? null : Clean(s, ascii);
        var s = report.Snapshot;
        var threads = Map(s.Threads, t => Clean(t, ascii));
        var warnings = Map(s.Warnings, w => Clean(w, ascii));
        var snapshot = ReferenceEquals(threads, s.Threads) && ReferenceEquals(warnings, s.Warnings)
            && C(s.Source) == s.Source && C(s.RuntimeVersion) == s.RuntimeVersion
            ? s : s with { Source = C(s.Source)!, RuntimeVersion = C(s.RuntimeVersion), Threads = threads, Warnings = warnings };
        var findings = Map(report.Findings, f =>
        {
            var details = Map(f.Details, d => Clean(d, ascii));
            return C(f.Title) == f.Title && C(f.Summary) == f.Summary && C(f.Advice) == f.Advice && ReferenceEquals(details, f.Details)
                ? f : f with { Title = C(f.Title)!, Summary = C(f.Summary)!, Advice = C(f.Advice), Details = details };
        });
        return ReferenceEquals(snapshot, s) && ReferenceEquals(findings, report.Findings) ? report : report with { Snapshot = snapshot, Findings = findings };
    }

    private static ThreadSnapshot Clean(ThreadSnapshot t, bool ascii)
    {
        string? C(string? s) => s is null ? null : Clean(s, ascii);
        var frames = Map(t.Frames, f =>
        {
            // The file name is cleaned where it is printed: an escape ends in a backslash, which FileName would split at.
            return C(f.TypeName) == f.TypeName && C(f.MethodName) == f.MethodName
                ? f : f with { TypeName = C(f.TypeName)!, MethodName = C(f.MethodName)! };
        });
        var context = t.SynchronizationContext is { } ctx && C(ctx.TypeName) != ctx.TypeName ? ctx with { TypeName = C(ctx.TypeName)! } : t.SynchronizationContext;
        // Names the sentences about a wait are built from.
        var collection = t.WaitingOnCollection is { } w && (C(w.FieldName) != w.FieldName || C(w.Collection.TypeName) != w.Collection.TypeName)
            ? w with { FieldName = C(w.FieldName), Collection = w.Collection with { TypeName = C(w.Collection.TypeName)! } } : t.WaitingOnCollection;
        var pulse = t.WaitingForPulse is { } p && (C(p.FieldName) != p.FieldName || C(p.Object.TypeName) != p.Object.TypeName)
            ? p with { FieldName = C(p.FieldName), Object = p.Object with { TypeName = C(p.Object.TypeName)! } } : t.WaitingForPulse;
        var database = t.Database is { } d && C(d.CommandText) != d.CommandText ? d with { CommandText = C(d.CommandText) } : t.Database;
        return C(t.Name) == t.Name && ReferenceEquals(frames, t.Frames) && ReferenceEquals(context, t.SynchronizationContext)
            && ReferenceEquals(collection, t.WaitingOnCollection) && ReferenceEquals(pulse, t.WaitingForPulse) && ReferenceEquals(database, t.Database)
            ? t : t with { Name = C(t.Name), Frames = frames, SynchronizationContext = context, WaitingOnCollection = collection, WaitingForPulse = pulse, Database = database };
    }

    /// Maps a list, returning the original when no element changed.
    private static IReadOnlyList<T> Map<T>(IReadOnlyList<T> items, Func<T, T> change) where T : class
    {
        List<T>? result = null;
        for (var i = 0; i < items.Count; i++)
        {
            var changed = change(items[i]);
            if (result is null && ReferenceEquals(changed, items[i])) continue;
            result ??= [.. items.Take(i)];
            result.Add(changed);
        }
        return result is null ? items : result;
    }

    private static IReadOnlyList<string> Map(IReadOnlyList<string> items, Func<string, string> change) => Map<string>(items, change);

    private static readonly Regex Sgr = new("\uE000\\[(?:0|1|2|31|32|33|36)m", RegexOptions.CultureInvariant);

    /// The last line of defence over the finished text: nothing but newlines and (when colour is on) the
    /// colour codes the report writes itself may be left in it, whatever a future change forgets to clean.
    public static string Guard(string text, bool allowColor, bool ascii)
    {
        if (!text.Any(ch => ch != '\n' && (char.IsControl(ch) || ch > 0x7E || char.IsSurrogate(ch)))) return text;
        var sb = new StringBuilder(text.Length);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            var line = lines[i];
            var at = 0;
            if (allowColor)
                foreach (Match m in Sgr.Matches(line))
                {
                    sb.Append(Clean(line[at..m.Index], ascii)).Append(m.Value);
                    at = m.Index + m.Length;
                }
            sb.Append(Clean(line[at..], ascii));
        }
        return sb.ToString();
    }
}
