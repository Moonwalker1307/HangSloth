using HangSloth.Model;

namespace HangSloth.Tests;

/// Builds synthetic snapshots so detectors can be tested without a dump.
internal static class SnapshotFactory
{
    public static StackFrameInfo Fx(string type, string method) => new() { TypeName = type, MethodName = method, ModuleName = "/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll" };

    public static StackFrameInfo App(string type, string method, string file, int line) =>
        new() { TypeName = type, MethodName = method, ModuleName = "/app/MyApp.dll", Source = new SourceLocation($"/src/{file}", line) };

    public static readonly StackFrameInfo[] ResultWait =
    [
        Fx("System.Threading.Monitor", "Wait"),
        Fx("System.Threading.ManualResetEventSlim", "Wait"),
        Fx("System.Threading.Tasks.Task", "SpinThenBlockingWait"),
        Fx("System.Threading.Tasks.Task", "InternalWaitCore"),
        Fx("System.Threading.Tasks.Task`1", "GetResultCore"),
    ];

    public static readonly StackFrameInfo[] MonitorEnter = [Fx("System.Threading.Monitor", "ReliableEnter")];

    public static readonly StackFrameInfo[] PoolBottom =
    [
        Fx("System.Threading.ThreadPoolWorkQueue", "Dispatch"),
        Fx("System.Threading.PortableThreadPool+WorkerThread", "WorkerThreadStart"),
    ];

    public static ThreadSnapshot Thread(int id, IEnumerable<StackFrameInfo> frames, string? name = null) =>
        new() { ManagedId = id, Name = name, Frames = frames.ToList() };

    public static ProcessSnapshot Process(params ThreadSnapshot[] threads) => new() { Source = "test", Threads = threads };
}
