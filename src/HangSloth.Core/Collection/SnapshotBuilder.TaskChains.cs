using HangSloth.Analysis;
using HangSloth.Model;
using Microsoft.Diagnostics.Runtime;

namespace HangSloth.Collection;

// Tasks that only stand in for other work: ContinueWith tasks, and the promises behind Task.Run(async ...),
// Unwrap(), WaitAsync, Task.WhenAny and Task.WaitAny. Blocking on one of them says little; follow it to the work it waits for.
public sealed partial class SnapshotBuilder
{
    private Dictionary<ulong, ulong>? _taskOfContinuation;
    private readonly HashSet<ulong> _resumedAsyncMethods = [];

    /// Exactly Task or Task&lt;T&gt;, which is what a TaskCompletionSource hands out. Subclasses are completed
    /// by the framework itself, when the work they stand for is done.
    internal static bool IsPlainTask(string typeName)
    {
        const string generic = "System.Threading.Tasks.Task<";
        if (typeName == "System.Threading.Tasks.Task") return true;
        if (!typeName.StartsWith(generic, StringComparison.Ordinal)) return false;
        var open = 0;
        for (var i = generic.Length - 1; i < typeName.Length; i++)
        {
            if (typeName[i] == '<') open++;
            else if (typeName[i] == '>' && --open == 0) return i == typeName.Length - 1;
        }
        return false;
    }

    /// A ContinueWith task starts when its antecedent finishes, and points at it until then.
    private TaskInfo DescribeContinuation(ClrObject task, TaskInfo info, int depth, HashSet<ulong> visited)
    {
        info = info with { Kind = TaskKind.Continuation, Description = "a ContinueWith(...) task" };
        if (depth < MaxTaskChainDepth
            && task.TryReadObjectField("m_antecedent", out var antecedent) && !antecedent.IsNull && IsTask(antecedent.Type)
            && ReadTaskState(antecedent) == TaskState.Pending && !visited.Contains(antecedent.Address))
            info = info with { Awaiting = DescribeTask(antecedent, depth + 1, visited) };
        return info;
    }

    /// Any other Task subclass is a promise the framework completes when the work it stands for is done, mostly
    /// another task: Task.Run(async ...) and Unwrap() return an UnwrapPromise, WaitAsync a CancellationPromise.
    /// When that task can't be found, the wrapper is only named, so it isn't mistaken for a TaskCompletionSource.
    private TaskInfo DescribeWrapper(ClrObject task, TaskInfo info, int depth, HashSet<ulong> visited)
    {
        if (info.TypeName.StartsWith("System.Threading.Tasks.TaskFactory+CompleteOnInvokePromise<", StringComparison.Ordinal))
            return DescribeWhenAny(task, info, depth, visited);

        TaskInfo? source = null;
        if (depth < MaxTaskChainDepth && FindSourceTask(task) is { } found && !visited.Contains(found.Address))
            source = DescribeTask(found, depth + 1, visited);
        // Task.Run sets the flag (it also unwraps a cancellation thrown by the delegate); a plain Unwrap() doesn't.
        var fromTaskRun = !task.TryReadField<bool>("_lookForOce", out var lookForOce) || lookForOce;
        info = info with { Description = WrapperName(info.TypeName, fromTaskRun, source?.Kind) ?? info.Description };
        return source is null ? info : info with { Kind = TaskKind.Wrapper, Awaiting = source };
    }

