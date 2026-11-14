using HangSloth.Model;

namespace HangSloth.Analysis;

/// What a thread is doing, judged from the framework frames on top of its stack.
public enum BlockingKind
{
    /// Not blocked in a known wait (running, or nothing recognisable).
    None,
    /// Infrastructure thread parked waiting for work (idle pool worker, timer thread, gate thread).
    Idle,
    /// Synchronously waiting on a Task: .Result, .Wait(), .GetAwaiter().GetResult(), Task.WaitAll.
    TaskWait,
    /// Trying to enter a lock (Monitor.Enter / lock statement / System.Threading.Lock).
    MonitorEnter,
    /// Monitor.Wait: waiting to be pulsed.
    MonitorWait,
    Sleep,
    Join,
    SemaphoreWait,
    WaitHandle,
}

public static class FrameClassifier
{
    public static BlockingKind Classify(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = FrameworkPrefix(frames);
        if (prefix.Count == 0) return BlockingKind.None;

        if (prefix.Any(f => IsTaskWaitMethod(f.TypeName, f.MethodName))) return BlockingKind.TaskWait;
        if (IsIdleInfrastructure(frames)) return BlockingKind.Idle;
        if (prefix.Any(IsMonitorEnter)) return BlockingKind.MonitorEnter;
        if (prefix.Any(f => f.TypeName == "System.Threading.Monitor" && f.MethodName is "Wait" or "ObjWait")) return BlockingKind.MonitorWait;
        if (prefix.Any(f => f.TypeName == "System.Threading.Thread" && f.MethodName is "Sleep" or "SleepInternal")) return BlockingKind.Sleep;
        if (prefix.Any(f => f.TypeName == "System.Threading.Thread" && f.MethodName == "Join")) return BlockingKind.Join;
        if (prefix.Any(f => f.TypeName == "System.Threading.SemaphoreSlim" && f.MethodName.StartsWith("Wait", StringComparison.Ordinal))) return BlockingKind.SemaphoreWait;
        if (prefix.Any(f => f.TypeName == "System.Threading.WaitHandle" && f.MethodName.StartsWith("Wait", StringComparison.Ordinal))) return BlockingKind.WaitHandle;
        return BlockingKind.None;
    }

    public static bool IsBlocked(BlockingKind kind) => kind is not (BlockingKind.None or BlockingKind.Idle);

    /// The framework frames at the top of the stack, above the first frame of the app's own code.
    public static IReadOnlyList<StackFrameInfo> FrameworkPrefix(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = new List<StackFrameInfo>();
        foreach (var frame in frames)
        {
            if (IsUserFrame(frame)) break;
            prefix.Add(frame);
        }
        return prefix;
    }

    public static StackFrameInfo? FirstUserFrame(IReadOnlyList<StackFrameInfo> frames) => frames.FirstOrDefault(IsUserFrame);

    /// The innermost user frame that has a source location, falling back to the innermost user frame.
    public static StackFrameInfo? BestUserFrame(IReadOnlyList<StackFrameInfo> frames) =>
        FirstUserFrame(frames) is { } first && first.Source is null
            ? frames.Where(IsUserFrame).FirstOrDefault(f => f.Source is not null) ?? first
            : FirstUserFrame(frames);

    public static bool IsUserFrame(StackFrameInfo frame) =>
        !frame.IsRuntimeFrame && !IsFrameworkType(frame.TypeName) && !IsFrameworkModule(frame.ModuleName);

    public static bool IsFrameworkType(string typeName) =>
        typeName.StartsWith("System.", StringComparison.Ordinal)
        || typeName.StartsWith("Microsoft.", StringComparison.Ordinal)
        || typeName.StartsWith("Internal.", StringComparison.Ordinal)
        || typeName.Length == 0;

