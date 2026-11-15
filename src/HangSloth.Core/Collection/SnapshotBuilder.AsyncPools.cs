using HangSloth.Analysis;
using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

// Async code waiting for a database connection doesn't block a thread, so no stack shows it. Find full
// pools with queued OpenAsync calls on the heap, and the app's paused async methods that use them.
public sealed partial class SnapshotBuilder
{
    private const string NpgsqlPool = "Npgsql.PoolingDataSource";

    private List<AsyncPoolWait> ReadAsyncPoolWaits()
    {
        var result = new List<AsyncPoolWait>();
        try
        {
            if (!_heap.CanWalkHeap) return result;
            // Walking the heap is the expensive part; only do it when the driver is loaded.
            if (!_runtime.EnumerateModules().Any(m => Path.GetFileName(m.Name ?? "").StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))) return result;
            var full = new List<(ClrObject Pool, ConnectionPoolInfo Info, int Queued)>();
            foreach (var obj in _heap.EnumerateObjects())
            {
                if (obj.Type?.Name != NpgsqlPool) continue;
                var max = obj.ReadInt("<MaxConnections>k__BackingField");
                var open = obj.ReadInt("_numConnectors");
                var idle = obj.ReadInt("_idleCount");
                if (max is not { } m || open is not { } o || idle is not { } i || o < m || i > 0) continue;
                var queued = QueuedReaders(obj);
                if (queued > 0) full.Add((obj, new ConnectionPoolInfo(m, o, i), queued));
            }
            if (full.Count == 0) return result;

            var users = full.ToDictionary(f => f.Pool.Address, _ => new List<AsyncConnectionUser>());
            foreach (var box in _heap.EnumerateObjects())
            {
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
                    var mine = connections.Where(c => c.TryReadObjectField("_dataSource", out var ds) && ds.Address == pool.Pool.Address).ToList();
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
        return result;
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
