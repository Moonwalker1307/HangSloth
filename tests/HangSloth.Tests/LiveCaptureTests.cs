using System.Diagnostics;
using System.Net.Sockets;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// Live capture: the temporary dump is unique per run and never left behind, except when the analysis
/// failed and the user needs it; a process that cannot answer is an error with the reason, not a hang.
[Collection("Cli")]
public class LiveCaptureTests
{
    private static ProcessSnapshot Healthy() => Process(Thread(1, [App("Orders.Program", "Main", "Program.cs", 5)]));

    /// A temporary dump as a real capture would leave it, with a few bytes in it.
    private static CapturedDump FakeCapture(int pid, string? keep, Action<string> progress)
    {
        var dump = keep is null ? CapturedDump.CreateTemporary(pid, privateFolder: true) : new CapturedDump(Path.GetFullPath(keep), temporary: false, folder: null);
        progress($"Taking a full dump of process {pid} to {dump.Path}...");
        File.WriteAllBytes(dump.Path, [1, 2, 3]);
        return dump;
    }

    [Fact]
    public void The_temporary_dump_is_deleted_after_a_successful_analysis()
    {
        string? path = null;
        var (exit, _, err) = CliTests.Run(["4242", "--no-color"], (p, _) => { path = p; return Healthy(); }, capture: FakeCapture);
        Assert.Equal(0, exit);
        Assert.Equal($"Taking a full dump of process 4242 to {path}...", err.TrimEnd());
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void The_dump_is_kept_and_named_when_the_analysis_fails()
    {
        string? path = null;
        var (exit, _, err) = CliTests.Run(["4242", "--no-color"], (p, _) => { path = p; throw new HangSlothException("Could not load the debugging library (DAC)."); }, capture: FakeCapture);
        try
        {
            Assert.Equal(1, exit);
            var lines = err.TrimEnd().Split('\n');
            Assert.Equal(2, lines.Length);
            var quoted = path!.Contains(' ') ? $"\"{path}\"" : path;
            Assert.Equal($"hangsloth: Could not load the debugging library (DAC). The dump was kept at {path}; analyze it again with: hangsloth {quoted}", lines[1].TrimEnd());
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (path is not null) Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void The_dump_is_kept_after_an_unexpected_error_too()
    {
        string? path = null;
        var (exit, _, err) = CliTests.Run(["4242"], (p, _) => { path = p; throw new KeyNotFoundException("no such key"); }, capture: FakeCapture);
        try
        {
            Assert.Equal(1, exit);
            Assert.StartsWith("hangsloth: unexpected error (KeyNotFoundException): no such key The dump was kept at ", err.TrimEnd().Split('\n')[^1]);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (path is not null) Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void A_dump_kept_with_keep_dump_gets_no_extra_note()
    {
        var keep = Path.Combine(Path.GetTempPath(), $"hangsloth-keep-{Guid.NewGuid():N}.dmp");
        try
        {
            var (exit, _, err) = CliTests.Run(["4242", "--keep-dump", keep], (_, _) => throw new HangSlothException("boom"), capture: FakeCapture);
            Assert.Equal(1, exit);
            Assert.Equal("hangsloth: boom", err.TrimEnd().Split('\n')[^1]);
            Assert.True(File.Exists(keep));
        }
        finally
        {
            File.Delete(keep);
        }
    }

    [Fact]
    public void A_live_report_names_the_process_and_the_kept_dump()
    {
        var keep = Path.Combine(Path.GetTempPath(), $"hangsloth-keep-{Guid.NewGuid():N}.dmp");
        try
        {
            var (exit, stdout, _) = CliTests.Run(["4242", "--keep-dump", keep, "--no-color"], (_, _) => Healthy(), capture: FakeCapture);
            Assert.Equal(0, exit);
            Assert.StartsWith($"HangSloth · process 4242 (dump saved to {keep}) · ", stdout);
            Assert.True(File.Exists(keep));
        }
        finally
        {
            File.Delete(keep);
        }
    }

    [Fact]
    public void Two_temporary_dumps_of_one_process_never_share_a_path()
    {
        using var a = CapturedDump.CreateTemporary(4242, privateFolder: true);
        using var b = CapturedDump.CreateTemporary(4242, privateFolder: true);
        using var c = CapturedDump.CreateTemporary(4242, privateFolder: false);
        using var d = CapturedDump.CreateTemporary(4242, privateFolder: false);
        Assert.NotEqual(Path.GetDirectoryName(a.Path), Path.GetDirectoryName(b.Path));
        Assert.NotEqual(c.Path, d.Path);
        Assert.True(Path.IsPathFullyQualified(a.Path));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(a.Path)!));
    }

    [Fact]
    public void Disposing_deletes_the_dump_and_its_folder_and_a_kept_one_stays()
    {
        var dump = CapturedDump.CreateTemporary(4242, privateFolder: true);
        File.WriteAllBytes(dump.Path, [1]);
        dump.Dispose();
        Assert.False(Directory.Exists(Path.GetDirectoryName(dump.Path)));
        Assert.Equal("", dump.KeepAndDescribe());

        var kept = CapturedDump.CreateTemporary(4242, privateFolder: true);
        File.WriteAllBytes(kept.Path, [1]);
        Assert.Contains(kept.Path, kept.KeepAndDescribe());
        kept.Dispose();
        Assert.True(File.Exists(kept.Path));
        Directory.Delete(Path.GetDirectoryName(kept.Path)!, recursive: true);
    }

    private static int ExitedPid()
    {
        using var p = System.Diagnostics.Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.Id;
    }

    [Fact]
    public void A_process_that_is_not_running_is_named_as_such()
    {
        var pid = ExitedPid();
        var e = Assert.Throws<HangSlothException>(() => DumpLoader.CaptureProcess(pid));
        Assert.Equal($"No process with id {pid} is running.", e.Message);
    }

    [SkippableFact]
    public void A_thread_id_is_named_as_a_thread_and_not_paused()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Thread ids are read from /proc, which only Linux has.");
        var started = new ManualResetEventSlim();
        var done = new ManualResetEventSlim();
        var tid = 0;
        // /proc/thread-self links to "<pid>/task/<tid>" for the thread that reads it.
        var worker = new System.Threading.Thread(() => { tid = int.Parse(Path.GetFileName(new FileInfo("/proc/thread-self").LinkTarget!)); started.Set(); done.Wait(); });
        worker.Start();
        try
        {
            started.Wait();
            var progress = new List<string>();
            var e = Assert.Throws<HangSlothException>(() => DumpLoader.CaptureProcess(tid, null, progress.Add));
            Assert.Equal($"{tid} is the id of a thread in process {Environment.ProcessId}, not of a process. Pass {Environment.ProcessId} instead.", e.Message);
            Assert.Empty(progress);
        }
        finally
        {
            done.Set();
            worker.Join();
        }
    }

    /// A process that is not .NET and does nothing, to stop or to give a fake diagnostics channel.
    private static System.Diagnostics.Process StartSleeper() =>
        System.Diagnostics.Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;

    private static void Signal(string signal, int pid) => System.Diagnostics.Process.Start("kill", [$"-{signal}", pid.ToString()])!.WaitForExit();

    [SkippableFact]
    public void A_stopped_process_is_an_error_at_once_instead_of_a_hang()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The process state is read from /proc, which only Linux has.");
        using var sleeper = StartSleeper();
        try
        {
            Signal("STOP", sleeper.Id);
            for (var i = 0; i < 50 && !File.ReadAllText($"/proc/{sleeper.Id}/status").Contains("State:\tT"); i++) System.Threading.Thread.Sleep(20);
            var progress = new List<string>();
            var watch = Stopwatch.StartNew();
            var e = Assert.Throws<HangSlothException>(() => DumpLoader.CaptureProcess(sleeper.Id, null, progress.Add));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
            Assert.Equal($"Process {sleeper.Id} is stopped (Ctrl+Z or kill -STOP), so it cannot write a dump. Resume it (fg, or kill -CONT {sleeper.Id}) and run HangSloth again.", e.Message);
            Assert.Empty(progress);
        }
        finally
        {
            sleeper.Kill();
        }
    }

    [SkippableFact]
    public void A_process_held_by_a_tracer_is_an_error_that_names_it()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The tracer is read from /proc, which only Linux has.");
        Skip.IfNot(File.Exists("/usr/bin/strace"), "Needs strace to attach a tracer.");
        using var sleeper = StartSleeper();
        using var strace = System.Diagnostics.Process.Start(new ProcessStartInfo("/usr/bin/strace", ["-p", sleeper.Id.ToString(), "-o", "/dev/null"])
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        _ = strace.StandardError.ReadToEndAsync();
        try
        {
            var attached = false;
            for (var i = 0; i < 100 && !(attached = !File.ReadAllText($"/proc/{sleeper.Id}/status").Contains("TracerPid:\t0\n")); i++) System.Threading.Thread.Sleep(50);
            Skip.IfNot(attached, "strace could not attach here (ptrace not allowed).");
            var e = Assert.Throws<HangSlothException>(() => DumpLoader.CaptureProcess(sleeper.Id));
            Assert.StartsWith($"Process {sleeper.Id} is attached to strace (process {strace.Id}), so it cannot write a dump. Detach it and run HangSloth again", e.Message);
        }
        finally
        {
            try { strace.Kill(); } catch { }
            sleeper.Kill();
        }
    }

    [SkippableFact]
    public void A_process_that_never_answers_is_given_up_on_with_the_reason()
    {
        Skip.If(OperatingSystem.IsWindows(), "The fake diagnostics channel is a Unix domain socket; Windows uses a named pipe.");
        using var sleeper = StartSleeper();
        // A diagnostics channel that takes the dump request and never answers, like a frozen app.
        var socketPath = Path.Combine(Path.GetTempPath(), $"dotnet-diagnostic-{sleeper.Id}-{Environment.TickCount64}-socket");
        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(socketPath));
        server.Listen();
        var accepted = new System.Collections.Concurrent.ConcurrentBag<Socket>();
        _ = Task.Run(() => { while (true) accepted.Add(server.Accept()); });
        try
        {
            var progress = new List<string>();
            var watch = Stopwatch.StartNew();
            var e = Assert.Throws<HangSlothException>(() =>
                DumpLoader.CaptureProcess(sleeper.Id, null, progress.Add, stallLimit: TimeSpan.FromSeconds(3), hintAfter: TimeSpan.FromSeconds(1)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
            Assert.Equal(
                $"Process {sleeper.Id} did not start writing its dump within 3 seconds, so HangSloth gave up. " +
                "If the app is paused (docker pause, a debugger) or frozen, resume it and try again, or take a dump with createdump, gcore or procdump -ma and pass that file.",
                e.Message);
            Assert.Equal(2, progress.Count);
            Assert.StartsWith($"Taking a full dump of process {sleeper.Id} to ", progress[0]);
            Assert.EndsWith(" (the app is paused while it is written; the file can be several GB)...", progress[0]);
            Assert.Equal($"Process {sleeper.Id} has not started writing its dump yet; HangSloth waits up to 3 seconds for it.", progress[1]);

            // The given-up dump's folder is gone, so a late answer cannot leave a dump behind.
            var path = progress[0][$"Taking a full dump of process {sleeper.Id} to ".Length..progress[0].IndexOf(" (the app", StringComparison.Ordinal)];
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
            Assert.NotEmpty(accepted);
        }
        finally
        {
            sleeper.Kill();
            foreach (var s in accepted) s.Dispose();
            File.Delete(socketPath);
        }
    }
}
