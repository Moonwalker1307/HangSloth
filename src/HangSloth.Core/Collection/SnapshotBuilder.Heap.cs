using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

// Walking the whole GC heap is what costs time on a big dump. Thread objects come from the handle table
// instead, and everything else the builder looks up on the heap is gathered in one pass, made only when needed.
public sealed partial class SnapshotBuilder
{
    private const string ThreadType = "System.Threading.Thread";
    private const string NativeThreadField = "_DONT_USE_InternalThread";

    private HeapIndex? _heapIndex;
    private bool? _npgsqlLoaded;

    /// Lets a test make reading one thread fail, the way an unreadable object in a damaged dump does.
    [ThreadStatic] internal static Action<int, string>? ThreadReadFault;

    /// Full heap walks made on this thread, so a test can check that a snapshot needs at most one.
    [ThreadStatic] internal static int HeapWalks;

    /// What the builder needs from walking the heap: objects that nothing on a stack points at.
    private sealed class HeapIndex
    {
        public readonly Dictionary<int, ClrObject> ThreadsById = [];
        public readonly Dictionary<ulong, ulong> SemaphoreOfTaskNode = [];
        public readonly Dictionary<ulong, ulong> TaskOfContinuation = [];
        public readonly List<ClrObject> NpgsqlPools = [];
        /// The first NpgsqlConnection's type, to tell whether this driver version's names are known.
        public ClrType? NpgsqlConnectionType;
        public readonly List<ulong> PendingAsyncBoxes = [];
    }

    [Flags]
    private enum HeapKind { None = 0, Thread = 1, Semaphore = 2, Task = 4, Wrapper = 8, NpgsqlPool = 16, AsyncBox = 32, NpgsqlConnection = 64 }

    private HeapIndex HeapLookups => _heapIndex ??= WalkHeap();

    /// Each live thread's System.Threading.Thread object, by managed id.
    private Dictionary<int, ClrObject> FindManagedThreadObjects(Dictionary<ulong, int> liveThreads)
    {
        if (!_heap.CanWalkHeap)
        {
            _warnings.Add(HeapNotWalkableWarning);
            _heapAvailable = false;
            return [];
        }
        // A running .NET process always has objects on its heap. A mini or triage dump leaves the heap's memory
        // out, and ClrMD then still says the heap can be walked but enumerates nothing.
        if (!HeapHasObjects())
        {
            _heapAvailable = false;
            return [];
        }
        return ThreadObjectsFromHandles(liveThreads) ?? new Dictionary<int, ClrObject>(HeapLookups.ThreadsById);
    }

    private bool HeapHasObjects()
    {
        try
        {
            return _heap.EnumerateObjects().Any();
        }
        catch (Exception e)
        {
            _warnings.Add($"Walking the heap failed part-way: {e.Message}");
            return false;
        }
    }

    /// The runtime keeps a handle to every thread's Thread object, so the handle table finds them without a heap
    /// walk. Null when it can't be used (no Thread object in it, a runtime without the native thread field, or no
    /// object that points at a live thread): then the heap walk matches them by managed id, as before.
    private Dictionary<int, ClrObject>? ThreadObjectsFromHandles(Dictionary<ulong, int> liveThreads)
    {
        var candidates = new List<(ClrObject Object, int? ManagedId, ulong? NativeThread)>();
        var seen = new HashSet<ulong>();
        try
        {
            var pointerSize = _runtime.DataTarget.DataReader.PointerSize;
            foreach (var handle in _runtime.EnumerateHandles())
            {
                var obj = handle.Object;
                if (obj.IsNull || obj.Type is not { Name: ThreadType } type || !seen.Add(obj.Address)) continue;
                if (type.GetFieldByName(NativeThreadField) is null) return null;
                ulong? native = pointerSize == 4
                    ? obj.TryReadField<uint>(NativeThreadField, out var p32) ? p32 : null
                    : obj.TryReadField<ulong>(NativeThreadField, out var p64) ? p64 : null;
                candidates.Add((obj, obj.TryReadField<int>("_managedThreadId", out var id) ? id : null, native));
            }
        }
        catch
        {
            return null;
        }
        return MatchThreadObjects(candidates, liveThreads);
    }

