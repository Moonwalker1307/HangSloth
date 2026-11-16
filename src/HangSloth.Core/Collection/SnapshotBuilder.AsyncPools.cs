using System.Collections.Immutable;
using System.Reflection.Metadata;
using HangSloth.Analysis;
using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

// Async code waiting for a database connection doesn't block a thread, so no stack shows it. Find full
// pools with queued OpenAsync calls on the heap, and the app's paused async methods that use them.
public sealed partial class SnapshotBuilder
{
    // Npgsql's private names, newest first: the pool type was ConnectorPool before 7.0, its size _max
    // before 8.0, a connection's pool _pool and a command's connection _connection before 7.0.
    internal static readonly string[] NpgsqlPoolTypes = ["Npgsql.PoolingDataSource", "Npgsql.ConnectorPool"];
    internal static readonly string[] NpgsqlPoolMax = ["<MaxConnections>k__BackingField", "_max"];
    internal static readonly string[] NpgsqlConnectionPool = ["_dataSource", "_pool"];
    internal static readonly string[] NpgsqlCommandConnection = ["<InternalConnection>k__BackingField", "_connection"];

    /// What could not be read, per driver module, for the one snapshot warning about it.
    private readonly Dictionary<ulong, (ClrModule? Module, string Fallback, SortedSet<string> Parts)> _unrecognised = [];

    private List<AsyncPoolWait> ReadAsyncPoolWaits()
    {
        var result = new List<AsyncPoolWait>();
        try
        {
            if (!_heap.CanWalkHeap) return result;
            // Walking the heap is the expensive part; only do it when the driver is loaded.
            if (!NpgsqlLoaded) return result;
            var full = new List<(ClrObject Pool, ConnectionPoolInfo Info, int Queued)>();
            if (HeapLookups.NpgsqlConnectionType is { } connectionType && !NpgsqlConnectionPool.Any(n => connectionType.GetFieldByName(n) is not null))
                Unrecognised(connectionType.Module, "Npgsql", "connection internals");
            foreach (var obj in HeapLookups.NpgsqlPools)
            {
                var max = obj.ReadInt(NpgsqlPoolMax);
                var open = obj.ReadInt("_numConnectors");
                var idle = obj.ReadInt("_idleCount");
                if (max is null || open is null || idle is null) Unrecognised(obj.Type?.Module, "Npgsql", "connection pool internals");
                if (max is not { } m || open is not { } o || idle is not { } i || o < m || i > 0) continue;
                var queued = QueuedReaders(obj);
                if (queued > 0) full.Add((obj, new ConnectionPoolInfo(m, o, i), queued));
            }
            if (full.Count == 0) return result;

            var users = full.ToDictionary(f => f.Pool.Address, _ => new List<AsyncConnectionUser>());
            foreach (var address in HeapLookups.PendingAsyncBoxes)
            {
                var box = _heap.GetObject(address);
                var typeName = box.Type?.Name;
                if (typeName is null || !typeName.Contains("AsyncStateMachineBox<", StringComparison.Ordinal)) continue;
                if (ReadTaskState(box) != TaskState.Pending || !TryReadStateMachine(box, out var sm) || sm.Type is not { } smType) continue;
                if (smType.Name is not { } smName || IsLibraryType(smName)) continue;

                var connections = smType.Fields
                    .Where(f => f.IsObjectReference)
                    .Select(f => sm.ReadObject(f))
                    .Where(o => !o.IsNull && DerivesFrom(o.Type, "System.Data.Common.DbConnection"))
                    .ToList();
                var task = DescribeTask(box, 0, []);
                var chain = Describe.Chain(task).ToList();
                var openAt = chain.FindIndex(IsOpenAsync);
                var waitingOpen = openAt >= 0;
                var opener = waitingOpen ? chain.Take(openAt).LastOrDefault(t => Describe.IsAppAsyncMethod(t) && !t.Description.StartsWith("Npgsql", StringComparison.Ordinal) && !t.TypeName.Contains("<Npgsql.", StringComparison.Ordinal)) : null;
                var openingIn = opener is null ? null : opener.AwaitLocation is { } at ? $"{opener.Description} ({at})" : opener.Description;
                if (connections.Count == 0 && !waitingOpen) continue;

                foreach (var pool in full)
                {
                    var mine = connections.Where(c => c.TryReadObject(NpgsqlConnectionPool, out var ds) && ds.Address == pool.Pool.Address).ToList();
                    if (mine.Count == 0) continue;
                    var holds = mine.Any(c => ReadConnection(c) is not null);
                    users[pool.Pool.Address].Add(new AsyncConnectionUser(task.Description, task.AwaitLocation, holds, waitingOpen, openingIn));
                }
            }

            foreach (var pool in full)
                result.Add(new AsyncPoolWait { Provider = "PostgreSQL", Pool = pool.Info, QueuedOpens = pool.Queued, Methods = users[pool.Pool.Address] });
        }
        catch
        {
            // A nicety: without it, the waits are just not explained.
        }
        finally
        {
            // Last of the reads that note unrecognised driver internals (the stacks' database reads come first).
            // Build must call this before it copies _warnings into the snapshot, or the notes are lost.
            AddUnrecognisedWarnings();
        }
        return result;
    }

