using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HangSloth.Model;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

public sealed record LoadOptions
{
    /// Extra directories to look for the app's binaries and PDBs in.
    public IReadOnlyList<string> SymbolDirectories { get; init; } = [];

    /// Explicit path to the runtime's DAC (libmscordaccore.so / mscordaccore.dll), for dumps from another machine.
    public string? DacPath { get; init; }
}

public sealed class HangSlothException(string message, Exception? inner = null) : Exception(message, inner);

public static partial class DumpLoader
{
    private static readonly object DacGate = new();

    /// Load a dump file (Windows minidump, Linux core dump or macOS dump) and take a snapshot of it.
    public static ProcessSnapshot LoadDump(string path, LoadOptions? options = null)
    {
        options ??= new LoadOptions();
        if (!File.Exists(path)) throw new HangSlothException($"Dump file not found: {path}");
        CheckComplete(path);

        DataTarget target;
        try
        {
            target = DataTarget.LoadDump(path, new DataTargetOptions { Limits = RaisedLimits() });
        }
        catch (Exception e)
        {
            throw new HangSlothException($"Could not open '{path}' as a memory dump: {e.Message}", e);
        }

        // The DAC keeps process-wide state (its PAL on Linux), so two dumps read at the same time from
        // one process can corrupt each other and crash with an access violation. Read one at a time.
        lock (DacGate)
        using (target)
        {
            var clr = PickRuntime(target, path);
            var warnings = new List<string>();
            using var runtime = CreateRuntime(clr, options, warnings);
            using var sources = new SourceResolver(options.SymbolDirectories.Append(Path.GetDirectoryName(Path.GetFullPath(path))!));
            var snapshot = SnapshotBuilder.Build(runtime, path, sources, RuntimeVersion(clr));
            return snapshot with { Warnings = [.. warnings, .. snapshot.Warnings] };
        }
    }

    /// ClrMD's caps guard against corrupt files, but the default of 10,000 ELF program headers (or Mach-O
    /// load commands) is hit by a real dump of an app with about 5,000 threads: there are two per thread.
    private static DataTargetLimits RaisedLimits() => new()
    {
        MaxElfProgramHeaders = 4_000_000,
        MaxMachOLoadCommands = 4_000_000,
        MaxThreads = 1_000_000,
    };

    /// The .NET (Core) runtime of the dump; a dump with only the desktop CLR is rejected with a clear message.
    private static ClrInfo PickRuntime(DataTarget target, string path)
    {
        List<ClrInfo> versions;
        try
        {
            // ClrMD reads the dump's module list lazily here, so a truncated or damaged file fails now.
            versions = [.. target.ClrVersions];
        }
        catch (Exception e)
        {
            throw new HangSlothException($"Could not read '{path}': the dump looks truncated or damaged: {e.Message}", e);
        }

        var clr = versions.FirstOrDefault(c => c.Flavor == ClrFlavor.Core) ?? versions.FirstOrDefault()
                  ?? throw new HangSlothException("No .NET runtime found in this dump. Is it a .NET (Core) process?");
        if (clr.Flavor == ClrFlavor.Desktop)
            throw new HangSlothException(
                "This dump is from a .NET Framework process (the desktop CLR), which is not supported yet. " +
                "HangSloth reads dumps of .NET Core 3.1+ and .NET 5+ apps.");

        // ClrMD cannot read a Windows dump on another OS, and no --dac changes that.
        if (target.DataReader.TargetPlatform == OSPlatform.Windows && !OperatingSystem.IsWindows())
            throw new HangSlothException(
                "This dump comes from Windows, so it has to be analyzed on Windows (a Windows dump cannot be read on Linux or macOS).");
        return clr;
    }

    /// How a report names a live process.
    public static string LiveSource(int pid, string? keepDumpAt) =>
        $"process {pid}" + (keepDumpAt is null ? "" : $" (dump saved to {keepDumpAt})");

    /// Without a sign of progress for this long, a capture is given up. Generous: writing a dump of
    /// many GB takes minutes, but it keeps growing all the while.
    internal static readonly TimeSpan CaptureStallLimit = TimeSpan.FromMinutes(2);

    /// When the dump file has not appeared after this long, say that HangSloth is still waiting.
    internal static readonly TimeSpan CaptureHintAfter = TimeSpan.FromSeconds(15);