    /// A Thread object belongs to the live thread whose native thread it points at; the pointer is zero once its
    /// thread has ended. Managed ids are reused, so a match by id alone can name a live thread after one that ended.
    /// Two objects claiming one thread: the one with that thread's managed id. An object whose pointer can't be read
    /// is skipped. Null when no object points at a live thread.
    internal static Dictionary<int, T>? MatchThreadObjects<T>(IReadOnlyList<(T Object, int? ManagedId, ulong? NativeThread)> candidates, IReadOnlyDictionary<ulong, int> liveThreads)
    {
        var result = new Dictionary<int, T>();
        foreach (var (obj, managedId, native) in candidates)
        {
            if (native is not { } address || address == 0 || !liveThreads.TryGetValue(address, out var id)) continue;
            if (!result.ContainsKey(id) || managedId == id) result[id] = obj;
        }
        return result.Count == 0 ? null : result;
    }

    private bool NpgsqlLoaded => _npgsqlLoaded ??= IsNpgsqlLoaded();

    private bool IsNpgsqlLoaded()
    {
        try
        {
            return _runtime.EnumerateModules().Any(m => Path.GetFileName(m.Name ?? "").StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// One pass over the heap for all of the lookups. Each type is classified once, so the pass costs about as
    /// much as walking the heap for one of them.
    private HeapIndex WalkHeap()
    {
        var index = new HeapIndex();
        if (!_heapAvailable || !_heap.CanWalkHeap) return index;
        // The async methods are only needed to explain a full Npgsql pool.
        var boxes = NpgsqlLoaded;
        HeapWalks++;
        var kinds = new Dictionary<ulong, HeapKind>();
        HeapKind KindOf(ClrType type)
        {
            if (kinds.TryGetValue(type.MethodTable, out var kind)) return kind;
            var name = type.Name ?? "";
            kind = name switch
            {
                ThreadType => HeapKind.Thread,
                "System.Threading.SemaphoreSlim" => HeapKind.Semaphore,
                "Npgsql.NpgsqlConnection" => HeapKind.NpgsqlConnection,
                _ when NpgsqlPoolTypes.Contains(name) => HeapKind.NpgsqlPool,
                _ => HeapKind.None,
            };
            if (IsTask(type))
                kind |= name.Contains("AsyncStateMachineBox", StringComparison.Ordinal) ? HeapKind.Task : HeapKind.Task | HeapKind.Wrapper;
            if (name.Contains("AsyncStateMachineBox<", StringComparison.Ordinal)) kind |= HeapKind.AsyncBox;
            return kinds[type.MethodTable] = kind;
        }

        try
        {
            foreach (var obj in _heap.EnumerateObjects())
            {
                if (obj.Type is not { } type) continue;
                var kind = KindOf(type);
                if (kind == HeapKind.None) continue;
                try
                {
                    if ((kind & HeapKind.Thread) != 0 && obj.TryReadField<int>("_managedThreadId", out var id) && id != 0)
                        index.ThreadsById[id] = obj;
                    if ((kind & HeapKind.Semaphore) != 0)
                    {
                        // WaitAsync queues a TaskNode on the semaphore; the node doesn't point back.
                        foreach (var n in AsyncWaiterNodes(obj))
                            index.SemaphoreOfTaskNode[n] = obj.Address;
                    }
                    if ((kind & HeapKind.NpgsqlPool) != 0) index.NpgsqlPools.Add(obj);
                    if ((kind & HeapKind.NpgsqlConnection) != 0) index.NpgsqlConnectionType ??= type;
                    if ((kind & HeapKind.Task) != 0)
                    {
                        // A wrapper task is registered as a continuation on the task it waits for and doesn't point at it.
                        // Only wrappers are looked up: delegates, ContinueWith and await continuations would just fill the map.
                        foreach (var continuation in EnumerateContinuations(obj))
                        {
                            if (continuation.Type is { } continuationType && (KindOf(continuationType) & HeapKind.Wrapper) != 0)
                                index.TaskOfContinuation.TryAdd(continuation.Address, obj.Address);
                        }
                    }
                    if (boxes && (kind & HeapKind.AsyncBox) != 0 && ReadTaskState(obj) == TaskState.Pending)
                        index.PendingAsyncBoxes.Add(obj.Address);
                }
                catch
                {
                    // One unreadable object; the rest of the heap still counts.
                }
            }
        }
        catch (Exception e)
        {
            _warnings.Add($"Walking the heap failed part-way: {e.Message}");
        }
        return index;
    }
}
