using HangSloth.Analysis;
using HangSloth.Model;
using HangSloth.Reporting;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Pins what the text report looks like, on hand-built findings so the detectors' wording is not part of it.
public class ReporterLayoutTests
{
    private static string Text(AnalysisReport report, TextReportOptions? options = null)
    {
        var w = new StringWriter { NewLine = "\n" };
        TextReporter.Write(report, w, options);
        return w.ToString();
    }

    private static ThreadSnapshot Worker(int id, IEnumerable<StackFrameInfo> frames, string? name = null) => Thread(id, frames, name) with { OSThreadId = 0x2a };

    // ------------------------------------------------------------ whole reports

    [Fact]
    public void A_critical_finding_is_laid_out_with_its_summary_details_advice_and_stacks()
    {
        var snapshot = Process(
            Worker(1, [.. ResultWait, App("MyApp.OrderController", "Get", "OrderController.cs", 10), App("MyApp.Program", "Main", "Program.cs", 9)], "main"),
            Worker(2, [Fx("System.Threading.LowLevelLifoSemaphore", "WaitForSignal")]))
            with { RuntimeVersion = "10.0.1", Warnings = ["PDB for MyApp.dll was not found."] };
        var finding = new Finding
        {
            Id = "demo", Severity = Severity.Critical, Title = "Deadlock: demo", Summary = "Thread 1 waits for itself.",
            Details = ["Detail one is short.", "Detail two is long enough that it has to wrap onto a second line when it is written at one hundred columns wide."],
            Advice = "Stop doing that.", ThreadIds = [1],
        };

        const string expected = """
            HangSloth · test · .NET 10.0.1 · 2 managed threads

            1. ✖ Deadlock: demo
               Thread 1 waits for itself.
               • Detail one is short.
               • Detail two is long enough that it has to wrap onto a second line when it is written at one
                 hundred columns wide.
               Fix: Stop doing that.

            Stacks of the threads involved
               Thread 1 ("main") · OS thread 0x2a
                    Monitor.Wait
                    ManualResetEventSlim.Wait
                    Task.SpinThenBlockingWait
                    Task.InternalWaitCore
                    Task`1.GetResultCore
                  › OrderController.Get  OrderController.cs:10
                  › Program.Main  Program.cs:9

            note: PDB for MyApp.dll was not found.

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), Text(new AnalysisReport(snapshot, [finding])));
    }

    [Fact]
    public void Severities_have_their_own_markers()
    {
        var report = new AnalysisReport(Process(Worker(1, [Fx("A", "B")])), [
            new Finding { Id = "a", Severity = Severity.Critical, Title = "One", Summary = "S" },
            new Finding { Id = "b", Severity = Severity.Warning, Title = "Two", Summary = "S" },
            new Finding { Id = "c", Severity = Severity.Info, Title = "Three", Summary = "S" },
        ]);
        var lines = Text(report).Split('\n');
        Assert.Contains("1. ✖ One", lines);
        Assert.Contains("2. ▲ Two", lines);
        Assert.Contains("3. ● Three", lines);
    }

    [Fact]
    public void Colour_is_ansi_codes_around_the_same_text_and_none_when_off()
    {
        var report = new AnalysisReport(Process(Worker(1, [Fx("A", "B")])), [new Finding { Id = "a", Severity = Severity.Warning, Title = "Two", Summary = "S", Advice = "Do." }]);
        var plain = Text(report);
        var colored = Text(report, new TextReportOptions { UseColor = true });
        Assert.False(plain.Contains('\u001b'));
        Assert.Contains("\u001b[1m\u001b[33m▲ Two\u001b[0m\u001b[0m", colored);
        Assert.Contains("\u001b[32mFix: \u001b[0mDo.", colored);
        Assert.Equal(plain, System.Text.RegularExpressions.Regex.Replace(colored, "\u001b\\[[0-9;]*m", ""));
    }

    [Fact]
    public void Newlines_follow_the_writer()
    {
        var report = new AnalysisReport(Process(Worker(1, [Fx("A", "B")])), []);
        var w = new StringWriter { NewLine = "\r\n" };
        TextReporter.Write(report, w);
        Assert.Contains("\r\n", w.ToString(), StringComparison.Ordinal);
        Assert.False(w.ToString().Replace("\r\n", "").Contains('\n'));
    }

    // ------------------------------------------------------------ stacks

    private static List<StackFrameInfo> Fw(int n, string tag = "F") => Enumerable.Range(1, n).Select(i => Fx("System.Framework", $"{tag}{i}")).ToList();
    private static List<StackFrameInfo> Apps(int n, string tag = "A") => Enumerable.Range(1, n).Select(i => App("MyApp.Layer", $"{tag}{i}", "Layer.cs", i)).ToList();

    private static string[] StackLines(IEnumerable<StackFrameInfo> frames, TextReportOptions? options = null)
    {
        var report = new AnalysisReport(Process(Worker(1, frames)), [new Finding { Id = "a", Severity = Severity.Warning, Title = "T", Summary = "S", ThreadIds = [1] }]);
        return Text(report, options).Split('\n').SkipWhile(l => !l.StartsWith("   Thread 1", StringComparison.Ordinal)).Skip(1).TakeWhile(l => l != "").ToArray();
    }

    [Fact]
    public void The_apps_own_frames_are_never_cut_off()
    {
        // Six framework frames on top (a .Result wait), then nine app frames, as in a layered app.
        var lines = StackLines([.. ResultWait, Fx("System.Threading.Tasks.Task`1", "get_Result"), .. Apps(9)]);
        Assert.Equal(6 + 9, lines.Length);
        for (var i = 1; i <= 9; i++) Assert.Contains($"› Layer.A{i}  Layer.cs:{i}", string.Join("\n", lines));
        Assert.DoesNotContain(lines, l => l.Contains('…'));
    }

    [Fact]
    public void A_stack_of_thirteen_frames_is_shown_whole_not_as_a_line_that_says_one_more_frame()
    {
        var lines = StackLines([.. Fw(6), .. Apps(7)]);
        Assert.Equal(13, lines.Length);
        Assert.DoesNotContain(lines, l => l.Contains("more frame"));
    }

    [Fact]
    public void Runs_of_framework_frames_between_and_below_the_apps_frames_are_collapsed()
    {
        var lines = StackLines([.. Fw(6, "Top"), .. Apps(9, "First"), .. Fw(3, "Mid"), .. Apps(1, "Last"), .. Fw(5, "Bottom")]);
        var text = string.Join("\n", lines);
        Assert.Equal(6 + 9 + 1 + 1 + 1, lines.Length);
        for (var i = 1; i <= 9; i++) Assert.Contains($"First{i}", text);
        Assert.Contains("Last1", text);
        Assert.Equal(2, lines.Count(l => l.Contains("… 3 framework frames") || l.Contains("… 5 framework frames")));
        Assert.Contains("        … 3 framework frames", lines);
        Assert.Contains("        … 5 framework frames", lines);
        Assert.DoesNotContain("Mid1", text);
        Assert.DoesNotContain("Bottom1", text);
    }

    [Fact]
    public void Up_to_two_framework_frames_between_app_frames_stay_visible()
    {
        var lines = StackLines([.. Apps(1, "A"), .. Fw(2, "Mid"), .. Apps(1, "B")]);
        Assert.Equal(4, lines.Length);
        Assert.Contains("        Framework.Mid1", lines);
    }

    [Fact]
    public void A_long_run_above_the_first_app_frame_keeps_its_top_and_its_last_frame()
    {
        var lines = StackLines([.. Fw(20), .. Apps(2)]);
        Assert.Equal(4 + 1 + 1 + 2, lines.Length);
        Assert.Equal("        … 15 framework frames", lines[4]);
        Assert.Equal("        Framework.F20", lines[5]);
    }

    [Fact]
    public void A_stack_without_app_frames_collapses_the_same_way()
    {
        var lines = StackLines(Fw(30));
        Assert.Equal(6, lines.Length);
        Assert.Equal("        … 25 framework frames", lines[4]);
    }

    [Fact]
    public void A_single_frame_is_never_replaced_by_a_line_that_names_it()
    {
        // One frame over the framework budget: shown, not "… 1 framework frame".
        var lines = StackLines([.. Fw(2, "Top"), .. Apps(1), .. Fw(2, "Mid"), .. Apps(1, "B"), .. Fw(1, "Tail")], new TextReportOptions { MaxFrames = 4 });
        Assert.DoesNotContain(lines, l => l.Contains("1 framework frame") || l.Contains("1 more"));
        Assert.Contains("        Framework.Tail1", lines);
    }

    [Fact]
    public void The_framework_budget_collapses_short_runs_once_it_is_used_up()
    {
        var lines = StackLines([.. Fw(2, "A"), .. Apps(1, "P"), .. Fw(2, "B"), .. Apps(1, "Q"), .. Fw(2, "C"), .. Apps(1, "R")], new TextReportOptions { MaxFrames = 4 });
        Assert.Equal(["        Framework.A1", "        Framework.A2", "      › Layer.P1  Layer.cs:1", "        Framework.B1", "        Framework.B2", "      › Layer.Q1  Layer.cs:1", "        … 2 framework frames", "      › Layer.R1  Layer.cs:1"], lines);
    }

    [Fact]
    public void App_frames_have_a_generous_cap_and_the_line_says_how_many_are_left()
    {
        var lines = StackLines(Apps(45));
        Assert.Equal(41, lines.Length);
        Assert.Equal("        … 5 more frames", lines[^1]);
        // Exactly one frame over the cap is printed instead of being counted.
        Assert.Equal(41, StackLines(Apps(41)).Length);
        Assert.Contains("A41", StackLines(Apps(41))[^1]);
    }

    [Fact]
    public void Max_frames_of_int_max_shows_every_frame()
    {
        var lines = StackLines([.. Fw(30), .. Apps(50), .. Fw(10)], new TextReportOptions { MaxFrames = int.MaxValue });
        Assert.Equal(90, lines.Length);
        Assert.DoesNotContain(lines, l => l.Contains('…'));
    }

    [Fact]
    public void Threads_stopped_at_the_same_place_are_shown_once_with_their_ids()
    {
        StackFrameInfo[] frames = [.. ResultWait, App("MyApp.Cache", "Refresh", "Cache.cs", 5)];
        var report = new AnalysisReport(Process(Worker(1, frames), Worker(2, frames), Worker(3, frames)),
            [new Finding { Id = "a", Severity = Severity.Warning, Title = "T", Summary = "S", ThreadIds = [1, 2, 3] }]);
        var text = Text(report);
        Assert.Contains("   Threads 1, 2, 3 · 3 threads stopped at the same place", text);
        Assert.Single(text.Split('\n'), l => l.Contains("Cache.Refresh"));
    }

    // ------------------------------------------------------------ nothing found

    [Fact]
    public void Nothing_found_says_so_and_lists_the_threads_in_your_code()
    {
        var snapshot = Process(
            Worker(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Parser", "Loop", "Parser.cs", 88)]),
            Worker(2, [Fx("System.Threading.LowLevelLifoSemaphore", "WaitForSignal")]));
        var lines = Text(new AnalysisReport(snapshot, [])).Split('\n');
        Assert.Equal("No hang patterns found.", lines[2]);
        Assert.Contains("Threads in your code", lines);
        Assert.Contains("Stacks of the threads in your code", lines);
    }
}
