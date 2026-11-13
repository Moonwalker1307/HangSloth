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
    /// Load a dump file (Windows minidump, Linux core dump or macOS dump) and take a snapshot of it.
    public static ProcessSnapshot LoadDump(string path, LoadOptions? options = null)
    {
        options ??= new LoadOptions();
        if (!File.Exists(path)) throw new HangSlothException($"Dump file not found: {path}");

        DataTarget target;
        try
        {
            target = DataTarget.LoadDump(path);
        }
        catch (Exception e)
        {
            throw new HangSlothException($"Could not open '{path}' as a memory dump: {e.Message}", e);
        }

        using (target)
        {
            var clr = target.ClrVersions.FirstOrDefault()
                      ?? throw new HangSlothException("No .NET runtime found in this dump. Is it a .NET (Core) process?");
            var warnings = new List<string>();
            using var runtime = CreateRuntime(clr, options, warnings);
            using var sources = new SourceResolver(options.SymbolDirectories.Append(Path.GetDirectoryName(Path.GetFullPath(path))!));
            var snapshot = SnapshotBuilder.Build(runtime, path, sources, RuntimeVersion(clr));
            return snapshot with { Warnings = [.. warnings, .. snapshot.Warnings] };
        }
    }

    /// Capture a dump of a live .NET process (via the diagnostics IPC channel, like dotnet-dump) and analyze it.
    /// The target process is paused only while the dump is written.
    public static ProcessSnapshot LoadProcess(int pid, LoadOptions? options = null, string? keepDumpAt = null)
    {
        var dumpPath = keepDumpAt ?? Path.Combine(Path.GetTempPath(), $"hangsloth-{pid}-{DateTime.UtcNow:yyyyMMddHHmmss}.dmp");
        try
        {
            CollectDump(pid, dumpPath);
            var options2 = options ?? new LoadOptions();
            // Live process: the app's binaries are where the process loaded them from, so PDBs are found next to them.
            return LoadDump(dumpPath, options2) with { Source = $"process {pid}" + (keepDumpAt is null ? "" : $" (dump saved to {keepDumpAt})") };
        }
        finally
        {
            if (keepDumpAt is null)
            {
                try { File.Delete(dumpPath); } catch { /* best effort */ }
            }
        }
    }

    public static void CollectDump(int pid, string dumpPath)
    {
        try
        {
            var client = new DiagnosticsClient(pid);
            client.WriteDump(DumpType.WithHeap, Path.GetFullPath(dumpPath), logDumpGeneration: false);
        }
        catch (ServerNotAvailableException e)
        {
            throw new HangSlothException($"Could not connect to process {pid}. Is it a .NET 5+ process running as the same user (and not with DOTNET_EnableDiagnostics=0)? {e.Message}", e);
        }
        catch (Exception e) when (e is not HangSlothException)
        {
            throw new HangSlothException($"Could not capture a dump of process {pid}: {e.Message}", e);
        }
    }

    private static ClrRuntime CreateRuntime(ClrInfo clr, LoadOptions options, List<string> warnings)
    {
        if (options.DacPath is { } explicitDac)
        {
            if (!File.Exists(explicitDac)) throw new HangSlothException($"DAC not found: {explicitDac}");
            return clr.CreateRuntime(explicitDac, ignoreMismatch: true);
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
            $"Could not load the debugging library (DAC) for .NET {RuntimeVersion(clr) ?? "?"}: {first?.Message} " +
            "Pass --dac <path to libmscordaccore.so or mscordaccore.dll> from the same runtime version.", first);
    }

    private static IEnumerable<string> LocalDacCandidates(ClrInfo clr)
    {
        var dacName = clr.DebuggingLibraries.FirstOrDefault(d => d.Kind == DebugLibraryKind.Dac).FileName
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
}