    /// Driver internals that are there but not under any name this version knows: say so once per
    /// driver, so a missed pool exhaustion isn't silent.
    private void Unrecognised(ClrModule? module, string driver, string part)
    {
        var key = module?.Address ?? 0;
        if (!_unrecognised.TryGetValue(key, out var entry)) _unrecognised[key] = entry = (module, driver, new SortedSet<string>(StringComparer.Ordinal));
        entry.Parts.Add(part);
    }

    private void AddUnrecognisedWarnings()
    {
        foreach (var (module, fallback, parts) in _unrecognised.Values)
            _warnings.Add(UnrecognisedWarning(DriverName(module) ?? fallback, parts));
        _unrecognised.Clear();
    }

    /// SqlClient's pool is read only for a thread that waits for a connection, which is reported either way: what is
    /// missing then is the proof that the pool is full.
    internal static string UnrecognisedWarning(string driver, IEnumerable<string> parts) =>
        driver.StartsWith("Microsoft.Data.SqlClient", StringComparison.Ordinal)
            ? $"{driver}: {string.Join(" and ", parts)} not recognised, so the pool's size and use are unknown: threads waiting for a connection are still reported, but not whether the pool is full."
            : $"{driver}: {string.Join(" and ", parts)} not recognised, so pool exhaustion cannot be detected.";

    /// "Npgsql 6.0.13", from the assembly's metadata in the dump: its informational version (without the
    /// commit hash), which is the package version, else its assembly version.
    private string? DriverName(ClrModule? module)
    {
        try
        {
            if (module is null || module.MetadataAddress == 0 || module.MetadataLength is <= 0 or > 256 * 1024 * 1024) return null;
            var bytes = new byte[module.MetadataLength];
            if (_runtime.DataTarget.DataReader.Read(module.MetadataAddress, bytes) != bytes.Length) return null;
            using var provider = MetadataReaderProvider.FromMetadataImage(ImmutableArray.Create(bytes));
            var md = provider.GetMetadataReader();
            if (!md.IsAssembly) return null;
            var assembly = md.GetAssemblyDefinition();
            var name = md.GetString(assembly.Name);
            string? version = null;
            foreach (var handle in assembly.GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var ctor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (ctor.Parent.Kind != HandleKind.TypeReference || md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != "AssemblyInformationalVersionAttribute") continue;
                var blob = md.GetBlobReader(attribute.Value);
                if (blob.ReadUInt16() == 1) version = blob.ReadSerializedString()?.Split('+')[0];
            }
            if (string.IsNullOrWhiteSpace(version)) version = assembly.Version.ToString(3);
            return $"{name} {version}";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLibraryType(string name) =>
        FrameClassifier.IsFrameworkType(name) || name.StartsWith("Npgsql.", StringComparison.Ordinal);

    /// NpgsqlConnection.OpenAsync and the pool's rent methods, as they appear in a task chain.
    private static bool IsOpenAsync(TaskInfo t) =>
        t.TypeName.Contains("Npgsql.", StringComparison.Ordinal) && t.TypeName.Contains("Open", StringComparison.Ordinal);

    /// Npgsql queues callers waiting for an idle connector as blocked readers on an unbounded channel.
    private static int QueuedReaders(ClrObject pool)
    {
        if (!pool.TryReadObjectField("_idleConnectorReader", out var reader) || reader.IsNull) return 0;
        if (!reader.TryReadObjectField("_parent", out var channel) || channel.IsNull) return 0;
        if (!channel.TryReadObjectField("_blockedReadersHead", out var node) || node.IsNull) return 0;
        var seen = new HashSet<ulong>();
        while (!node.IsNull && seen.Add(node.Address) && seen.Count < 10_000)
        {
            if (!node.TryReadObjectField("_next", out var next) && !node.TryReadObjectField("Next", out next)) break;
            node = next;
        }
        return seen.Count;
    }
}

/// Drivers rename their private fields between versions. A read takes every spelling known to work,
/// newest first, so the next rename is one more name in a list. Never throws: a field that can't be
/// read counts as missing.
internal static class AlternativeNames
{
    public delegate bool TryRead<T>(string name, out T value);

    /// The first of the names that reads, with its value; null (and the default value) when none does.
    public static string? First<T>(IReadOnlyList<string> names, TryRead<T> read, out T value)
    {
        foreach (var name in names)
        {
            try
            {
                if (read(name, out value)) return name;
            }
            catch
            {
                // Try the next spelling.
            }
        }
        value = default!;
        return null;
    }

    public static int? ReadInt(this ClrObject obj, IReadOnlyList<string> names) =>
        First<int>(names, obj.TryReadField, out var value) is not null ? value : null;

    /// The object in the first of the fields that exists; false when none exists or it holds null.
    public static bool TryReadObject(this ClrObject obj, IReadOnlyList<string> names, out ClrObject value) =>
        First(names, obj.TryReadObjectField, out value) is not null && !value.IsNull;
}
