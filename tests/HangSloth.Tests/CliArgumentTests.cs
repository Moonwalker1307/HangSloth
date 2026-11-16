using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// An option never takes the next option as its value, and combinations that cannot work are usage errors.
[Collection("Cli")]
public class CliArgumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hangsloth-args-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static ProcessSnapshot Healthy() => Process(Thread(1, [App("Orders.Program", "Main", "Program.cs", 5)]));

    private static void AssertUsageError(int exit, string stdout, string err, string message)
    {
        Assert.Equal(1, exit);
        Assert.Equal("", stdout);
        Assert.Equal(message, err.Split('\n')[0].TrimEnd());
        Assert.Contains("Usage:", err);
    }

    private static CapturedDump NoCapture(int pid, string? keep, Action<string> progress) =>
        throw new InvalidOperationException("The app must not be paused for a run that cannot work.");

    [Fact]
    public void An_option_does_not_take_the_next_option_as_its_value()
    {
        var (exit, stdout, err) = CliTests.Run(["orders.dmp", "--symbols", "--json"], (_, _) => Healthy());
        AssertUsageError(exit, stdout, err, "--symbols needs a folder, got '--json'. Write --symbols=<folder> for one that starts with '-'.");
    }

    [Fact]
    public void Keep_dump_does_not_write_the_dump_to_a_file_named_after_the_next_option()
    {
        var (exit, stdout, err) = CliTests.Run(["4242", "--keep-dump", "--json"], (_, _) => Healthy(), capture: NoCapture);
        AssertUsageError(exit, stdout, err, "--keep-dump needs a file, got '--json'. Write --keep-dump=<file> for one that starts with '-'.");
    }

    [Fact]
    public void An_option_at_the_end_without_its_value_is_a_usage_error()
    {
        var (exit, stdout, err) = CliTests.Run(["orders.dmp", "--dac"], (_, _) => Healthy());
        AssertUsageError(exit, stdout, err, "--dac needs a file.");
    }

    [Fact]
    public void A_value_can_be_given_with_an_equals_sign_even_when_it_starts_with_a_dash()
    {
        var symbols = Directory.CreateDirectory(Path.Combine(_dir, "-symbols")).FullName;
        LoadOptions? seen = null;
        var (exit, _, err) = CliTests.Run(["orders.dmp", $"--symbols={symbols}", "--no-color"], (_, o) => { seen = o; return Healthy(); });
        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Equal([symbols], seen!.SymbolDirectories);
    }

    [Fact]
    public void A_flag_with_a_value_is_a_usage_error()
    {
        var (exit, stdout, err) = CliTests.Run(["orders.dmp", "--json=yes"], (_, _) => Healthy());
        AssertUsageError(exit, stdout, err, "--json does not take a value.");
    }

    [Fact]
    public void Two_different_dumps_to_keep_are_a_usage_error()
    {
        var (exit, stdout, err) = CliTests.Run(["4242", "--keep-dump", "a.dmp", "--keep-dump", "b.dmp"], capture: NoCapture);
        AssertUsageError(exit, stdout, err, "--keep-dump is given twice.");
    }

    [Fact]
    public void A_missing_target_says_so_on_stderr()
    {
        var (exit, stdout, err) = CliTests.Run(["--json"]);
        AssertUsageError(exit, stdout, err, "Missing <dump-file> or <pid>.");
    }

    [Fact]
    public void A_symbols_folder_that_swallows_the_target_is_a_missing_target()
    {
        var (exit, stdout, err) = CliTests.Run(["--symbols", "orders.dmp"]);
        AssertUsageError(exit, stdout, err, "Missing <dump-file> or <pid>.");
    }

    [Fact]
    public void All_threads_with_json_says_it_has_no_effect()
    {
        var (exit, stdout, err) = CliTests.Run(["orders.dmp", "--json", "--all-threads"], (_, _) => Healthy());
        Assert.Equal(0, exit);
        Assert.StartsWith("{", stdout.TrimStart());
        Assert.Equal("hangsloth: --all-threads has no effect with --json, which always has every thread's stack.", err.TrimEnd());
    }

    [Fact]
    public void A_missing_dac_is_reported_before_the_app_is_paused()
    {
        var missing = Path.Combine(_dir, "no-such-libmscordaccore.so");
        var (exit, _, err) = CliTests.Run(["4242", "--dac", missing], capture: NoCapture);
        Assert.Equal(1, exit);
        Assert.Equal($"hangsloth: DAC not found: {missing}", err.TrimEnd());
    }

    [Fact]
    public void A_missing_symbols_folder_is_reported_before_the_app_is_paused()
    {
        var missing = Path.Combine(_dir, "no-such-folder");
        var (exit, _, err) = CliTests.Run(["4242", "--symbols", missing], capture: NoCapture);
        Assert.Equal(1, exit);
        Assert.Equal($"hangsloth: Symbols folder not found: {missing}", err.TrimEnd());
    }

    [Fact]
    public void Every_option_still_works()
    {
        var symbols = Directory.CreateDirectory(Path.Combine(_dir, "symbols")).FullName;
        var dac = Path.Combine(_dir, "libmscordaccore.so");
        File.WriteAllText(dac, "");
        LoadOptions? seen = null;
        var (exit, stdout, err) = CliTests.Run(["orders.dmp", "--symbols", symbols, "--dac", dac, "--all-threads", "--no-color"], (_, o) => { seen = o; return Healthy(); });
        Assert.Equal(0, exit);
        Assert.Equal("", err);
        Assert.Contains("No hang patterns found", stdout);
        Assert.Equal([symbols], seen!.SymbolDirectories);
        Assert.Equal(dac, seen.DacPath);
    }
}
