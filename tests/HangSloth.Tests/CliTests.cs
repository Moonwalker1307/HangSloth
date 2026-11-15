using System.Text;
using HangSloth.Analysis;
using HangSloth.Cli;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// The CLI's contract: exit code 0, 1 or 2 and a one-line message, whatever the input. Runs Program.Main
/// in process with the console captured. The console is process-wide, hence the collection.
[Collection("Cli")]
public class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hangsloth-cli-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly object ConsoleLock = new();

    internal static (int Exit, string Out, string Err) Run(string[] args, Func<string, LoadOptions, ProcessSnapshot>? loadDump = null, Func<HangAnalyzer>? analyzer = null)
    {
        // Also used by the end-to-end class, which is another collection, so the lock is what serialises the capture.
        lock (ConsoleLock)
        {
            var (oldOut, oldErr) = (Console.Out, Console.Error);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Console.SetOut(stdout);
            Console.SetError(stderr);
            try
            {
                var exit = loadDump is null && analyzer is null ? Program.Main(args) : Program.Run(args, loadDump, analyzer);
                return (exit, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }
        }
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static void AssertOneLineError(string err, string expectedStart)
    {
        Assert.StartsWith(expectedStart, err);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void A_file_that_is_not_a_dump_is_an_error()
    {
        var path = WriteFile("notes.txt", Encoding.UTF8.GetBytes("this is not a memory dump\n"));
        var (exit, _, err) = Run([path, "--no-color"]);
        Assert.Equal(1, exit);
        AssertOneLineError(err, $"hangsloth: Could not open '{path}' as a memory dump");
    }

    [Fact]
    public void A_truncated_elf_core_dump_is_an_error_not_a_crash()
    {
        // An ELF core file whose header promises program headers that are not in the file.
        var elf = new byte[64];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F';
        elf[4] = 2; elf[5] = 1; elf[6] = 1;                     // 64-bit, little endian, version 1
        BitConverter.GetBytes((ushort)4).CopyTo(elf, 16);       // ET_CORE
        BitConverter.GetBytes((ushort)62).CopyTo(elf, 18);      // x86-64
        BitConverter.GetBytes(1u).CopyTo(elf, 20);
        BitConverter.GetBytes(64UL).CopyTo(elf, 32);            // e_phoff
        BitConverter.GetBytes((ushort)64).CopyTo(elf, 52);      // e_ehsize
        BitConverter.GetBytes((ushort)56).CopyTo(elf, 54);      // e_phentsize
        BitConverter.GetBytes((ushort)40).CopyTo(elf, 56);      // e_phnum
        var path = WriteFile("cut.core", elf);

        var (exit, _, err) = Run([path, "--no-color"]);
        Assert.Equal(1, exit);
        Assert.StartsWith("hangsloth: ", err);
        Assert.DoesNotContain("unexpected error", err);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void A_missing_symbols_folder_is_an_error()
    {
        var path = WriteFile("any.dmp", [1, 2, 3]);
        var missing = Path.Combine(_dir, "no-such-folder");
        var (exit, _, err) = Run([path, "--symbols", missing, "--no-color"]);
        Assert.Equal(1, exit);
        AssertOneLineError(err, $"hangsloth: Symbols folder not found: {missing}");
    }

    [Fact]
    public void Keep_dump_with_a_dump_file_is_a_usage_error()
    {
        var path = WriteFile("any.dmp", [1, 2, 3]);
        var (exit, _, err) = Run([path, "--keep-dump", Path.Combine(_dir, "kept.dmp")]);
        Assert.Equal(1, exit);
        Assert.StartsWith("--keep-dump only works with a process id.", err);
        Assert.Contains("Usage:", err);
        Assert.False(File.Exists(Path.Combine(_dir, "kept.dmp")));
    }

    [Fact]
    public void Version_prints_the_short_version_and_exits_with_0()
    {
        var (exit, stdout, err) = Run(["--version"]);
        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Matches(@"^hangsloth \d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9a-f]{1,7})?\r?\n$", stdout);
    }

    [Fact]
    public void Usage_text_lists_version()
    {
        var (exit, stdout, _) = Run(["--help"]);
        Assert.Equal(0, exit);
        Assert.Contains("--version", stdout);
        var readme = File.ReadAllText(Path.Combine(SampleApp.RepoRoot(), "README.md"));
        Assert.Contains("  --version            Show the version", readme);
    }

    [Fact]
    public void A_dump_of_the_desktop_clr_is_rejected_as_not_supported()
    {
        var path = WriteFile("fx.dmp", MiniDump.Windows(MiniDump.DesktopClr));
        var (exit, _, err) = Run([path, "--no-color"]);
        Assert.Equal(1, exit);
        AssertOneLineError(err, "hangsloth: This dump is from a .NET Framework process (the desktop CLR), which is not supported yet.");
        Assert.DoesNotContain("--dac", err);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_dump_with_both_runtimes_is_read_as_the_core_one(bool coreFirst)
    {
        // Core is preferred, so this is not rejected as .NET Framework. On Linux and macOS it then stops at "Windows dump".
        var path = WriteFile("both.dmp", MiniDump.Windows(coreFirst ? [MiniDump.CoreClr, MiniDump.DesktopClr] : [MiniDump.DesktopClr, MiniDump.CoreClr]));
        // On Windows the loader goes on to the DAC; a text file as --dac ends it there without a symbol server download.
        var notADac = WriteFile("not-a-dac.dll", "this is a text file"u8.ToArray());
        var (exit, _, err) = Run([path, "--no-color", "--dac", notADac]);
        Assert.Equal(1, exit);
        Assert.DoesNotContain("Framework", err);
        if (!OperatingSystem.IsWindows())
            AssertOneLineError(err, "hangsloth: This dump comes from Windows, so it has to be analyzed on Windows");
    }

    [SkippableFact]
    public void A_windows_dump_on_another_os_does_not_suggest_dac()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows can read Windows dumps.");
        var path = WriteFile("win.dmp", MiniDump.Windows([MiniDump.CoreClr]));
        var (exit, _, err) = Run([path, "--no-color", "--dac", path]);
        Assert.Equal(1, exit);
        AssertOneLineError(err, "hangsloth: This dump comes from Windows, so it has to be analyzed on Windows");
        Assert.DoesNotContain("--dac", err);
    }

    [Fact]
    public void An_unexpected_exception_is_one_line_with_exit_code_1()
    {
        var (exit, stdout, err) = Run(["whatever.dmp"], (_, _) => throw new KeyNotFoundException("no such key"));
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Equal("hangsloth: unexpected error (KeyNotFoundException): no such key", err.TrimEnd());
    }

    [Fact]
    public void The_full_exception_is_printed_only_with_hangsloth_debug()
    {
        var old = Environment.GetEnvironmentVariable("HANGSLOTH_DEBUG");
        try
        {
            Environment.SetEnvironmentVariable("HANGSLOTH_DEBUG", "1");
            var (exit, _, err) = Run(["whatever.dmp"], (_, _) => throw new KeyNotFoundException("no such key"));
            Assert.Equal(1, exit);
            Assert.StartsWith("hangsloth: unexpected error (KeyNotFoundException): no such key", err);
            Assert.Contains("System.Collections.Generic.KeyNotFoundException: no such key", err);
            Assert.Contains("   at ", err);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HANGSLOTH_DEBUG", old);
        }
    }

    private static ProcessSnapshot Deadlocked() => Process(
        Thread(1, [.. ResultWait, App("MyApp.ReportService", "Generate", "Reports.cs", 14)]) with { BlockedOnTask = Task500 },
        Thread(5, [.. MonitorEnter, App("MyApp.ReportService", "CollectRows", "Reports.cs", 20)], ".NET TP Worker") with { WaitingForLock = 0x100, RunningTask = 0x500 }) with
    {
        Locks = [Lock100],
    };

    private static readonly TaskInfo Task500 = new() { Address = 0x500, TypeName = "Task", Kind = TaskKind.Delegate, Description = "Task.Run(ReportService.CollectRows)" };
    private static readonly MonitorLock Lock100 = new() { Object = new ObjectRef(0x100, "System.Object"), FieldName = "ReportService._sync", OwnerThreadId = 1, WaiterThreadIds = [5] };

    private sealed class ThrowingDetector : IDetector
    {
        public IEnumerable<Finding> Detect(ProcessSnapshot snapshot, IReadOnlySet<int> claimed) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void A_throwing_detector_does_not_lose_the_findings_of_the_others()
    {
        var report = new HangAnalyzer([new ThrowingDetector(), new WaitCycleDetector()]).Analyze(Deadlocked());
        Assert.Equal("wait-cycle", Assert.Single(report.Findings).Id);
        Assert.Equal(["ThrowingDetector"], report.FailedDetectors);
        Assert.Contains("The ThrowingDetector check failed (InvalidOperationException: boom); the other checks are not affected.", report.Snapshot.Warnings);
    }

    [Fact]
    public void A_failed_detector_with_a_finding_still_exits_with_2()
    {
        var (exit, stdout, err) = Run(["x.dmp", "--no-color"], (_, _) => Deadlocked(), () => new HangAnalyzer([new ThrowingDetector(), new WaitCycleDetector()]));
        Assert.Equal(2, exit);
        Assert.Contains("The ThrowingDetector check failed", stdout);
        Assert.Equal("", err);
    }

    [Fact]
    public void A_failed_detector_without_a_finding_is_exit_1_not_a_clean_bill_of_health()
    {
        var (exit, stdout, err) = Run(["x.dmp", "--no-color"], (_, _) => Process(Thread(1, [App("MyApp.Program", "Main", "Program.cs", 5)])), () => new HangAnalyzer([new ThrowingDetector()]));
        Assert.Equal(1, exit);
        Assert.Contains("The ThrowingDetector check failed", stdout);
        Assert.Contains("No hang patterns found", stdout);
        Assert.Equal("hangsloth: the ThrowingDetector check failed, so \"No hang patterns found\" cannot be trusted.", err.TrimEnd());
    }

    [Fact]
    public void Two_threads_with_the_same_running_task_and_a_repeated_lock_do_not_break_the_analysis()
    {
        var snapshot = Deadlocked();
        snapshot = snapshot with { Threads = [.. snapshot.Threads, Thread(6, MonitorEnter, "other") with { RunningTask = 0x500 }] };

        var f = Assert.Single(new WaitCycleDetector().Detect(snapshot with { Locks = [Lock100, Lock100] }, new HashSet<int>()));
        Assert.Equal("wait-cycle", f.Id);

        var report = new HangAnalyzer().Analyze(snapshot);
        Assert.Empty(report.FailedDetectors);
        Assert.Contains(report.Findings, x => x.Id == "wait-cycle");
    }

    /// A tiny Windows x64 minidump: a system info stream and a module list, no memory and no threads.
    private static class MiniDump
    {
        public record Module(string Name, ulong Base, uint Size, uint TimeStamp, ushort V1, ushort V2, ushort V3, ushort V4);

        public static readonly Module DesktopClr = new(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\clr.dll", 0x7FF700000000, 0xAC0000, 0x61000000, 4, 8, 4300, 0);
        public static readonly Module CoreClr = new(@"C:\Apps\Orders\coreclr.dll", 0x7FF600000000, 0x500000, 0x62000000, 10, 0, 25, 0);

        public static byte[] Windows(params Module[] runtimes)
        {
            Module[] modules =
            [
                new(@"C:\Program Files\MyApp\MyApp.exe", 0x400000, 0x10000, 0x60000000, 1, 0, 0, 0),
                new(@"C:\Windows\System32\ntdll.dll", 0x7FF800000000, 0x1F0000, 0x5F000000, 10, 0, 19041, 1),
                .. runtimes,
            ];

            const int streams = 4;
            var blobs = new List<byte[]>();
            var pos = 32 + 12 * streams;
            uint Add(byte[] b) { var rva = (uint)pos; blobs.Add(b); pos += b.Length; return rva; }

            // SystemInfo: processor architecture AMD64, Windows 10 (platform id 2 = VER_PLATFORM_WIN32_NT).
            var sys = new MemoryStream();
            var w = new BinaryWriter(sys);
            w.Write((ushort)9); w.Write((ushort)6); w.Write((ushort)0); w.Write((byte)4); w.Write((byte)1);
            w.Write(10u); w.Write(0u); w.Write(19045u); w.Write(2u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(new byte[24]);
            var sysBytes = sys.ToArray();
            var sysRva = Add(sysBytes);

            var nameRvas = modules.Select(m =>
            {
                var s = Encoding.Unicode.GetBytes(m.Name);
                var b = new MemoryStream();
                var bw = new BinaryWriter(b);
                bw.Write((uint)s.Length); bw.Write(s); bw.Write((ushort)0);
                return Add(b.ToArray());
            }).ToArray();

            var ml = new MemoryStream();
            var mw = new BinaryWriter(ml);
            mw.Write((uint)modules.Length);
            for (var i = 0; i < modules.Length; i++)
            {
                var m = modules[i];
                uint ms = ((uint)m.V1 << 16) | m.V2, ls = ((uint)m.V3 << 16) | m.V4;
                mw.Write(m.Base); mw.Write(m.Size); mw.Write(0u); mw.Write(m.TimeStamp); mw.Write(nameRvas[i]);
                foreach (var v in new uint[] { 0xFEEF04BD, 0x10000, ms, ls, ms, ls, 0x3F, 0, 0x40004, 2, 0, 0, 0 }) mw.Write(v);
                mw.Write(new byte[16]);   // CvRecord and MiscRecord locations
                mw.Write(0UL); mw.Write(0UL);
            }
            var mlBytes = ml.ToArray();
            var mlRva = Add(mlBytes);

            var tlBytes = BitConverter.GetBytes(0u);
            var tlRva = Add(tlBytes);

            var mem64 = new byte[16];
            BitConverter.GetBytes((ulong)(pos + 16)).CopyTo(mem64, 8);
            var memRva = Add(mem64);

            var output = new MemoryStream();
            var o = new BinaryWriter(output);
            o.Write(0x504D444Du); o.Write(0xA793u); o.Write((uint)streams); o.Write(32u); o.Write(0u); o.Write(0x60000000u); o.Write(2UL);
            foreach (var (type, size, rva) in new (uint, int, uint)[] { (7, sysBytes.Length, sysRva), (4, mlBytes.Length, mlRva), (3, tlBytes.Length, tlRva), (9, mem64.Length, memRva) })
            {
                o.Write(type); o.Write((uint)size); o.Write(rva);
            }
            foreach (var b in blobs) o.Write(b);
            return output.ToArray();
        }
    }
}
