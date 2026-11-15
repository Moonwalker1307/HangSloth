using System.Diagnostics;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// Bad input around a real dump must end as exit code 1 with a one-line message, not as a stack trace.
[Trait("Category", "EndToEnd")]
[Collection("EndToEnd")]
public class CliEndToEndTests : IClassFixture<CliEndToEndTests.DumpFixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hangsloth-cli-e2e-").FullName;
    private readonly DumpFixture _fixture;

    public CliEndToEndTests(DumpFixture fixture) => _fixture = fixture;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// One dump of the HangApp 'deadlock' mode for the whole class; it is about 150 MB.
    public sealed class DumpFixture : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("hangsloth-cli-e2e-dump-").FullName;
        private string? _dump;

        public string Dump => _dump ??= Capture(Path.Combine(_dir, "deadlock.dmp"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private string CaptureDump() => _fixture.Dump;

    private static string Capture(string dump)
    {
        var app = SampleApp.Build("HangApp");
        using var process = Process.Start(new ProcessStartInfo("dotnet", [app, "deadlock"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        _ = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = Task.Run(() =>
            {
                while (process.StandardOutput.ReadLine() is { } line)
                    if (line == "READY") return true;
                return false;
            });
            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)) && ready.Result, "Sample 'deadlock' never became ready.");
            DumpLoader.CollectDump(process.Id, dump);
            return dump;
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public void A_truncated_dump_is_exit_1_with_one_line()
    {
        var dump = CaptureDump();
        var cut = Path.Combine(_dir, "cut.dmp");
        using (var source = File.OpenRead(dump))
        using (var target = File.Create(cut))
        {
            var buffer = new byte[4096];
            target.Write(buffer, 0, source.Read(buffer, 0, buffer.Length));
        }

        var (exit, _, err) = CliTests.Run([cut, "--no-color"]);
        Assert.Equal(1, exit);
        Assert.StartsWith("hangsloth: ", err);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (!OperatingSystem.IsWindows())
            Assert.Contains("the dump looks truncated or damaged", err);
    }

    [Fact]
    public void A_dac_that_is_not_a_dac_is_exit_1_with_one_line()
    {
        var dump = CaptureDump();
        var notADac = Path.Combine(_dir, "not-a-dac.so");
        File.WriteAllText(notADac, "this is a text file");

        var (exit, _, err) = CliTests.Run([dump, "--no-color", "--dac", notADac]);
        Assert.Equal(1, exit);
        Assert.StartsWith($"hangsloth: Could not load '{notADac}' as the debugging library (DAC): ", err);
        Assert.Single(err.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void A_dump_still_gets_analysed_with_the_cli()
    {
        var dump = CaptureDump();
        var (exit, stdout, err) = CliTests.Run([dump, "--no-color"]);
        Assert.Equal(2, exit);
        Assert.Contains("Deadlock: threads waiting on each other's locks", stdout);
        Assert.Equal("", err);
    }
}