    /// Capture a full dump of a live process to <paramref name="keepDumpAt"/>, or to a temporary file that is
    /// deleted when the result is disposed, or when HangSloth is interrupted (Ctrl+C, SIGTERM) meanwhile.
    /// <paramref name="progress"/> gets one line before the app is paused, and one more if it is slow to answer.
    public static CapturedDump CaptureProcess(int pid, string? keepDumpAt = null, Action<string>? progress = null) =>
        CaptureProcess(pid, keepDumpAt, progress, CaptureStallLimit, CaptureHintAfter);

    internal static CapturedDump CaptureProcess(int pid, string? keepDumpAt, Action<string>? progress, TimeSpan stallLimit, TimeSpan hintAfter)
    {
        CheckCanCapture(pid);
        var dump = keepDumpAt is null ? CapturedDump.CreateTemporary(pid, SameUserAs(pid)) : new CapturedDump(Path.GetFullPath(keepDumpAt), temporary: false, folder: null);
        try
        {
            progress?.Invoke($"Taking a full dump of process {pid} to {dump.Path} (the app is paused while it is written; the file can be several GB)...");
            CollectDump(pid, dump.Path, progress, stallLimit, hintAfter);
            return dump;
        }
        catch
        {
            dump.Dispose();
            throw;
        }
    }

    /// Fail at once, with the reason, for a process that cannot answer: on Linux a stopped or traced
    /// process would never write the dump (createdump has to attach to it, and only one tracer can).
    private static void CheckCanCapture(int pid)
    {
        try
        {
            using var _ = Process.GetProcessById(pid);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new HangSlothException($"No process with id {pid} is running.");
        }

        if (!OperatingSystem.IsLinux()) return;
        var status = ProcStatus(pid);
        // /proc/<tid> exists for every thread too, so a thread id looks like a running process.
        if (status.TryGetValue("Tgid", out var tgid) && int.TryParse(tgid, out var owner) && owner != pid)
            throw new HangSlothException($"{pid} is the id of a thread in process {owner}, not of a process. Pass {owner} instead.");
        if (status.TryGetValue("TracerPid", out var tracer) && int.TryParse(tracer, out var tracerPid) && tracerPid != 0)
        {
            var name = ReadProcFile($"/proc/{tracerPid}/comm")?.Trim();
            throw new HangSlothException(
                $"Process {pid} is attached to {(string.IsNullOrEmpty(name) ? "a debugger or tracer" : name)} (process {tracerPid}), so it cannot write a dump. " +
                "Detach it and run HangSloth again, or save a dump from the debugger (gcore in gdb, process save-core in lldb) and pass that file.");
        }
        if (status.TryGetValue("State", out var state) && state.Length > 0 && state[0] is 'T' or 't')
            throw new HangSlothException(
                $"Process {pid} is stopped (Ctrl+Z or kill -STOP), so it cannot write a dump. Resume it (fg, or kill -CONT {pid}) and run HangSloth again.");
    }

