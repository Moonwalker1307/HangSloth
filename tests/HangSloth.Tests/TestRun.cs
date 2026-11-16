using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HangSloth.Analysis;
using HangSloth.Collection;

namespace HangSloth.Tests;

/// What one test run owns on this machine: a private folder (sample builds and dumps), the samples it started, and
/// clean-up steps such as dropping the lab's databases. All of it goes when the run ends. If the run is killed
/// instead, a watchdog (Unix) or a job object (Windows) stops its samples, and the next run removes the folder.
internal static class TestRun
{
    public const string FolderPrefix = "hangsloth-run-";
    private static readonly Lazy<string> Folder = new(Begin);
    private static readonly ConcurrentDictionary<int, Process> Samples = new();
    private static readonly List<Action> Cleanups = [];
    private static readonly List<PosixSignalRegistration> Signals = [];
    private static IntPtr _job;
    private static int _ended;

    /// The run's own folder under the temp folder, created on first use.
    public static string Directory => Folder.Value;

    /// Names this run outside its folder (databases): the start time first, so that leftovers can be told by age.
    public static string Id { get; } = $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}";

    public static string NewDumpPath(string name) => Path.Combine(Directory, $"{name}-{Guid.NewGuid():N}.dmp");

    /// Runs once, in reverse order of registration: when the end-to-end tests are done, or when the run ends.
    public static void OnEnd(Action cleanup)
    {
        lock (Cleanups) Cleanups.Add(cleanup);
    }

    public static void Track(Process process)
    {
        _ = Directory;
        Samples[process.Id] = process;
        try { File.WriteAllText(Path.Combine(Directory, "pids", process.Id.ToString()), StartTicks(process).ToString()); } catch { /* the watchdog finds it by its path anyway */ }
        if (_job != IntPtr.Zero) ProcessJob.Add(_job, process);
    }

    public static void Untrack(Process process)
    {
        Samples.TryRemove(process.Id, out _);
        try { File.Delete(Path.Combine(Directory, "pids", process.Id.ToString())); } catch { }
    }

    private static string Begin()
    {
        Sweep(Path.GetTempPath());
        var folder = System.IO.Directory.CreateTempSubdirectory(FolderPrefix).FullName;
        System.IO.Directory.CreateDirectory(Path.Combine(folder, "pids"));
        using var me = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(folder, "owner"), $"{me.Id} {StartTicks(me)}");

        if (OperatingSystem.IsWindows())
            _job = ProcessJob.Create();
        else
        {
            StartWatchdog(me.Id, folder);
            // Ctrl+C and kill: the default action still follows, these only stop the samples first.
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
                Signals.Add(PosixSignalRegistration.Create(signal, _ => End()));
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => End();
        return folder;
    }

    private static void End()
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        foreach (var process in Samples.Values)
        {
            try { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } catch { }
        }
        RunCleanups();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { }
    }

    /// Runs the clean-up steps registered so far, each once: when the end-to-end tests are done (the test host
    /// may not give the process much time to exit), and otherwise when the process ends.
    public static void RunCleanups()
    {
        Action[] cleanups;
        lock (Cleanups)
        {
            cleanups = [.. Cleanups];
            Cleanups.Clear();
        }
        foreach (var cleanup in Enumerable.Reverse(cleanups))
        {
            try { cleanup(); } catch { }
        }
    }

    /// Removes what earlier runs left behind: the folders of runs whose test process is gone (stopping any sample
    /// still running from one), and dumps of the time before runs had their own folder.
    public static void Sweep(string temp)
    {
        foreach (var folder in SafeEnumerate(() => System.IO.Directory.EnumerateDirectories(temp, FolderPrefix + "*")))
        {
            try
            {
                var owner = Path.Combine(folder, "owner");
                var stale = File.Exists(owner)
                    ? !IsRunning(File.ReadAllText(owner))
                    : System.IO.Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddHours(-1);
                if (!stale) continue;
                foreach (var pid in SafeEnumerate(() => System.IO.Directory.EnumerateFiles(Path.Combine(folder, "pids"))))
                {
                    if (!int.TryParse(Path.GetFileName(pid), out var id) || !long.TryParse(File.ReadAllText(pid), out var ticks)) continue;
                    try
                    {
                        using var process = Process.GetProcessById(id);
                        if (Math.Abs(StartTicks(process) - ticks) < TimeSpan.TicksPerSecond * 2) process.Kill(entireProcessTree: true);
                    }
                    catch { /* gone already */ }
                }
                System.IO.Directory.Delete(folder, recursive: true);
            }
            catch { /* another user's folder, or one that is being removed right now */ }
        }
        foreach (var dump in SafeEnumerate(() => System.IO.Directory.EnumerateFiles(temp, "hangsloth-test-*.dmp")))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(dump) < DateTime.UtcNow.AddHours(-1)) File.Delete(dump);
            }
            catch { }
        }
    }

    /// "pid start-ticks", as written into a run's owner file.
    private static bool IsRunning(string owner)
    {
        var parts = owner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var pid) || !long.TryParse(parts[1], out var ticks)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && Math.Abs(StartTicks(process) - ticks) < TimeSpan.TicksPerSecond * 2;
        }
        catch
        {
            return false;
        }
    }

    private static long StartTicks(Process process) => process.StartTime.ToUniversalTime().Ticks;

    private static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> list)
    {
        try { return list().ToList(); } catch { return []; }
    }

    /// The watchdog waits for the test process to go and then kills every process that runs something from the
    /// run's folder, so a test process killed outright (a cancelled CI job, a hang timeout) leaves no sample behind.
    /// It gets the folder from its environment, not its arguments, so that it doesn't match itself. Without a run
    /// folder in that variable it does nothing: an empty one would match every process on the machine.
    internal const string WatchdogScript = """
        exec </dev/null >/dev/null 2>&1
        case "$HANGSLOTH_RUN_DIR" in */hangsloth-run-*) ;; *) exit 0 ;; esac
        while kill -0 "$1" 2>/dev/null; do sleep 1; done
        ps -ww -eo pid=,args= | awk 'index($0, ENVIRON["HANGSLOTH_RUN_DIR"] "/") { print $1 }' | while read -r pid; do kill -9 "$pid" 2>/dev/null; done
        rm -rf "$HANGSLOTH_RUN_DIR"
        """;

    internal static Process? StartWatchdog(int owner, string folder)
    {
        try
        {
            var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            foreach (var arg in new[] { "-c", WatchdogScript, "hangsloth-watchdog", owner.ToString() }) start.ArgumentList.Add(arg);
            start.Environment["HANGSLOTH_RUN_DIR"] = folder;
            return Process.Start(start);
        }
        catch
        {
            return null;
        }
    }
}

