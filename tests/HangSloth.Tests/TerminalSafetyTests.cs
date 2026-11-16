using HangSloth.Analysis;
using HangSloth.Model;
using HangSloth.Reporting;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// A dump comes from another machine, so nothing in it may reach the terminal as a control character:
/// not a thread name, a type or method name, SQL, a URL, a command line or an exception message.
public class TerminalSafetyTests
{
    private const string Esc = "\u001b";

    /// Escape sequences that clear the screen, recolour, retitle the window and write to the clipboard,
    /// plus a bell, a newline that forges a line, a C1 control and a right-to-left override.
    private const string Hostile = Esc + "[2J" + Esc + "[1;31mFAKE" + Esc + "[0m" + Esc + "]0;pwned-title\u0007\nNo hang patterns found.\n\u009b31m\u202Eevil\u0085";

    /// Ordinal: xunit's own Contains compares by culture, which ignores control and format characters.
    private static void NotContains(string part, string text) => Assert.False(text.Contains(part, StringComparison.Ordinal), $"found {part.Length} chars of '{part}' in the output");

    private static string Text(AnalysisReport report, bool color = false, bool ascii = false)
    {
        var w = new StringWriter { NewLine = "\n" };
        TextReporter.Write(report, w, new TextReportOptions { UseColor = color, Ascii = ascii });
        return w.ToString();
    }