    /// On Linux the dump file is created by the target's user: a folder only HangSloth's user can write to
    /// would break `sudo hangsloth <pid>` on a service running as another user.
    private static bool SameUserAs(int pid)
    {
        if (!OperatingSystem.IsLinux()) return true;
        static string? FsUid(Dictionary<string, string> status) =>
            status.TryGetValue("Uid", out var uids) && uids.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } u ? u[3] : null;
        var (target, self) = (FsUid(ProcStatus(pid)), FsUid(ProcStatus(Environment.ProcessId)));
        return target is not null && target == self;
    }

    private static Dictionary<string, string> ProcStatus(int pid)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (ReadProcFile($"/proc/{pid}/status") ?? "").Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) fields[line[..colon]] = line[(colon + 1)..].Trim();
        }
        return fields;
    }

    private static string? ReadProcFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    /// Writes a full dump of a live process, and gives up (with the reason) when nothing happens for 2 minutes.
    public static void CollectDump(int pid, string dumpPath) => CollectDump(pid, dumpPath, null, CaptureStallLimit, CaptureHintAfter);

    internal static void CollectDump(int pid, string dumpPath, Action<string>? progress, TimeSpan stallLimit, TimeSpan hintAfter)
    {
        var fullPath = Path.GetFullPath(dumpPath);
        using var cancel = new CancellationTokenSource();
        try
        {
            var client = new DiagnosticsClient(pid);
            // Full, like dotnet-dump: smaller dump types can omit the JIT debug info (IL offsets) and loader heaps
            // (statics) that line numbers and lock names come from, notably on Windows.
            var write = client.WriteDumpAsync(DumpType.Full, fullPath, logDumpGeneration: false, cancel.Token);

            // A process that cannot answer (paused by docker pause, frozen, or stopped where it cannot be seen
            // up front) would keep the request forever, so watch the file grow and give up when it doesn't.
            var waited = Stopwatch.StartNew();
            var sinceProgress = Stopwatch.StartNew();
            var lastSize = -1L;
            var hinted = false;
            while (!Finished(write, TimeSpan.FromMilliseconds(250)))
            {
                var size = FileSize(fullPath);
                if (size != lastSize)
                {
                    lastSize = size;
                    sinceProgress.Restart();
                }
                else if (sinceProgress.Elapsed >= stallLimit)
                {
                    cancel.Cancel();
                    throw new HangSlothException(
                        $"Process {pid} {(size < 0 ? "did not start writing its dump" : "stopped writing its dump")} within {Duration(stallLimit)}, so HangSloth gave up. " +
                        "If the app is paused (docker pause, a debugger) or frozen, resume it and try again, or take a dump with createdump, gcore or procdump -ma and pass that file.");
                }
                if (!hinted && size < 0 && waited.Elapsed >= hintAfter)
                {
                    hinted = true;
                    progress?.Invoke($"Process {pid} has not started writing its dump yet; HangSloth waits up to {Duration(stallLimit)} for it.");
                }
            }
            write.GetAwaiter().GetResult();
        }
        catch (ServerNotAvailableException e)
        {
            throw new HangSlothException(
                $"Could not connect to process {pid}. Is it a .NET Core 3.1+ or .NET 5+ process, running as the same user as HangSloth (or is HangSloth run as root), " +
                $"and without DOTNET_EnableDiagnostics=0? {OneLine(e.Message)}", e);
        }
        catch (Exception e) when (e is not HangSlothException)
        {
            throw new HangSlothException($"Could not capture a dump of process {pid}: {OneLine(e.Message)}", e);
        }
    }

    private static bool Finished(Task task, TimeSpan wait)
    {
        try { return task.Wait(wait); }
        catch (AggregateException) { return true; }
    }

    private static long FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes >= 1 && t.Seconds == 0 ? $"{t.TotalMinutes:0} minute{(t.TotalMinutes == 1 ? "" : "s")}" : $"{t.TotalSeconds:0} seconds";

    /// createdump's messages come back as several lines; errors are one line.
    private static string OneLine(string message) =>
        string.Join(" ", message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static ClrRuntime CreateRuntime(ClrInfo clr, LoadOptions options, List<string> warnings)
    {
        if (options.DacPath is { } explicitDac)
        {
            if (!File.Exists(explicitDac)) throw new HangSlothException($"DAC not found: {explicitDac}");
            try
            {
                return clr.CreateRuntime(explicitDac, ignoreMismatch: true);
            }
            catch (Exception e)
            {
                throw new HangSlothException($"Could not load '{explicitDac}' as the debugging library (DAC): {e.Message}", e);
            }
        }

        Exception? first = null;
        try
        {
            return clr.CreateRuntime();
        }
        catch (Exception e)
        {
            first = e;
        }

        // Not on the symbol server (e.g. distro-built runtimes) or offline: use a matching local runtime's DAC.
        foreach (var candidate in LocalDacCandidates(clr))
        {
            try
            {
                var runtime = clr.CreateRuntime(candidate, ignoreMismatch: true);
                if (!SameDirectory(candidate, clr.ModuleInfo.FileName))
                    warnings.Add($"Using the debugging library (DAC) from {candidate}; results may be off if it doesn't exactly match the dumped runtime.");
                return runtime;
            }
            catch
            {
                // Try the next one.
            }
        }

        throw new HangSlothException(
            $"Could not load the debugging library (DAC) for .NET {RuntimeVersion(clr) ?? "?"}: {first?.Message} {DacAdvice(RuntimeVersion(clr))}", first);
    }

    /// Where a matching DAC comes from. Not the dumped machine's runtime folder: the DAC has to be built for
    /// the analyzing machine, so the one from a musl container or another architecture does not load here.
    internal static string DacAdvice(string? version) =>
        "Pass --dac <path to libmscordaccore.so or mscordaccore.dll> from that runtime version, built for this machine's OS, architecture and C library: " +
        $"it is in the runtime's shared/Microsoft.NETCore.App/{version ?? "<version>"} folder and in its Microsoft.NETCore.App.Runtime.<rid> package " +
        "(see \"Analyzing a dump from another machine\" in the README). HangSloth also finds it by itself when that runtime version is installed next to the one HangSloth runs on.";

    private static IEnumerable<string> LocalDacCandidates(ClrInfo clr)
    {
        var dacName = clr.DebuggingLibraries.FirstOrDefault(d => d.Kind == DebugLibraryKind.Dac)?.FileName
                      ?? (OperatingSystem.IsWindows() ? "mscordaccore.dll" : OperatingSystem.IsMacOS() ? "libmscordaccore.dylib" : "libmscordaccore.so");
        dacName = Path.GetFileName(dacName);

        // 1. Next to the runtime as it was loaded in the dumped process (same machine, or same layout).
        var runtimeDir = Path.GetDirectoryName(clr.ModuleInfo.FileName);
        if (!string.IsNullOrEmpty(runtimeDir)) yield return Path.Combine(runtimeDir, dacName);

        // 2. An installed shared runtime of the same version on this machine.
        var version = RuntimeVersion(clr);
        var hostRuntimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location); // .../Microsoft.NETCore.App/x.y.z
        var sharedRoot = hostRuntimeDir is null ? null : Path.GetDirectoryName(hostRuntimeDir);
        if (sharedRoot is not null && version is not null)
            yield return Path.Combine(sharedRoot, version, dacName);
    }

    private static bool SameDirectory(string file, string other) =>
        string.Equals(Path.GetDirectoryName(file), Path.GetDirectoryName(other), StringComparison.Ordinal);

    internal static string? RuntimeVersion(ClrInfo clr)
    {
        if (clr.Version is { Major: > 0 } v) return v.ToString(3);
        // Some builds (e.g. distro packages) carry no version resource; the install path has it.
        var m = VersionInPathRegex().Match(clr.ModuleInfo.FileName ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"Microsoft\.NETCore\.App[\\/](\d+\.\d+\.\d+[^\\/]*)[\\/]")]
    private static partial Regex VersionInPathRegex();

    /// A dump cut off by an interrupted copy or a full disk can still be opened, and one cut near its
    /// end even looks like an app with nothing to report (the thread stacks come last), so compare
    /// the file with what its own headers describe before reading it.
    private static void CheckComplete(string path)
    {
        long length, extent;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            length = file.Length;
            extent = DumpExtent(file) ?? 0;
        }
        catch
        {
            return; // Unreadable or nonsense headers: ClrMD reports the problem when it opens the file.
        }
        if (extent > length)
            throw new HangSlothException(
                $"'{path}' looks cut off: the file has {length.ToString("N0", CultureInfo.InvariantCulture)} bytes, but its own headers describe at least {extent.ToString("N0", CultureInfo.InvariantCulture)}. " +
                "Was a copy or download interrupted, or the disk full while it was written? Copy or capture the dump again.");
    }

    /// How long the dump says it is: the end of its furthest header or data block, for an ELF core
    /// (Linux), a minidump (Windows) or a Mach-O core (macOS); null for anything else.
    internal static long? DumpExtent(Stream s)
    {
        var head = new byte[64];
        var n = ReadAt(s, 0, head);
        if (n >= 4 && head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F') return ElfExtent(s, head, n);
        if (n >= 4 && BitConverter.ToUInt32(head, 0) == 0x504D444D) return MinidumpExtent(s, head, n);
        if (n >= 4 && BitConverter.ToUInt32(head, 0) == 0xFEEDFACF) return MachOExtent(s, head, n);
        return null;
    }

    private static long? ElfExtent(Stream s, byte[] head, int n)
    {
        if (n < 20) return 64;
        var is64 = head[4] == 2;
        if (head[4] is not (1 or 2) || head[5] != 1 || BitConverter.ToUInt16(head, 16) != 4) return null; // little-endian ET_CORE only
        var headerSize = is64 ? 64 : 52;
        if (n < headerSize) return headerSize;

        ulong phoff = is64 ? BitConverter.ToUInt64(head, 32) : BitConverter.ToUInt32(head, 28);
        ulong shoff = is64 ? BitConverter.ToUInt64(head, 40) : BitConverter.ToUInt32(head, 32);
        int entrySize = BitConverter.ToUInt16(head, is64 ? 54 : 42);
        ulong count = BitConverter.ToUInt16(head, is64 ? 56 : 44);
        if (entrySize < (is64 ? 56 : 32)) return null;
        if (count == 0xFFFF)
        {
            // PN_XNUM: more than 65,534 program headers; the real count is in section header 0.
            var section = new byte[is64 ? 64 : 40];
            if (shoff > long.MaxValue - (ulong)section.Length) return null;
            if (ReadAt(s, (long)shoff, section) < section.Length) return (long)shoff + section.Length;
            count = BitConverter.ToUInt32(section, is64 ? 44 : 28);
        }

        var tableEnd = phoff + count * (ulong)entrySize;
        if (tableEnd > long.MaxValue / 2) return null;
        if ((long)tableEnd > s.Length) return (long)tableEnd;

        var extent = (long)tableEnd;
        var entry = new byte[entrySize];
        s.Position = (long)phoff;
        for (ulong i = 0; i < count; i++)
        {
            s.ReadExactly(entry);
            ulong offset = is64 ? BitConverter.ToUInt64(entry, 8) : BitConverter.ToUInt32(entry, 4);
            ulong size = is64 ? BitConverter.ToUInt64(entry, 32) : BitConverter.ToUInt32(entry, 16);
            if (size == 0) continue;
            if (offset > long.MaxValue / 2 || size > long.MaxValue / 2) return null;
            extent = Math.Max(extent, (long)(offset + size));
        }
        return extent;
    }

    private static long? MinidumpExtent(Stream s, byte[] head, int n)
    {
        if (n < 32) return 32;
        long streams = BitConverter.ToUInt32(head, 8);
        long directory = BitConverter.ToUInt32(head, 12);
        // More streams than ClrMD accepts is not a minidump it could read anyway.
        if (streams > new DataTargetLimits().MaxMinidumpStreams) return null;
        var extent = directory + streams * 12;
        if (extent > s.Length) return extent;

        var entries = new byte[streams * 12];
        ReadAt(s, directory, entries);
        var entry = new byte[16];
        for (var i = 0; i < streams; i++)
        {
            var type = BitConverter.ToUInt32(entries, i * 12);
            long size = BitConverter.ToUInt32(entries, i * 12 + 4);
            long rva = BitConverter.ToUInt32(entries, i * 12 + 8);
            if (type == 0) continue; // UnusedStream
            extent = Math.Max(extent, rva + size);
            if (rva + size > s.Length) continue;

            if (type == 9 && ReadAt(s, rva, entry) == 16)
            {
                // Memory64ListStream: the memory of every range follows BaseRva, one after the other.
                var ranges = BitConverter.ToUInt64(entry, 0);
                var data = BitConverter.ToUInt64(entry, 8);
                var listEnd = rva + 16 + (long)Math.Min(ranges, (ulong)long.MaxValue / 32) * 16;
                if (listEnd > s.Length) return Math.Max(extent, listEnd);
                s.Position = rva + 16;
                for (ulong r = 0; r < ranges; r++)
                {
                    s.ReadExactly(entry);
                    data += BitConverter.ToUInt64(entry, 8);
                    if (data > long.MaxValue / 2) return null;
                }
                extent = Math.Max(extent, (long)data);
            }
            else if (type == 5 && ReadAt(s, rva, entry.AsSpan(0, 4)) == 4)
            {
                // MemoryListStream: each range says where its memory is.
                long ranges = BitConverter.ToUInt32(entry, 0);
                var listEnd = rva + 4 + ranges * 16;
                if (listEnd > s.Length) return Math.Max(extent, listEnd);
                s.Position = rva + 4;
                for (long r = 0; r < ranges; r++)
                {
                    s.ReadExactly(entry);
                    extent = Math.Max(extent, (long)BitConverter.ToUInt32(entry, 12) + BitConverter.ToUInt32(entry, 8));
                }
            }
        }
        return extent;
    }

    private static long? MachOExtent(Stream s, byte[] head, int n)
    {
        if (n < 32) return 32;
        if (BitConverter.ToUInt32(head, 12) != 4) return null; // MH_CORE
        long commands = BitConverter.ToUInt32(head, 16);
        long size = BitConverter.ToUInt32(head, 20);
        var extent = 32 + size;
        if (extent > s.Length) return extent;

        // One command at a time: the commands of a dump with many threads take hundreds of MB.
        var cmd = new byte[56];
        var at = 0L;
        for (var i = 0; i < commands && at + 8 <= size; i++)
        {
            ReadAt(s, 32 + at, cmd);
            long cmdSize = BitConverter.ToUInt32(cmd, 4);
            if (cmdSize < 8) return null;
            if (BitConverter.ToUInt32(cmd, 0) == 0x19 && at + 56 <= size) // LC_SEGMENT_64
            {
                var fileOffset = BitConverter.ToUInt64(cmd, 40);
                var fileSize = BitConverter.ToUInt64(cmd, 48);
                if (fileOffset > long.MaxValue / 2 || fileSize > long.MaxValue / 2) return null;
                if (fileSize > 0) extent = Math.Max(extent, (long)(fileOffset + fileSize));
            }
            at += cmdSize;
        }
        return extent;
    }

    /// Reads as much of <paramref name="buffer"/> as the stream has at <paramref name="offset"/>.
    private static int ReadAt(Stream s, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= s.Length) return 0;
        s.Position = offset;
        return s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    }
}