/// The end-to-end tests run one after another in this collection; when the last of them is done, the run's
/// databases and containers go.
[CollectionDefinition("EndToEnd")]
public sealed class EndToEndCollection : ICollectionFixture<EndToEndCollection.Cleanup>
{
    public sealed class Cleanup : IDisposable
    {
        public void Dispose() => TestRun.RunCleanups();
    }
}

/// A sample app started by a test: its output is collected from the start, so that a sample that never gets ready
/// can say why, and it is stopped when the test is done with it or the run ends.
internal sealed class Sample : IDisposable
{
    private readonly StringBuilder _output = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _name;

    public Process Process { get; }

    private Sample(Process process, string name)
    {
        Process = process;
        _name = name;
    }

    /// Starts `dotnet <app> <args>`; `environment` adds to (or, with null, removes from) the test process's environment.
    public static Sample Start(string app, string[] args, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(app);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }

        var process = new Process { StartInfo = start };
        var sample = new Sample(process, args.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(app));
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) sample._ready.TrySetResult(false);
            else if (e.Data == "READY") sample._ready.TrySetResult(true);
            else sample.Append(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) sample.Append(e.Data);
        };
        process.Start();
        TestRun.Track(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return sample;
    }

    private void Append(string line)
    {
        lock (_output)
            if (_output.Length < 20_000) _output.AppendLine(line);
    }

    /// Waits for the sample's READY line. If it doesn't come, the failure says whether the sample exited (with its
    /// exit code and output) or is still running (with its output so far and what HangSloth sees in it).
    public void WaitUntilReady(TimeSpan timeout)
    {
        if (_ready.Task.Wait(timeout) && _ready.Task.Result) return;
        Assert.Fail(WhyNotReady(timeout));
    }

    private string WhyNotReady(TimeSpan timeout)
    {
        var message = new StringBuilder($"Sample '{_name}' never became ready: ");
        if (Process.WaitForExit(5000))
        {
            // The parameterless wait also waits for the last lines of output, but not for ever: a child process can hold the pipes.
            Task.Run(() => Process.WaitForExit()).Wait(TimeSpan.FromSeconds(5));
            message.Append($"it exited with code {Process.ExitCode}.");
        }
        else
        {
            message.Append($"it is still running after {timeout.TotalSeconds:0} seconds.");
            try
            {
                var dump = TestRun.NewDumpPath($"not-ready-{_name}");
                try
                {
                    DumpLoader.CollectDump(Process.Id, dump);
                    var report = new HangAnalyzer().Analyze(DumpLoader.LoadDump(dump));
                    message.Append(" HangSloth says it is stuck here:");
                    foreach (var f in report.Findings.Take(3)) message.Append($"\n  [{f.Severity}] {f.Id}: {f.Summary}");
                    if (report.Findings.Count == 0) message.Append(" nothing found.");
                }
                finally
                {
                    File.Delete(dump);
                }
            }
            catch (Exception ex)
            {
                message.Append($" (It couldn't be dumped: {ex.Message})");
            }
        }
        lock (_output)
            message.Append(_output.Length == 0 ? "\nIt wrote nothing." : $"\nIts output:\n{_output.ToString().TrimEnd()}");
        return message.ToString();
    }

    public void Dispose()
    {
        try { Process.Kill(entireProcessTree: true); } catch { }
        try { Process.WaitForExit(10_000); } catch { }
        TestRun.Untrack(Process);
        Process.Dispose();
    }
}

/// A Windows job object that kills every process in it when its last handle closes, which the system does when the
/// test process ends in any way.
internal static class ProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    public static IntPtr Create()
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
            if (SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) return job;
            CloseHandle(job);
        }
        catch { /* best effort: without it, a killed run can leave samples behind */ }
        return IntPtr.Zero;
    }

    public static bool Add(IntPtr job, Process process)
    {
        try { return AssignProcessToJobObject(job, process.Handle); } catch { return false; }
    }

    public static void Close(IntPtr job) => CloseHandle(job);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
