using System.Globalization;
using System.Text;
using System.Text.Json;
using HangSloth.Cli;
using HangSloth.Model;
using static HangSloth.Cli.Program;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// How the report leaves the program: UTF-8 whatever the console's code page, ASCII markers when a console
/// cannot be switched, and the same numbers on every machine. The destination is injected, so no test
/// touches the process's console.
public class CliOutputTests
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding("iso-8859-1");

    private sealed class Destination
    {
        public MemoryStream Legacy { get; } = new();
        public MemoryStream Bytes { get; } = new();
        public StreamWriter Writer { get; }
        public bool Restored { get; private set; }
        public bool Opened { get; private set; }
        public Stdout Stdout { get; }

        public Destination(Encoding consoleEncoding, bool redirected, bool canSwitch)
        {
            Writer = new StreamWriter(Legacy, consoleEncoding, 1024, leaveOpen: true) { AutoFlush = true };
            var switched = new StreamWriter(Bytes, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            Stdout = new Stdout
            {
                Writer = Writer,
                Redirected = redirected,
                OpenBytes = () => { Opened = true; return Bytes; },
                SwitchToUtf8 = () => canSwitch ? (switched, () => Restored = true) : null,
            };
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static ProcessSnapshot Snapshot() => Process(
        Thread(1, [Fx("System.Threading.Thread", "Sleep"), App("MyApp.Parser", "Loop", "Parser.cs", 88)], "Zahlungsprüfer-✓ заказ-0"));

    private static (int Exit, Destination Out) Run(Destination d, params string[] args)
    {
        var exit = Program.Run(args, loadDump: (_, _) => Snapshot(), stdout: d.Stdout);
        return (exit, d);
    }

    // ------------------------------------------------------------ the plan

    [Theory]
    [InlineData("utf-8", false, false, "Console")]
    [InlineData("utf-8", true, true, "Console")]
    [InlineData("utf-16", true, false, "Console")]               // a StringWriter
    [InlineData("legacy", true, false, "Utf8Stream")]
    [InlineData("legacy", true, true, "Utf8Stream")]
    [InlineData("legacy", false, true, "Utf8Stream")]           // --json always writes bytes
    [InlineData("legacy", false, false, "SwitchConsole")]
    public void The_plan_follows_the_consoles_encoding(string encoding, bool redirected, bool json, string expected)
    {
        // Latin-1 stands in for any legacy code page (437 needs a code page provider).
        Encoding console = encoding switch { "utf-8" => new UTF8Encoding(false), "utf-16" => Encoding.Unicode, _ => Latin1 };
        Assert.Equal(expected, PlanOutput(console, redirected, json).ToString());
    }

    // ------------------------------------------------------------ JSON

    [Fact]
    public void Json_is_valid_utf8_without_a_byte_order_mark_whatever_the_console_encoding()
    {
        var (exit, d) = Run(new Destination(Latin1, redirected: true, canSwitch: false), "x.dmp", "--json");
        Assert.Equal(0, exit);
        Assert.Equal(0, d.Legacy.Length);
        var bytes = d.Bytes.ToArray();
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'{', bytes[0]);
        var json = StrictUtf8.GetString(bytes);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Thread 1 (\"Zahlungsprüfer-✓ заказ-0\")", doc.RootElement.GetProperty("snapshot").GetProperty("threads")[0].GetProperty("label").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Matches(@"^\d+\.\d+\.\d+", doc.RootElement.GetProperty("version").GetString()!);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_to_an_interactive_legacy_console_is_still_utf8_bytes()
    {
        var (_, d) = Run(new Destination(Latin1, redirected: false, canSwitch: false), "x.dmp", "--json");
        Assert.Equal(0, d.Legacy.Length);
        Assert.Contains("заказ-0", StrictUtf8.GetString(d.Bytes.ToArray()), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ text

    [Fact]
    public void Text_to_a_file_on_a_legacy_console_is_utf8_with_the_real_glyphs()
    {
        var (_, d) = Run(new Destination(Latin1, redirected: true, canSwitch: false), "x.dmp");
        Assert.Equal(0, d.Legacy.Length);
        var bytes = d.Bytes.ToArray();
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "byte order mark");
        var text = StrictUtf8.GetString(bytes);
        Assert.StartsWith("HangSloth · test", text, StringComparison.Ordinal);
        Assert.Contains("Zahlungsprüfer-✓ заказ-0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_to_a_legacy_console_that_can_be_switched_is_written_in_utf8_and_the_console_put_back()
    {
        var d = new Destination(Latin1, redirected: false, canSwitch: true);
        Run(d, "x.dmp", "--no-color");
        Assert.Equal(0, d.Legacy.Length);
        Assert.False(d.Opened);
        Assert.Contains("Zahlungsprüfer-✓ заказ-0", StrictUtf8.GetString(d.Bytes.ToArray()), StringComparison.Ordinal);
        Assert.True(d.Restored);
    }

    [Fact]
    public void Text_to_a_legacy_console_that_cannot_be_switched_uses_ascii_markers_only()
    {
        var d = new Destination(Latin1, redirected: false, canSwitch: false);
        Run(d, "x.dmp", "--no-color");
        Assert.Equal(0, d.Bytes.Length);
        var bytes = d.Legacy.ToArray();
        Assert.All(bytes, b => Assert.True(b < 0x80, $"byte 0x{b:X2}"));
        var text = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("HangSloth | test", text, StringComparison.Ordinal);
        Assert.Contains("Zahlungspr\\u00FCfer-\\u2713 \\u0437\\u0430\\u043A\\u0430\\u0437-0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_windows_has_a_console_code_page_to_switch()
    {
        // On Linux and macOS the terminal's charset comes from the locale: changing the encoder would send
        // UTF-8 bytes to a Latin-1 terminal, so there is nothing to switch and the report falls back to ASCII.
        Assert.Null(Stdout.SwitchConsoleToUtf8(isWindows: false));
    }

    [Fact]
    public void A_utf8_console_is_written_to_as_it_is()
    {
        var d = new Destination(new UTF8Encoding(false), redirected: false, canSwitch: false);
        Run(d, "x.dmp", "--no-color");
        Assert.False(d.Opened);
        Assert.Equal(0, d.Bytes.Length);
        Assert.Contains("Zahlungsprüfer-✓ заказ-0", StrictUtf8.GetString(d.Legacy.ToArray()), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ numbers

    private static ProcessSnapshot ReadingRows() => Process(Thread(10,
        [new StackFrameInfo { TypeName = "Npgsql.NpgsqlDataReader", MethodName = "Read", ModuleName = "/app/Npgsql.dll" }, App("MyApp.ReportCache", "Refresh", "Cache.cs", 30)], "refresh") with
    {
        Database = new DatabaseActivity
        {
            Provider = "PostgreSQL", Waiting = DatabaseWaitKind.ReadingRows, CommandText = "SELECT id, kind FROM page_events", RowsRead = 12345,
            Connections = [new DatabaseConnection(100, false)],
        },
    });

    /// A culture that groups digits with '.' and writes decimals with ',', whatever the machine's ICU data says.
    private static CultureInfo German()
    {
        var culture = (CultureInfo)new CultureInfo("en-US").Clone();
        culture.NumberFormat.NumberGroupSeparator = ".";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return culture;
    }

    [Fact]
    public void Numbers_in_the_report_do_not_depend_on_the_machines_culture()
    {
        var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        var german = German();
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = german;
        try
        {
            var stdout = new StringWriter();
            var json = new Stdout { Writer = stdout, Redirected = true, OpenBytes = () => throw new InvalidOperationException(), SwitchToUtf8 = () => null };
            var exit = Program.Run(["x.dmp", "--json"], loadDump: (_, _) => ReadingRows(), stdout: json);
            Assert.Equal(0, exit);
            Assert.Contains("(12,345 rows so far)", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("12.345", stdout.ToString(), StringComparison.Ordinal);

            // The caller's culture is not changed for good.
            Assert.Same(german, CultureInfo.CurrentCulture);
            Assert.Same(german, CultureInfo.CurrentUICulture);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = before;
        }
    }
}
