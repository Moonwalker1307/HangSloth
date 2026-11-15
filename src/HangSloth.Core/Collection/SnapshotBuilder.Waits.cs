using HangSloth.Analysis;
using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

// What else a thread can be waiting for besides a monitor or a task: a ReaderWriterLockSlim, a
// SemaphoreSlim, another thread (Join), or a database. Plus naming the objects involved.
public sealed partial class SnapshotBuilder
{
    private Dictionary<ulong, ulong>? _semaphoreOfTaskNode;

    // ---------------------------------------------------------------- what a thread is running

    private static readonly HashSet<string> TaskExecuteMethods =
        ["InnerInvoke", "ExecuteWithThreadLocal", "ExecuteEntry", "ExecuteEntryUnsafe", "ExecuteFromThreadPool", "ExecuteEntryCancellationRequestedOrCanceled"];

    /// The task whose delegate this thread is running: the Task.Run body further down its stack.
    private static ulong? FindRunningTask(List<ClrStackRoot> roots)
    {
        foreach (var root in roots)
        {
            var method = root.StackFrame?.Method;
            if (method?.Type?.Name?.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal) != true
                || !TaskExecuteMethods.Contains(method.Name ?? "")) continue;
            if (!root.Object.IsNull && IsTask(root.Object.Type)) return root.Object.Address;
        }
        return null;
    }

    private static int? FindJoinTarget(List<ClrStackRoot> roots, int self)
    {
        foreach (var root in roots)
        {
            if (root.Object.Type?.Name != "System.Threading.Thread") continue;
            if (root.StackFrame?.Method is not { Type.Name: "System.Threading.Thread" } m || !(m.Name ?? "").StartsWith("Join", StringComparison.Ordinal)) continue;
            if (root.Object.TryReadField<int>("_managedThreadId", out var id) && id != self && id != 0) return id;
        }
        return null;
    }

    // ---------------------------------------------------------------- ReaderWriterLockSlim and SemaphoreSlim

    private const uint RwMaxReader = 0x0FFFFFFE;

    /// A ReaderWriterLockSlim records the managed id of its writer (and upgradeable reader) itself,
    /// so it joins the lock graph like a monitor. Plain readers aren't recorded per thread, only counted.
    private static ulong? FindReaderWriterLock(List<ClrStackRoot> roots, Dictionary<ulong, MonitorLock> locks)
    {
        foreach (var root in roots)
        {
            var rw = root.Object;
            if (rw.Type?.Name != "System.Threading.ReaderWriterLockSlim") continue;
            var writer = rw.ReadInt("_writeLockOwnerId") ?? -1;
            var upgrader = rw.ReadInt("_upgradeLockOwnerId") ?? -1;
            var owners = rw.TryReadField<uint>("_owners", out var o) ? o : 0;
            locks[rw.Address] = new MonitorLock
            {
                Object = new ObjectRef(rw.Address, "System.Threading.ReaderWriterLockSlim"),
                Kind = LockKind.ReaderWriter,
                OwnerThreadId = writer > 0 ? writer : upgrader > 0 ? upgrader : null,
                ReaderCount = (int)(owners & RwMaxReader),
            };
            return rw.Address;
        }
        return null;
    }

    private static ulong? FindSemaphoreBeingWaited(List<ClrStackRoot> roots)
    {
        foreach (var root in roots)
        {
            if (root.Object.Type?.Name == "System.Threading.SemaphoreSlim"
                && root.StackFrame?.Method?.Type?.Name == "System.Threading.SemaphoreSlim")
                return root.Object.Address;
        }
        return null;
    }

    /// WaitAsync queues a TaskNode on the semaphore; the node doesn't point back, so map nodes to semaphores once.
    private ulong? FindSemaphoreOfTaskNode(ClrObject node)
    {
        if (_semaphoreOfTaskNode is null)
        {
            _semaphoreOfTaskNode = [];
            try
            {
                if (_heap.CanWalkHeap)
                {
                    foreach (var obj in _heap.EnumerateObjects())
                    {
                        if (obj.Type?.Name != "System.Threading.SemaphoreSlim") continue;
                        foreach (var n in AsyncWaiterNodes(obj))
                            _semaphoreOfTaskNode[n] = obj.Address;
                    }
                }
            }
            catch
            {
                // Without the mapping the wait is still described, just without the semaphore's name.
            }
        }
        return _semaphoreOfTaskNode.TryGetValue(node.Address, out var s) ? s : null;
    }

    private static IEnumerable<ulong> AsyncWaiterNodes(ClrObject semaphore)
    {
        if (!semaphore.TryReadObjectField("m_asyncHead", out var node)) yield break;
        for (var guard = 0; !node.IsNull && guard < 10_000; guard++)
        {
            yield return node.Address;
            if (!node.TryReadObjectField("Next", out node)) yield break;
        }
    }

    private SemaphoreInfo? ReadSemaphore(ulong address, NamedObject? name, List<ThreadSnapshot> threads)
    {
        var obj = _heap.GetObject(address);
        if (!obj.IsValid || obj.Type?.Name != "System.Threading.SemaphoreSlim") return null;
        return new SemaphoreInfo
        {
            Object = new ObjectRef(address, "System.Threading.SemaphoreSlim"),
            FieldName = name?.Name,
            ContainerAddress = name?.Container,
            CurrentCount = obj.ReadInt("m_currentCount") ?? 0,
            MaxCount = obj.ReadInt("m_maxCount") ?? int.MaxValue,
            WaiterThreadIds = threads.Where(t => t.WaitingForSemaphore == address).Select(t => t.ManagedId).ToList(),
            AsyncWaiters = AsyncWaiterNodes(obj).Count(),
        };
    }

    // ---------------------------------------------------------------- databases

    private const int ConnectionStateOpen = 1;
    private const byte PgTransactionInBlock = (byte)'T';
    private const byte PgTransactionFailed = (byte)'E';

    /// The database connections and commands on a thread's stack: which command it waits on, which
    /// server session that runs on, and which of its connections are inside a transaction.
    private DatabaseActivity? ReadDatabaseActivity(List<ClrStackRoot> roots, BlockingKind kind)
    {
        var commands = new List<ClrObject>();
        var connections = new List<ClrObject>();
        var seen = new HashSet<ulong>();
        foreach (var root in roots)
        {
            var obj = root.Object;
            if (obj.IsNull || !seen.Add(obj.Address)) continue;
            if (DerivesFrom(obj.Type, "System.Data.Common.DbCommand")) commands.Add(obj);
            else if (DerivesFrom(obj.Type, "System.Data.Common.DbConnection")) connections.Add(obj);
        }

        var open = connections.Select(ReadConnection).OfType<DatabaseConnection>().ToList();
        var waiting = kind switch
        {
            BlockingKind.Database => DatabaseWaitKind.Query,
            BlockingKind.ConnectionPool => DatabaseWaitKind.ConnectionPool,
            _ => DatabaseWaitKind.None,
        };
        if (waiting == DatabaseWaitKind.None && open.Count == 0) return null;

        var command = waiting == DatabaseWaitKind.Query ? commands.FirstOrDefault() : default;
        var typeName = (commands.Concat(connections).FirstOrDefault().Type?.Name) ?? "";
        return new DatabaseActivity
        {
            Provider = FrameClassifier.DatabaseProvider(typeName) ?? "the database",
            Waiting = waiting,
            CommandText = command.IsNull ? null : DatabaseCommandText(command),
            SessionId = command.IsNull ? null : CommandSession(command),
            Connections = open,
            Pool = waiting == DatabaseWaitKind.ConnectionPool ? connections.Select(ReadPool).FirstOrDefault(p => p is not null) : null,
        };
    }

    private static DatabaseConnection? ReadConnection(ClrObject connection)
    {
        // Npgsql: NpgsqlConnection._fullState (ConnectionState flags) and its connector's session state.
        if (!connection.TryReadField<int>("_fullState", out var state) || (state & ConnectionStateOpen) == 0) return null;
        if (!connection.TryReadObjectField("<Connector>k__BackingField", out var connector) || connector.IsNull) return null;
        return new DatabaseConnection(BackendPid(connector), InTransaction(connector));
    }

    private static int? BackendPid(ClrObject connector) =>
        connector.TryReadField<int>("<BackendProcessId>k__BackingField", out var pid) && pid != 0 ? pid : null;

    private static bool InTransaction(ClrObject connector) =>
        connector.TryReadField<byte>("<TransactionStatus>k__BackingField", out var status)
        && status is PgTransactionInBlock or PgTransactionFailed;

    private static int? CommandSession(ClrObject command)
    {
        if (command.TryReadObjectField("<InternalConnection>k__BackingField", out var connection) && !connection.IsNull
            && connection.TryReadObjectField("<Connector>k__BackingField", out var connector) && !connector.IsNull)
            return BackendPid(connector);
        return null;
    }

    private static ConnectionPoolInfo? ReadPool(ClrObject connection)
    {
        if (!connection.TryReadObjectField("_dataSource", out var pool) || pool.IsNull) return null;
        if (!pool.TryReadField<int>("<MaxConnections>k__BackingField", out var max)) return null;
        return new ConnectionPoolInfo(max, pool.ReadInt("_numConnectors") ?? 0, pool.ReadInt("_idleCount") ?? 0);
    }

    private static string? DatabaseCommandText(ClrObject obj)
    {
        if (!DerivesFrom(obj.Type, "System.Data.Common.DbCommand")) return null;
        foreach (var field in new[] { "_commandText", "commandText", "<CommandText>k__BackingField" })
            if (obj.TryReadStringField(field, null, out var text) && !string.IsNullOrWhiteSpace(text))
                return text;
        return null;
    }

    private static TaskInfo? DatabaseCommandInChain(TaskInfo task)
    {
        for (var t = task; t is not null; t = t.Awaiting)
            if (t.CommandText is not null) return t;
        return null;
    }

    /// A thread blocked (.Result, .GetAwaiter().GetResult()...) on an async chain that ends in a database command.
    private DatabaseActivity AsyncDatabaseWait(TaskInfo command, DatabaseActivity? onStack)
    {
        var obj = command.ThisAddress is { } a ? _heap.GetObject(a) : default;
        return new DatabaseActivity
        {
            Provider = FrameClassifier.DatabaseProvider(obj.Type?.Name ?? "") ?? onStack?.Provider ?? "the database",
            Waiting = DatabaseWaitKind.Query,
            CommandText = command.CommandText,
            SessionId = obj.IsValid ? CommandSession(obj) : null,
            Connections = onStack?.Connections ?? [],
        };
    }

    private static bool DerivesFrom(ClrType? type, string baseName)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.Name == baseName) return true;
        return false;
    }

    // ---------------------------------------------------------------- naming

    private sealed record NamedObject(string Name, ulong? Container);

    private static readonly string[] IdentityFields = ["Id", "Name", "Key", "Code", "Number"];

    /// "lock on object" says nothing; "lock on OrderCache._sync" does. Look for the static field, or the
    /// field of an object on an involved thread's stack (or the `this` of an async method in a chain),
    /// that references each object.
    private Dictionary<ulong, NamedObject> NameObjects(HashSet<ulong> targets, IEnumerable<ClrStackRoot> roots)
    {
        var names = new Dictionary<ulong, NamedObject>();
        if (targets.Count == 0) return names;
        var rootObjects = roots.Select(r => r.Object).Where(o => !o.IsNull).ToList();

        // A Lazy<T> locks a private LazyHelper; name it after the field that holds the Lazy<T>.
        var lazyOf = new Dictionary<ulong, ClrObject>();
        foreach (var obj in rootObjects)
        {
            if (obj.Type?.Name?.StartsWith("System.Lazy<", StringComparison.Ordinal) == true
                && obj.TryReadObjectField("_state", out var helper) && !helper.IsNull && targets.Contains(helper.Address))
                lazyOf[helper.Address] = obj;
        }
        var search = targets.Concat(lazyOf.Values.Select(l => l.Address)).ToHashSet();
        var containers = new Dictionary<ulong, ClrObject>();

        try
        {
            var domain = _runtime.AppDomains.FirstOrDefault();
            if (domain is not null)
            {
                foreach (var module in _runtime.EnumerateModules())
                {
                    if (module.IsDynamic || FrameClassifier.IsFrameworkModule(module.Name)) continue;
                    foreach (var type in module.EnumerateTypesWithStaticFields())
                    {
                        foreach (var field in type.StaticFields)
                        {
                            if (!field.IsObjectReference || !field.IsInitialized(domain)) continue;
                            var value = field.ReadObject(domain);
                            if (!value.IsNull && search.Contains(value.Address) && !names.ContainsKey(value.Address))
                                names[value.Address] = new NamedObject($"{Names.ShortType(type.Name ?? "")}.{field.Name}", null);
                        }
                    }
                }
            }

            foreach (var obj in rootObjects.Concat(_asyncThisObjects))
            {
                if (obj.Type is null || search.Contains(obj.Address) || FrameClassifier.IsFrameworkType(obj.Type.Name ?? "")) continue;
                foreach (var field in obj.Type.Fields)
                {
                    if (!field.IsObjectReference) continue;
                    var value = obj.ReadObjectField(field);
                    if (value.IsNull || !search.Contains(value.Address) || names.ContainsKey(value.Address)) continue;
                    names[value.Address] = new NamedObject($"{Names.ShortType(obj.Type.Name ?? "")}.{CleanFieldName(field.Name)}", obj.Address);
                    containers[obj.Address] = obj;
                }
            }
        }
        catch
        {
            // Names are a nicety.
        }

        foreach (var (helper, lazy) in lazyOf)
        {
            var lazyType = Names.ShortType(lazy.Type?.Name ?? "Lazy<T>");
            names[helper] = names.TryGetValue(lazy.Address, out var n)
                ? new NamedObject($"{n.Name} ({lazyType})", n.Container)
                : new NamedObject(lazyType, null);
        }

        // Two Account.Sync locks in one finding read as one lock; say which account each belongs to.
        foreach (var group in names.Where(kv => targets.Contains(kv.Key)).GroupBy(kv => kv.Value.Name).Where(g => g.Count() > 1).ToList())
        {
            foreach (var (address, named) in group)
            {
                if (named.Container is not { } c || !containers.TryGetValue(c, out var owner) || Identity(owner) is not { } id) continue;
                names[address] = named with { Name = $"{named.Name} ({id})" };
            }
        }
        return names;
    }

    private static string CleanFieldName(string? name) =>
        name is not null && name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal) ? name[1..name.IndexOf('>')] : name ?? "?";

    /// A short "Id = "alice"" for an object, from a field or auto-property that looks like its identity.
    private static string? Identity(ClrObject obj)
    {
        foreach (var field in obj.Type?.Fields ?? [])
        {
            var name = CleanFieldName(field.Name).TrimStart('_');
            if (!IdentityFields.Any(i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase))) continue;
            var label = char.ToUpperInvariant(name[0]) + name[1..];
            try
            {
                if (field.ElementType == ClrElementType.String && obj.TryReadStringField(field.Name!, 64, out var s) && s is not null)
                    return $"{label} = \"{s}\"";
                if (field.ElementType == ClrElementType.Int32)
                    return $"{label} = {obj.ReadField<int>(field.Name!)}";
                if (field.ElementType == ClrElementType.Int64)
                    return $"{label} = {obj.ReadField<long>(field.Name!)}";
            }
            catch
            {
                // Try the next candidate.
            }
        }
        return null;
    }
}