/// A dump of a live process on disk. A temporary one lives in a folder of its own and is deleted on
/// Dispose, and also when HangSloth is ended by Ctrl+C, SIGTERM, SIGHUP or SIGQUIT while it exists,
/// because the `finally` that normally deletes it does not run then. Keep() leaves it in place.
public sealed class CapturedDump : IDisposable
{
    private readonly string? _folder;
    private readonly List<PosixSignalRegistration> _signals = [];
    private readonly object _gate = new();
    private int _state; // 0 = in use, 1 = kept, 2 = deleted

    /// The dump file.
    public string Path { get; }

    /// False for a dump the user asked to keep (--keep-dump).
    public bool IsTemporary { get; }

    internal CapturedDump(string path, bool temporary, string? folder)
    {
        Path = path;
        IsTemporary = temporary;
        _folder = folder;
        if (!temporary) return;
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
        {
            // The handler does not cancel the signal, so HangSloth still ends as it did before.
            try { _signals.Add(PosixSignalRegistration.Create(signal, _ => Delete())); }
            catch (Exception e) when (e is PlatformNotSupportedException or IOException) { /* best effort */ }
        }
    }

    /// A unique path per run. When the target runs as HangSloth's user the dump goes in a fresh folder
    /// only that user can open, which also stops a capture that was given up on from writing the file later.
    internal static CapturedDump CreateTemporary(int pid, bool privateFolder)
    {
        if (privateFolder)
        {
            var folder = System.IO.Path.GetFullPath(Directory.CreateTempSubdirectory("hangsloth-").FullName);
            return new CapturedDump(System.IO.Path.Combine(folder, $"{pid}.dmp"), temporary: true, folder);
        }
        return new CapturedDump(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hangsloth-{pid}-{Guid.NewGuid():N}.dmp"), temporary: true, folder: null);
    }

    /// Keeps a temporary dump (the analysis failed, so it is needed again) and says where it is.
    public string KeepAndDescribe()
    {
        lock (_gate)
        {
            if (!IsTemporary || _state == 2 || !File.Exists(Path)) return "";
            _state = 1;
        }
        var quoted = Path.Contains(' ') ? $"\"{Path}\"" : Path;
        return $" The dump was kept at {Path}; analyze it again with: hangsloth {quoted}";
    }

    public void Dispose()
    {
        foreach (var signal in _signals) signal.Dispose();
        _signals.Clear();
        Delete();
    }

    /// Under a lock, so that the main thread does not end the process while a signal handler is still deleting.
    private void Delete()
    {
        lock (_gate)
        {
            if (!IsTemporary || _state != 0) return;
            _state = 2;
            try { File.Delete(Path); } catch { /* best effort */ }
            if (_folder is not null)
                try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
        }
    }
}
