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

    /// A SynchronizationContext.Send override on the stack, and the context it was called on. Send runs the
    /// callback on the context's thread and waits for it, like Control.Invoke and Dispatcher.Invoke.
    private static ObjectRef? FindSendTarget(List<ClrStackRoot> roots)
    {
        foreach (var root in roots)
        {
            if (root.StackFrame?.Method is not { Name: "Send" } m || m.Type?.Name == SyncContextType || !DerivesFrom(m.Type, SyncContextType)) continue;
            if (!root.Object.IsNull && DerivesFrom(root.Object.Type, SyncContextType))
                return new ObjectRef(root.Object.Address, root.Object.Type?.Name ?? SyncContextType);
        }
        return null;
    }

    private const string SyncContextType = "System.Threading.SynchronizationContext";

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

    /// ReaderWriterLockSlim._owners: the low 28 bits count the readers (the upgradeable reader included),
    /// the top bits are flags (write lock held, writers waiting, upgrader waiting). Unchanged in .NET 8, 9 and 10.
    private const uint RwReaderMask = 0x0FFFFFFF;

    internal static int RwReaderCount(uint owners) => (int)(owners & RwReaderMask);

    /// A ReaderWriterLockSlim records the managed id of its writer (and upgradeable reader) itself,
    /// so it joins the lock graph like a monitor. Plain readers are only counted on the lock; each
    /// thread keeps its own counts per lock, which is where the dump shows who they are.
    private static ulong? FindReaderWriterLock(List<ClrStackRoot> roots, Dictionary<ulong, MonitorLock> locks, IReadOnlyList<ClrThread> threads)
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
                UpgraderThreadId = writer <= 0 && upgrader > 0 ? upgrader : null,
                ReaderCount = RwReaderCount(owners),
                ReaderThreadIds = ReadLockHolders(rw, threads),
            };
            return rw.Address;
        }
        return null;
    }

    /// Each thread's [ThreadStatic] ReaderWriterLockSlim.t_rwc list has an entry per lock it uses,
    /// matched by the lock's _lockID; readercount > 0 means it holds that lock for reading.
    private static List<int> ReadLockHolders(ClrObject rw, IReadOnlyList<ClrThread> threads)
    {
        var holders = new List<int>();
        try
        {
            if (!rw.TryReadField<long>("_lockID", out var id) || id == 0) return holders;
            var counts = rw.Type?.ThreadStaticFields.FirstOrDefault(f => f.Name == "t_rwc");
            if (counts is null) return holders;
            foreach (var thread in threads)
            {
                try
                {
                    var entry = counts.ReadObject(thread);
                    for (var guard = 0; !entry.IsNull && guard < 1_000; guard++)
                    {
                        if (entry.TryReadField<long>("lockID", out var lockId) && lockId == id)
                        {
                            if (entry.ReadInt("readercount") is > 0) holders.Add(thread.ManagedThreadId);
                            break;
                        }
                        if (!entry.TryReadObjectField("next", out entry)) break;
                    }
                }
                catch
                {
                    // This thread's counts can't be read; the others still can.
                }
            }
        }
        catch
        {
            // Without the per-thread counts the readers are still counted, just not named.
        }
        return holders;
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

    private static readonly string[] WorkCollections =
        ["System.Collections.Generic.Queue<", "System.Collections.Generic.Stack<", "System.Collections.Generic.List<",
         "System.Collections.Generic.LinkedList<", "System.Collections.Generic.PriorityQueue<"];

    /// Monitor.Wait called directly from app code (not from inside ManualResetEventSlim, SemaphoreSlim...):
    /// the object passed to it, and the collection next to it that the waiting code checks, if that already holds work.
    private PulseWait? FindPulseWait(ClrThread thread, List<ClrStackRoot> roots, IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = FrameClassifier.FrameworkPrefix(frames);
        if (prefix.Any(f => f.TypeName != "System.Threading.Monitor")) return null;

        var target = roots.FirstOrDefault(r => r.StackFrame?.Method is { Name: "Wait" or "ObjWait", Type.Name: "System.Threading.Monitor" } && !r.Object.IsNull)?.Object;
        if (target is not { } obj) return null;

        var pending = new List<string>();
        var possible = new List<string>();
        foreach (var root in roots)
        {
            var owner = root.Object;
            if (owner.IsNull || owner.Type is null || FrameClassifier.IsFrameworkType(owner.Type.Name ?? "")) continue;
            if (!owner.Type.Fields.Any(f => f.IsObjectReference && owner.ReadObjectField(f).Address == obj.Address)) continue;
            var collections = new List<(ClrInstanceField Field, int? Count)>();
            foreach (var field in owner.Type.Fields)
            {
                if (!field.IsObjectReference) continue;
                var value = owner.ReadObjectField(field);
                if (value.IsNull || value.Address == obj.Address || value.Type?.Name is not { } typeName || !WorkCollections.Any(c => typeName.StartsWith(c, StringComparison.Ordinal))) continue;
                collections.Add((field, value.ReadInt("_size") ?? value.ReadInt("count")));
            }
            string Label((ClrInstanceField Field, int? Count) c) =>
                $"{Names.ShortType(owner.Type.Name ?? "")}.{CleanFieldName(c.Field.Name)} ({c.Count} {(c.Count == 1 ? "item" : "items")})";

            var nonEmpty = collections.Where(c => c.Count is > 0).ToList();
            if (nonEmpty.Count == 0) break;
            // Only the collection the waiting code checks is its work; the object may hold others (results, history...).
            // A condition that reads none of them (a counter, a flag) says the collections aren't its work.
            var (outcome, checkedField) = WaitingFrame(thread) is var (method, ip)
                ? (_waitCondition ??= new WaitCondition(_runtime)).FirstReadAfterWait(method, ip, owner.Type, collections.Select(c => c.Field).ToList())
                : (IlInstructions.Outcome.Unknown, null);
            if (outcome == IlInstructions.Outcome.Unknown) possible.AddRange(nonEmpty.Select(Label));
            else if (outcome == IlInstructions.Outcome.Read && nonEmpty.FirstOrDefault(c => c.Field == checkedField) is { Field: not null } work) pending.Add(Label(work));
            break;
        }
        return new PulseWait { Object = new ObjectRef(obj.Address, obj.Type?.Name ?? "System.Object"), PendingWork = pending, PossibleWork = possible };
    }

    private WaitCondition? _waitCondition;

    /// The app method that called Monitor.Wait, and where in it the call returns to.
    private static (ClrMethod Method, ulong ReturnAddress)? WaitingFrame(ClrThread thread)
    {
        try
        {
            var inWait = false;
            foreach (var frame in thread.EnumerateStackTrace(includeContext: false, MaxFramesPerThread))
            {
                if (frame.Kind != ClrStackFrameKind.ManagedMethod || frame.Method is not { } method) continue;
                if (method.Type?.Name == "System.Threading.Monitor") inWait = true;
                else if (inWait) return frame.InstructionPointer == 0 ? null : (method, frame.InstructionPointer);
            }
        }
        catch
        {
            // Without the frame, the collections next to the object are reported as possible work only.
        }
        return null;
    }

    /// BlockingCollection.Add and Take wait on one of its two internal semaphores: free slots and occupied slots.
    private static CollectionWait? FindCollectionWait(List<ClrStackRoot> roots)
    {
        foreach (var root in roots)
        {
            var method = root.StackFrame?.Method;
            if (method?.Type?.Name?.StartsWith("System.Collections.Concurrent.BlockingCollection<", StringComparison.Ordinal) != true) continue;
            var adding = method.Name == "TryAddWithNoTimeValidation";
            if (!adding && method.Name != "TryTakeWithNoTimeValidation") continue;
            var c = root.Object;
            if (c.IsNull || c.Type?.Name?.StartsWith("System.Collections.Concurrent.BlockingCollection<", StringComparison.Ordinal) != true) continue;
            var capacity = c.ReadInt("_boundedCapacity");
            int? count = c.TryReadObjectField("_occupiedNodes", out var occupied) && !occupied.IsNull ? occupied.ReadInt("m_currentCount") : null;
            return new CollectionWait
            {
                Collection = new ObjectRef(c.Address, c.Type.Name),
                Adding = adding,
                Capacity = capacity is > 0 ? capacity : null,
                Count = count,
            };
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
            // Next is declared on TaskNode itself; look it up on the node's own type.
            var next = node.Type?.GetFieldByName("Next");
            if (next is null || !next.IsObjectReference) yield break;
            node = node.ReadObjectField(next);
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
        var readers = new List<(ClrObject Reader, bool Reading)>();
        var lists = new List<int>();
        string? isolation = null;
        var seen = new HashSet<ulong>();
        foreach (var root in roots)
        {
            var obj = root.Object;
            if (!obj.IsNull && root.StackFrame?.Method?.Type?.Name is { } frameType && !FrameClassifier.IsFrameworkType(frameType)
                && obj.Type?.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true && obj.ReadInt("_size") is { } size)
                lists.Add(size);
            if (!obj.IsNull && DerivesFrom(obj.Type, "System.Data.Common.DbDataReader"))
            {
                var reading = root.StackFrame?.Method is { Name: "Read" or "ReadAsync" } m && DerivesFrom(m.Type, "System.Data.Common.DbDataReader");
                var i = readers.FindIndex(r => r.Reader.Address == obj.Address);
                if (i < 0) readers.Add((obj, reading));
                else if (reading) readers[i] = (obj, true);
            }
            if (obj.IsNull || !seen.Add(obj.Address)) continue;
            if (isolation is null && DerivesFrom(obj.Type, "System.Data.Common.DbTransaction") && obj.ReadInt("_isolationLevel") is { } level
                && Enum.IsDefined(typeof(System.Data.IsolationLevel), level))
                isolation = ((System.Data.IsolationLevel)level).ToString();
            if (DerivesFrom(obj.Type, "System.Data.Common.DbCommand")) commands.Add(obj);
            else if (DerivesFrom(obj.Type, "System.Data.Common.DbConnection")) connections.Add(obj);
            else if (EfConnection(obj) is { } wrapped && seen.Add(wrapped.Address)) connections.Add(wrapped);
        }

        // A connection kept in a field of the app's own objects (a "leader" connection, a cached one) is
        // held for as long as that object lives, even while the thread does something else.
        var kept = new List<(ClrObject Connection, string Field)>();
        var scanned = new HashSet<ulong>();
        foreach (var root in roots)
        {
            var owner = root.Object;
            if (owner.IsNull || owner.Type is null || FrameClassifier.IsFrameworkType(owner.Type.Name ?? "") || !scanned.Add(owner.Address)) continue;
            foreach (var field in owner.Type.Fields)
            {
                if (!field.IsObjectReference) continue;
                var value = owner.ReadObjectField(field);
                if (!value.IsNull && seen.Add(value.Address) && DerivesFrom(value.Type, "System.Data.Common.DbConnection"))
                    kept.Add((value, $"{Names.ShortType(owner.Type.Name ?? "")}.{CleanFieldName(field.Name)}"));
            }
        }

        var open = connections.Select(ReadConnection).OfType<DatabaseConnection>()
            .Concat(kept.Select(k => ReadConnection(k.Connection) is { } c ? c with { Field = k.Field } : null).OfType<DatabaseConnection>())
            .ToList();
        var waiting = kind switch
        {
            BlockingKind.Database => DatabaseWaitKind.Query,
            BlockingKind.ConnectionPool => DatabaseWaitKind.ConnectionPool,
            _ => DatabaseWaitKind.None,
        };
        if (waiting == DatabaseWaitKind.None && open.Count == 0) return null;

        var openReaders = readers.Where(r => ReaderIsOpen(r.Reader)).ToList();
        var readingRows = openReaders.FirstOrDefault(r => r.Reading).Reader;
        if (waiting == DatabaseWaitKind.Query && !readingRows.IsNull) waiting = DatabaseWaitKind.ReadingRows;
        // Running app code (not blocked) with a reader open: processing rows between Read() calls,
        // which can include the app's own I/O (writing each row to a socket or a pipe).
        if (waiting == DatabaseWaitKind.None && kind is BlockingKind.None or BlockingKind.SyncIO && openReaders.Count > 0)
        {
            waiting = DatabaseWaitKind.ReadingRows;
            readingRows = openReaders[0].Reader;
        }
        var openQuery = openReaders.Select(r => ReaderCommand(r.Reader)).OfType<ClrObject>().Select(DatabaseCommandText).FirstOrDefault(q => q is not null);

        var command = waiting == DatabaseWaitKind.Query ? commands.FirstOrDefault()
            : waiting == DatabaseWaitKind.ReadingRows ? ReaderCommand(readingRows) ?? commands.FirstOrDefault()
            : default;
        var typeName = (commands.Concat(connections).Concat(kept.Select(k => k.Connection)).FirstOrDefault().Type?.Name) ?? "";
        var target = waiting == DatabaseWaitKind.ConnectionPool ? connections.Select(ConnectTarget).FirstOrDefault(t => t.DataSource is not null) : default;
        return new DatabaseActivity
        {
            Provider = FrameClassifier.DatabaseProvider(typeName) ?? "the database",
            Waiting = waiting,
            CommandText = command.IsNull ? null : DatabaseCommandText(command),
            SessionId = command.IsNull ? null : CommandSession(command),
            Connections = open,
            Pool = waiting == DatabaseWaitKind.ConnectionPool ? connections.Select(ReadPool).FirstOrDefault(p => p is not null) : null,
            DataSource = target.DataSource,
            ConnectTimeout = target.Timeout,
            OpenQuery = openQuery,
            Isolation = isolation,
            RowsRead = waiting == DatabaseWaitKind.ReadingRows && lists.Count > 0 ? lists.Max() : null,
            StackCommands = waiting == DatabaseWaitKind.None
                ? commands.Select(DatabaseCommandText).OfType<string>().Distinct().ToList()
                : [],
        };
    }

    /// Npgsql: State is BeforeResult (0), InResult (1) or BetweenResults (2) while open. SqlClient: _isClosed.
    private static bool ReaderIsOpen(ClrObject reader) =>
        reader.ReadInt("State") is { } state ? state <= 2
        : reader.TryReadField<bool>("_isClosed", out var closed) ? !closed
        : false;

    private static ClrObject? ReaderCommand(ClrObject reader) =>
        reader.IsNull ? null
        : reader.TryReadObjectField("<Command>k__BackingField", out var c) && !c.IsNull ? c
        : reader.TryReadObjectField("_command", out c) && !c.IsNull ? c
        : null;

    /// EF Core keeps the ADO.NET connection inside its own wrappers: a RelationalTransaction (what
    /// BeginTransaction returns) points at a RelationalConnection, which holds the DbConnection.
    private static ClrObject? EfConnection(ClrObject obj)
    {
        if (obj.Type?.Name?.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) != true
            && obj.Type?.Name?.Contains(".EntityFrameworkCore.", StringComparison.Ordinal) != true) return null;
        var relational = obj;
        if (DerivesFrom(obj.Type, "Microsoft.EntityFrameworkCore.Storage.RelationalTransaction")
            && !(obj.TryReadObjectField("<Connection>k__BackingField", out relational) && !relational.IsNull)) return null;
        if (!DerivesFrom(relational.Type, "Microsoft.EntityFrameworkCore.Storage.RelationalConnection")) return null;
        return relational.TryReadObjectField("_connection", out var connection) && !connection.IsNull ? connection : null;
    }

    private static DatabaseConnection? ReadConnection(ClrObject connection)
    {
        if (connection.Type?.Name?.EndsWith(".SqlConnection", StringComparison.Ordinal) == true) return ReadSqlConnection(connection);
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

    // SqlClient: SqlConnection._innerConnection (State 1 = open) -> _parser (TdsParser), whose _currentTransaction
    // is the open transaction (_transactionState 1 = active) and whose physical state object knows the SPID.
    private const int SqlInternalStateOpen = 1;
    private const int SqlTransactionActive = 1;

    private static DatabaseConnection? ReadSqlConnection(ClrObject connection)
    {
        if (!connection.TryReadObjectField("_innerConnection", out var inner) || inner.IsNull) return null;
        if (inner.ReadInt("<State>k__BackingField") is { } state && state != SqlInternalStateOpen) return null;
        if (!inner.TryReadObjectField("_parser", out var parser) || parser.IsNull) return null;
        var inTx = parser.TryReadObjectField("_currentTransaction", out var tx) && !tx.IsNull && tx.ReadInt("_transactionState") == SqlTransactionActive;
        return new DatabaseConnection(SqlSpid(parser), inTx);
    }

    private static int? SqlSpid(ClrObject parser) =>
        parser.TryReadObjectField("_physicalStateObj", out var state) && !state.IsNull && state.ReadInt("_spid") is > 0 and var spid ? spid : null;

    private static int? CommandSession(ClrObject command)
    {
        if (command.TryReadObjectField("_activeConnection", out var sql) && !sql.IsNull
            && sql.TryReadObjectField("_innerConnection", out var inner) && !inner.IsNull
            && inner.TryReadObjectField("_parser", out var parser) && !parser.IsNull)
            return SqlSpid(parser);
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

    /// The server a connection is for and its connect timeout, from the parsed connection string the driver keeps.
    /// Npgsql: NpgsqlConnection.Settings (_host, _port, _timeout). SqlClient: SqlConnection._userConnectionOptions
    /// (_dataSource, _connectTimeout). Never the password or anything else from the string.
    private static (string? DataSource, int? Timeout) ConnectTarget(ClrObject connection)
    {
        try
        {
            if (connection.TryReadObjectField("<Settings>k__BackingField", out var settings) && !settings.IsNull
                && settings.TryReadStringField("_host", 256, out var host) && !string.IsNullOrEmpty(host))
            {
                var port = settings.ReadInt("_port");
                return (port is > 0 && !host.Contains(',') ? $"{host}:{port}" : host, settings.ReadInt("_timeout"));
            }
            if (connection.TryReadObjectField("_userConnectionOptions", out var options) && !options.IsNull
                && options.TryReadStringField("_dataSource", 256, out var dataSource) && !string.IsNullOrEmpty(dataSource))
                return (dataSource, options.ReadInt("_connectTimeout"));
        }
        catch
        {
            // Unknown target: the wait is still described.
        }
        return (null, null);
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