    /// What the code called to get this wrapper, or null when its type name is all there is to say.
    internal static string? WrapperName(string typeName, bool fromTaskRun, TaskKind? source)
    {
        if (typeName.StartsWith("System.Threading.Tasks.UnwrapPromise<", StringComparison.Ordinal))
            // Only an async method behind it shows the lambda is async: Task.Run(() => tcs.Task) gets this promise too.
            return !fromTaskRun ? "Unwrap()" : source == TaskKind.AsyncMethod ? "Task.Run(async ...)" : "Task.Run(...)";
        if (typeName.StartsWith("System.Threading.Tasks.Task+CancellationPromise<", StringComparison.Ordinal))
            return "WaitAsync(...)";
        if (typeName.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal) && typeName.EndsWith("+ValueTaskSourceAsTask", StringComparison.Ordinal))
            return "ValueTask.AsTask()";
        return null;
    }

    private const int MaxWhenAnyTasks = 64;

    /// Task.WhenAny of more than two tasks, and Task.WaitAny. It finishes as soon as one of its tasks does, so it is
    /// stuck only when every one of them ends in a TaskCompletionSource nobody completes; only then is it followed.
    private TaskInfo DescribeWhenAny(ClrObject task, TaskInfo info, int depth, HashSet<ulong> visited)
    {
        var count = 0;
        var tasks = task.TryReadObjectField("_tasks", out var list) && !list.IsNull ? ReadTaskList(list, out count) : [];
        // Task.WaitAny blocks on the same promise, marked as a synchronous wait; the thread's own call already names it.
        var waitAny = ((task.ReadInt("_stateFlags") ?? 0) & 2) != 0;
        info = info with { Kind = TaskKind.WhenAny, Description = waitAny && count > 0 ? $"one of its {count} tasks" : "Task.WhenAny(...)" };
        if (depth >= MaxTaskChainDepth || tasks.Count == 0 || count > MaxWhenAnyTasks) return info;

        TaskInfo? first = null;
        foreach (var t in tasks)
        {
            if (!IsTask(t.Type) || ReadTaskState(t) != TaskState.Pending || visited.Contains(t.Address)) return info;
            // Each task is a path of its own: two of them may well wait for the same thing.
            var described = DescribeTask(t, depth + 1, new HashSet<ulong>(visited));
            if (Describe.Chain(described).Last().Kind != TaskKind.Promise) return info;
            first ??= described;
        }
        // The first task stands for all of them; for Task.WaitAny, it is what the thread waits for.
        return waitAny ? first! : info with { Kind = TaskKind.Wrapper, Awaiting = first };
    }

    /// The tasks of a Task[] or a List&lt;Task&gt;: the first few, and how many there are.
    private static List<ClrObject> ReadTaskList(ClrObject list, out int count)
    {
        count = 0;
        var items = list;
        int? size = null;
        if (list.Type?.Name?.StartsWith("System.Collections.Generic.List<", StringComparison.Ordinal) == true)
        {
            if (!list.TryReadObjectField("_items", out items) || items.IsNull) return [];
            size = list.ReadInt("_size");
        }
        if (!items.IsArray) return [];
        var array = items.AsArray();
        count = Math.Clamp(size ?? array.Length, 0, array.Length);
        var result = new List<ClrObject>();
        for (var i = 0; i < Math.Min(count, MaxWhenAnyTasks); i++)
        {
            var element = array.GetObjectValue(i);
            if (!element.IsNull) result.Add(element);
        }
        return result;
    }

    /// The task a wrapper waits for. The wrapper doesn't point at it: it is registered as a continuation
    /// on that task, so map continuations back to their tasks once.
    private ClrObject? FindSourceTask(ClrObject wrapper)
    {
        if (_taskOfContinuation is null)
        {
            _taskOfContinuation = [];
            try
            {
                if (_heap.CanWalkHeap)
                {
                    var kinds = new Dictionary<ulong, (bool IsTask, bool IsWrapper)>();
                    (bool IsTask, bool IsWrapper) KindOf(ClrType type)
                    {
                        if (!kinds.TryGetValue(type.MethodTable, out var kind))
                        {
                            var isTask = IsTask(type);
                            kinds[type.MethodTable] = kind = (isTask, isTask && type.Name?.Contains("AsyncStateMachineBox", StringComparison.Ordinal) != true);
                        }
                        return kind;
                    }
                    foreach (var obj in _heap.EnumerateObjects())
                    {
                        if (obj.Type is not { } type || !KindOf(type).IsTask) continue;
                        foreach (var continuation in EnumerateContinuations(obj))
                        {
                            // Only wrappers are looked up: delegates, ContinueWith and await continuations would just fill the map.
                            if (continuation.Type is { } continuationType && KindOf(continuationType).IsWrapper)
                                _taskOfContinuation.TryAdd(continuation.Address, obj.Address);
                        }
                    }
                }
            }
            catch
            {
                // Without the mapping the wrapper is still described, just not what it waits for.
            }
        }
        if (!_taskOfContinuation.TryGetValue(wrapper.Address, out var address)) return null;
        var source = _heap.GetObject(address);
        return source.IsValid && IsTask(source.Type) && ReadTaskState(source) == TaskState.Pending ? source : null;
    }

    /// An async method that was resumed after an await runs from its box's MoveNext, not from a Task.Run
    /// body. Each box is given to one thread only.
    private bool IsResumedAsyncMethod(ClrStackRoot root)
    {
        if (root.StackFrame?.Method?.Type?.Name?.Contains("AsyncStateMachineBox<", StringComparison.Ordinal) != true) return false;
        var box = root.Object;
        return !box.IsNull && box.Type?.Name?.Contains("AsyncStateMachineBox<", StringComparison.Ordinal) == true && IsTask(box.Type)
               && ReadTaskState(box) == TaskState.Pending && !_resumedAsyncMethods.Contains(box.Address);
    }
}
