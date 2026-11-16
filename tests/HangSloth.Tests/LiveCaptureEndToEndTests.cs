using System.Diagnostics;

namespace HangSloth.Tests;

/// Live capture and cut dumps on real files: the CLI says what it is doing, leaves no dump behind,
/// keeps the one it needs when the analysis fails, and says "cut off" for a dump that is incomplete.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class LiveCaptureEndToEndTests : IClassFixture<CliEndToEndTests.DumpFixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hangsloth-live-e2e-").FullName;
    private readonly CliEndToEndTests.DumpFixture _fixture;

    public LiveCaptureEndToEndTests(CliEndToEndTests.DumpFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(999)]
    [InlineData(970)]
    [InlineData(500)]
    public void A_real_dump_cut_off_anywhere_is_exit_1_and_says_so(int perMille)
    {
        var dump = _fixture.Dump;
        var cut = Path.Combine(_dir, $"cut-{perMille}.dmp");
        using (var source = File.OpenRead(dump))
        using (var target = File.Create(cut))
        {
            var keep = source.Length * perMille / 1000;
            var buffer = new byte[1 << 20];
            for (long done = 0; done < keep;)
            {
                var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, keep - done));
                target.Write(buffer, 0, n);
                done += n;
            }
        }

        var (exit, stdout, err) = CliTests.Run([cut, "--no-color"]);
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.StartsWith($"hangsloth: '{cut}' looks cut off: the file has ", err);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("--dac", err);
    }

    [Fact]
    public void The_complete_dump_is_still_analysed()
    {
        // The header check knows this OS's real dumps (a minidump on Windows) and finds them complete.
        using (var file = File.OpenRead(_fixture.Dump))
        {
            var extent = HangSloth.Collection.DumpLoader.DumpExtent(file);
            Assert.NotNull(extent);
            Assert.InRange(extent!.Value, 1, file.Length);
        }
        var (exit, stdout, err) = CliTests.Run([_fixture.Dump, "--no-color"]);
        Assert.Equal(2, exit);
        Assert.Contains("Deadlock: threads waiting on each other's locks", stdout);
        Assert.Equal("", err);
    }

    /// A HangApp mode, started once it is READY; the shared Sample keeps reading its output (createdump writes its lines there).
    private static Sample StartSample(string mode)
    {
        var sample = Sample.Start(SampleApp.Build("HangApp"), [mode]);
        try
        {
            sample.WaitUntilReady(TimeSpan.FromSeconds(30));
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private static string DumpPathIn(string line, int pid)
    {
        var start = $"Taking a full dump of process {pid} to ";
        Assert.StartsWith(start, line);
        Assert.EndsWith(" (the app is paused while it is written; the file can be several GB)...", line);
        return line[start.Length..line.IndexOf(" (the app is paused", StringComparison.Ordinal)];
    }

    [Fact]
    public void A_live_capture_says_what_it_does_and_leaves_no_dump()
    {
        using var sample = StartSample("deadlock");
        var pid = sample.Process.Id;
        var (exit, stdout, err) = CliTests.Run([pid.ToString(), "--no-color"]);
        Assert.Equal(2, exit);
        Assert.StartsWith($"HangSloth · process {pid} · ", stdout);
        Assert.Contains("Deadlock: threads waiting on each other's locks", stdout);
        var line = Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries)).TrimEnd();
        var dump = DumpPathIn(line, pid);
        Assert.False(File.Exists(dump));
        // The sample runs as the test's user, so the dump had a folder of its own.
        Assert.False(Directory.Exists(Path.GetDirectoryName(dump)));
    }

    [Fact]
    public void A_live_capture_whose_analysis_fails_keeps_the_dump_for_another_try()
    {
        using var sample = StartSample("deadlock");
        var pid = sample.Process.Id;
        var notADac = Path.Combine(_dir, "not-a-dac.so");
        File.WriteAllText(notADac, "this is a text file");

        var (exit, _, err) = CliTests.Run([pid.ToString(), "--no-color", "--dac", notADac]);
        Assert.Equal(1, exit);
        var lines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        var dump = DumpPathIn(lines[0].TrimEnd(), pid);
        try
        {
            Assert.StartsWith($"hangsloth: Could not load '{notADac}' as the debugging library (DAC): ", lines[1]);
            var quoted = dump.Contains(' ') ? $"\"{dump}\"" : dump;
            Assert.EndsWith($" The dump was kept at {dump}; analyze it again with: hangsloth {quoted}", lines[1].TrimEnd());
            Assert.True(File.Exists(dump));

            var (again, stdout, againErr) = CliTests.Run([dump, "--no-color"]);
            Assert.Equal(2, again);
            Assert.Contains("Deadlock: threads waiting on each other's locks", stdout);
            Assert.Equal("", againErr);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dump)!, recursive: true); } catch { }
        }
    }

    [SkippableTheory]
    [InlineData("INT", 0)]
    [InlineData("INT", 300)]
    [InlineData("TERM", 0)]
    [InlineData("TERM", 300)]
    public void An_interrupted_live_capture_leaves_no_dump(string signal, int delayMs)
    {
        Skip.If(OperatingSystem.IsWindows(), "Sends a POSIX signal to the CLI, which Windows does not have.");
        using var sample = StartSample("deadlock");
        var temp = Directory.CreateDirectory(Path.Combine(_dir, "t")).FullName;
        var cli = typeof(HangSloth.Cli.Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet", [cli, sample.Process.Id.ToString(), "--no-color"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["TMPDIR"] = temp;
        using var hangsloth = Process.Start(start)!;
        _ = hangsloth.StandardOutput.ReadToEndAsync();
        var first = Task.Run(() => hangsloth.StandardError.ReadLine());
        Assert.True(first.Wait(TimeSpan.FromSeconds(60)), "hangsloth printed nothing.");
        var dump = DumpPathIn(first.Result ?? "", sample.Process.Id);
        Assert.StartsWith(temp, dump);

        System.Threading.Thread.Sleep(delayMs);
        Process.Start("kill", [$"-{signal}", hangsloth.Id.ToString()])!.WaitForExit();
        Assert.True(hangsloth.WaitForExit(TimeSpan.FromSeconds(60)), "hangsloth did not end after the signal.");

        // createdump, a child of the app, may still be writing: give it time to finish or fail.
        System.Threading.Thread.Sleep(1500);
        // The CLI's own runtime keeps its diagnostics socket in TMPDIR too; only HangSloth's files matter.
        Assert.Empty(Directory.GetFileSystemEntries(temp, "hangsloth-*"));
    }
}