    public static bool IsFrameworkModule(string? modulePath)
    {
        if (string.IsNullOrEmpty(modulePath)) return false;
        var normalized = modulePath.Replace('\\', '/');
        if (normalized.Contains("/shared/Microsoft.", StringComparison.OrdinalIgnoreCase)) return true;
        var file = Path.GetFileName(normalized);
        return file.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
               || file.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
               || file.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase)
               || file.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTaskWaitMethod(string typeName, string method)
    {
        if (typeName.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
        {
            return method is "Wait" or "WaitAll" or "WaitAllCore" or "WaitAllBlockingCore" or "WaitAny" or "WaitAnyCore"
                or "InternalWait" or "InternalWaitCore" or "SpinThenBlockingWait" or "GetResultCore" or "get_Result";
        }
        if (typeName.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal)
            || typeName.StartsWith("System.Runtime.CompilerServices.ConfiguredTaskAwaitable", StringComparison.Ordinal))
        {
            return method is "GetResult" or "ValidateEnd" or "HandleNonSuccessAndDebuggerNotification";
        }
        return false;
    }

    /// How the app's code blocked on the task, in the words a developer would use.
    public static string DescribeTaskWait(IReadOnlyList<StackFrameInfo> frames)
    {
        var prefix = FrameworkPrefix(frames);
        if (prefix.Any(f => f.TypeName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) && f.MethodName is "GetResult" or "HandleNonSuccessAndDebuggerNotification" or "ValidateEnd"))
            return ".GetAwaiter().GetResult()";
        if (prefix.Any(f => f.MethodName.StartsWith("WaitAll", StringComparison.Ordinal))) return "Task.WaitAll(...)";
        if (prefix.Any(f => f.MethodName.StartsWith("WaitAny", StringComparison.Ordinal))) return "Task.WaitAny(...)";
        if (prefix.Any(f => f.MethodName is "GetResultCore" or "get_Result")) return ".Result";
        if (prefix.Any(f => f.MethodName == "Wait")) return ".Wait()";
        return "a blocking wait";
    }

    public static bool IsThreadPoolDispatchFrame(StackFrameInfo frame) =>
        (frame.TypeName == "System.Threading.ThreadPoolWorkQueue" && frame.MethodName == "Dispatch")
        || (frame.TypeName.StartsWith("System.Threading.PortableThreadPool+WorkerThread", StringComparison.Ordinal) && frame.MethodName.StartsWith("WorkerThreadStart", StringComparison.Ordinal));

    /// The pool's own housekeeping threads (gate thread, wait thread), which are flagged as pool threads but never run work items.
    public static bool IsThreadPoolServiceFrame(StackFrameInfo frame) =>
        frame.TypeName.StartsWith("System.Threading.PortableThreadPool+GateThread", StringComparison.Ordinal)
        || frame.TypeName.StartsWith("System.Threading.PortableThreadPool+WaitThread", StringComparison.Ordinal);

    private static bool IsMonitorEnter(StackFrameInfo f) =>
        (f.TypeName == "System.Threading.Monitor" && (f.MethodName is "Enter" or "ReliableEnter" or "ReliableEnterTimeout" or "TryEnter" || f.MethodName.Contains("Slow", StringComparison.OrdinalIgnoreCase)))
        || (f.TypeName == "System.Threading.Lock" && f.MethodName.Contains("Enter", StringComparison.Ordinal));

    private static bool IsIdleInfrastructure(IReadOnlyList<StackFrameInfo> frames)
    {
        // Idle means: nothing but runtime plumbing on the stack, parked in its own wait.
        if (frames.Any(IsUserFrame)) return false;
        return frames.Any(f =>
            (f.TypeName.StartsWith("System.Threading.PortableThreadPool", StringComparison.Ordinal) && f.MethodName is "WorkerThreadStart" or "GateThreadStart")
            || f.TypeName == "System.Threading.TimerQueue" && f.MethodName == "TimerThread"
            || f.TypeName.StartsWith("System.Threading.PortableThreadPool+WaitThread", StringComparison.Ordinal)
            || f.TypeName.StartsWith("System.Threading.LowLevelLifoSemaphore", StringComparison.Ordinal) && frames.All(x => !IsUserFrame(x))
            || f.TypeName == "System.GC" && f.MethodName.Contains("Finaliz", StringComparison.Ordinal)
            || f.TypeName.Contains("Sockets", StringComparison.Ordinal) && f.MethodName.Contains("EventLoop", StringComparison.Ordinal));
    }
}
