namespace DeadlockLab.Code;

/// A native handle wrapper whose finalizer logs through a shared, locked logger. A cleanup job
/// holds the logger's lock and waits for pending finalizers, so the finalizer thread can't finish.
public static class HandleLog
{
    public static readonly object Sync = new();
    public static readonly List<string> Lines = [];
}

public sealed class NativeBuffer
{
    ~NativeBuffer()
    {
        lock (HandleLog.Sync) HandleLog.Lines.Add("buffer released"); // runs on the finalizer thread
    }
}

internal static class FinalizerWaitScenario
{
    public static void Run()
    {
        Allocate();
        Ready.SignalLater();
        lock (HandleLog.Sync)
        {
            HandleLog.Lines.Add("cleanup started");
            GC.Collect();
            GC.WaitForPendingFinalizers(); // BUG: the finalizer needs HandleLog.Sync
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Allocate()
    {
        for (var i = 0; i < 10; i++) _ = new NativeBuffer();
    }
}