    /// Hostile text in every string the text report prints.
    private static AnalysisReport HostileReport()
    {
        var thread = Thread(1, [
            Fx("System.Threading.Monitor", "ReliableEnter"),
            new StackFrameInfo { TypeName = "My" + Hostile + "App.Ledger", MethodName = "Post" + Hostile, Source = new SourceLocation("/src/" + Hostile + ".cs", 20) },
        ], "worker" + Hostile) with
        {
            SynchronizationContext = new ObjectRef(0x10, "My" + Hostile + "Context"),
        };
        var snapshot = new ProcessSnapshot
        {
            Source = "dump" + Hostile + ".dmp",
            RuntimeVersion = "10" + Hostile,
            Threads = [thread],
            Warnings = ["warning" + Hostile],
        };
        var finding = new Finding
        {
            Id = "x", Severity = Severity.Critical, Title = "Title" + Hostile, Summary = "Summary " + Hostile,
            Details = ["Detail " + Hostile], Advice = "Advice " + Hostile, ThreadIds = [1],
        };
        return new AnalysisReport(snapshot, [finding]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Nothing_from_the_dump_reaches_the_terminal_as_a_control_character(bool color, bool ascii)
    {
        var text = Text(HostileReport(), color, ascii);

        // With colour on, the only ESC left starts one of the report's own colour codes.
        var withoutOwnColors = System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[(0|1|2|31|32|33|36)m", "");
        Assert.DoesNotContain(withoutOwnColors, ch => ch != '\n' && char.IsControl(ch));
        NotContains("\u202E", text);
        NotContains("\u0085", text);
        // The dump cannot forge the report's verdict line.
        Assert.DoesNotContain("No hang patterns found.", text.Split('\n'));
        Assert.Contains("\\x1B[2J", text, StringComparison.Ordinal);
        Assert.Contains("\\x07", text, StringComparison.Ordinal);
        Assert.Contains("\\u202E", text, StringComparison.Ordinal);
        if (!color) NotContains(Esc, text);
        // A newline in a name does not start a new line of the report.
        Assert.DoesNotContain(text.Split('\n'), l => l.StartsWith("No hang", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hostile_dump_cannot_recolour_the_report()
    {
        var text = Text(HostileReport(), color: true);
        // Our own 33 (yellow) and the like are fine; a colour code that came from the dump is not.
        NotContains(Esc + "[1;31m", text);
        NotContains(Esc + "[2J", text);
        NotContains(Esc + "]", text);
    }

    [Fact]
    public void A_private_use_character_cannot_stand_in_for_the_reports_colour_codes()
    {
        var thread = Thread(1, [Fx("System.Threading.Monitor", "ReliableEnter")], "x" + TerminalText.Mark + "[1;31m" + TerminalText.Mark + "[2J");
        var snapshot = new ProcessSnapshot { Source = "d.dmp", Threads = [thread] };
        var text = Text(new AnalysisReport(snapshot, [new Finding { Id = "x", Severity = Severity.Critical, Title = "T", Summary = "S", ThreadIds = [1] }]), color: true);
        NotContains(Esc + "[1;31m", text);
        NotContains(Esc + "[2J", text);
        NotContains(TerminalText.Mark.ToString(), text);
        Assert.Contains("\\uE000[1;31m", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_file_name_keeps_its_real_name_next_to_the_escape()
    {
        // The escape ends in a backslash; the file name must be cut from the real path, then cleaned.
        var thread = Thread(1, [
            Fx("System.Threading.Monitor", "ReliableEnter"),
            new StackFrameInfo { TypeName = "App.Ledger", MethodName = "Post", Source = new SourceLocation("/src/a" + Esc + "[2J.cs", 20) },
        ]);
        var snapshot = new ProcessSnapshot { Source = "d.dmp", Threads = [thread] };
        var text = Text(new AnalysisReport(snapshot, [new Finding { Id = "x", Severity = Severity.Critical, Title = "T", Summary = "S", ThreadIds = [1] }]), color: false);
        Assert.Contains("a\\x1B[2J.cs:20", text, StringComparison.Ordinal);
        NotContains(Esc, text);
    }

    [Fact]
    public void The_guard_cleans_what_the_cleaning_of_the_report_missed()
    {
        // A string nobody thought of: the guard is the last step over the finished text.
        var m = TerminalText.Mark;
        var guarded = TerminalText.Guard("ok " + Esc + "[2J\u0007 " + m + "[1m" + "bold" + m + "[0m " + m + "[31" + "\nnext\r\n", allowColor: true, ascii: false);
        Assert.Equal("ok \\x1B[2J\\x07 " + m + "[1mbold" + m + "[0m \\uE000[31\nnext \n", guarded);
        Assert.Equal("\\x1B[1m\n", TerminalText.Guard(Esc + "[1m\n", allowColor: false, ascii: false));
    }

    [Fact]
    public void Clean_keeps_readable_text_and_escapes_the_rest()
    {
        const string readable = "Zahlungsprüfer-✓ заказ-0 日本語 😀 `SELECT 1` <T>[0]";
        Assert.Same(readable, TerminalText.Clean(readable));
        Assert.Equal("a b c d", TerminalText.Clean("a\tb\rc\nd"));
        Assert.Equal("\\x1B\\x00\\x7F\\x9B", TerminalText.Clean("\u001b\u0000\u007f\u009b"));
        Assert.Equal("\\u2028\\u2029\\u202E\\u2066", TerminalText.Clean("\u2028\u2029\u202e\u2066"));
        Assert.Equal("a\\uD800b", TerminalText.Clean("a\ud800b"));
        Assert.Equal("a\\uDC00b", TerminalText.Clean("a\udc00b"));
    }

    [Fact]
    public void Ascii_mode_escapes_every_non_ascii_character()
    {
        Assert.Equal("Zahlungspr\\u00FCfer-\\u2713 \\uD83D\\uDE00", TerminalText.Clean("Zahlungsprüfer-✓ 😀", ascii: true));
        Assert.Equal("Zahlungsprüfer-✓ 😀", TerminalText.Clean("Zahlungsprüfer-✓ 😀"));
    }

    [Fact]
    public void Ascii_output_is_pure_ascii_with_ascii_markers()
    {
        var snapshot = Process(Thread(1, [Fx("System.Threading.Monitor", "ReliableEnter"), App("MyApp.Ledger", "Post", "Ledger.cs", 20)], "заказ-0 ✓"));
        var report = new AnalysisReport(snapshot, [
            new Finding { Id = "a", Severity = Severity.Critical, Title = "Critical one", Summary = "S", Details = ["D"], ThreadIds = [1] },
            new Finding { Id = "b", Severity = Severity.Warning, Title = "Warning one", Summary = "S" },
            new Finding { Id = "c", Severity = Severity.Info, Title = "Info one", Summary = "S" },
        ]);

        var ascii = Text(report, ascii: true);
        Assert.All(ascii, ch => Assert.True(ch < 0x80, $"U+{(int)ch:X4} in ASCII output"));
        Assert.Contains("1. X Critical one", ascii);
        Assert.Contains("2. ! Warning one", ascii);
        Assert.Contains("3. i Info one", ascii);
        Assert.Contains("   - D", ascii);
        Assert.Contains("      > Ledger.Post  Ledger.cs:20", ascii);
        Assert.Contains("\\u0437\\u0430\\u043A\\u0430\\u0437-0 \\u2713", ascii);
        Assert.Contains(" | OS thread 0x0", ascii);

        var utf8 = Text(report);
        Assert.Contains("1. ✖ Critical one", utf8);
        Assert.Contains("2. ▲ Warning one", utf8);
        Assert.Contains("3. ● Info one", utf8);
        Assert.Contains("заказ-0 ✓", utf8);
    }

    [Fact]
    public void The_heapless_warning_and_the_ellipsis_have_ascii_forms()
    {
        var report = new AnalysisReport(Process(Thread(1, [.. ResultWait, App("MyApp.A", "B", "A.cs", 1)])) with { HeapAvailable = false }, []);
        var ascii = Text(report, ascii: true);
        Assert.Contains("! Can't tell: this dump has no managed heap", ascii);
        Assert.All(ascii, ch => Assert.True(ch < 0x80));
    }

    [Fact]
    public void The_json_report_escapes_control_characters_the_json_way()
    {
        var json = JsonReporter.Serialize(HostileReport());
        // The serializer ends its own lines the platform's way ("\r\n" on Windows). Nothing else may be a control character.
        Assert.All(json.Replace("\r\n", "\n"), ch => Assert.True(ch == '\n' || !char.IsControl(ch), $"U+{(int)ch:X4} in JSON"));
        NotContains("\u202E", json);
        NotContains("\u0085", json);
        NotContains("\u2028", json);
        // Still valid JSON, and the name comes back unchanged.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var name = doc.RootElement.GetProperty("snapshot").GetProperty("threads")[0].GetProperty("name").GetString();
        Assert.Equal("worker" + Hostile, name);
    }
}
