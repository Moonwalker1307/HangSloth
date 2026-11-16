using HangSloth.Analysis;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

/// async Main and top-level await: the compiler's entry point blocks the main thread on Main's task for as long as
/// the app runs. That wait is how every async console app and host looks, so it is no finding by itself; what Main
/// waits for further down the app's code still is. Plus a generic host whose hosted service never finishes starting.
public class AsyncMainTests
{
    /// The frames of the compiler-generated "&lt;Main&gt;", which has no source line of its own.
    private static readonly StackFrameInfo[] EntryPointWait =
    [
        .. ResultWait[..4],
        Fx("System.Threading.Tasks.Task", "InternalWait"),
        Fx("System.Runtime.CompilerServices.TaskAwaiter", "HandleNonSuccessAndDebuggerNotification"),
        new() { TypeName = "Program", MethodName = "<Main>", ModuleName = "/app/MyApp.dll" },
    ];

    private static readonly TaskInfo StopSignal =
        new() { Address = 0x900, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.Promise, Description = "a TaskCompletionSource task" };

    private static TaskInfo AsyncMain(TaskInfo awaiting, int line = 12) => new()
    {
        Address = 0x500, TypeName = "AsyncTaskMethodBuilder+AsyncStateMachineBox<Program+<<Main>$>d__0>", Kind = TaskKind.AsyncMethod,
        Description = "Program.<<Main>$>d__0.", AwaitLocation = new SourceLocation("/src/Program.cs", line), Awaiting = awaiting,
    };

    private static TaskInfo AppMethod(string type, string name, string file, int line, TaskInfo awaiting, ulong address = 0x600) => new()
    {
        Address = address, TypeName = $"AsyncTaskMethodBuilder+AsyncStateMachineBox<{type}+<{name}>d__1>", Kind = TaskKind.AsyncMethod,
        Description = $"{Names.ShortType(type)}.{name}", AwaitLocation = new SourceLocation($"/src/{file}", line), Awaiting = awaiting,
    };

    private static ThreadSnapshot MainThread(TaskInfo main) => Thread(1, EntryPointWait) with { BlockedOnTask = main };

    private static List<Finding> Detect(ProcessSnapshot snapshot) => new BlockedOnTaskDetector().Detect(snapshot, new HashSet<int>()).ToList();

    [Fact]
    public void Main_awaiting_its_stop_signal_is_not_a_finding()
    {
        Assert.Empty(Detect(Process(MainThread(AsyncMain(StopSignal)))));
    }

    [Fact]
    public void Main_awaiting_a_delay_is_not_a_finding()
    {
        var delay = new TaskInfo { Address = 0x900, TypeName = "System.Threading.Tasks.Task+DelayPromise", Kind = TaskKind.Delay, Description = "Task.Delay(...)" };
        Assert.Empty(Detect(Process(MainThread(AsyncMain(delay)))));
    }

    [Fact]
    public void Main_awaiting_its_stop_signal_through_WaitAsync_is_not_a_finding()
    {
        var waitAsync = new TaskInfo
        {
            Address = 0x700, TypeName = "System.Threading.Tasks.Task+CancellationPromise<System.Threading.Tasks.VoidTaskResult>",
            Kind = TaskKind.Wrapper, Description = "WaitAsync(...)", Awaiting = StopSignal,
        };
        Assert.Empty(Detect(Process(MainThread(AsyncMain(waitAsync)))));
    }

    [Fact]
    public void A_task_nobody_completes_further_down_the_apps_code_keeps_its_warning()
    {
        // The same hang as HangApp's never-completes, only with an async Main: the verdict must not depend on that.
        var import = AppMethod("MyApp.NightlyImport", "RunAsync", "NightlyImport.cs", 21, StopSignal with { Description = "a TaskCompletionSource<bool> task" });

        var f = Assert.Single(Detect(Process(MainThread(AsyncMain(import)))));

        Assert.Equal("blocked-on-task", f.Id);
        Assert.Equal("Waiting for a task nobody completes", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is waiting for the app's async Main, which is paused at Program.cs:12, awaiting NightlyImport.RunAsync (paused at NightlyImport.cs:21), " +
                     "which awaits a TaskCompletionSource<bool> task that nothing has completed yet.", f.Summary);
        Assert.Equal("If this is how the app waits to be told to stop, waiting here for as long as it runs is normal. If the app should have moved on by now, this is where it is stuck.", f.Details[^1]);
        Assert.DoesNotContain("Prefer await", f.Advice);
        Assert.Equal("Program.cs:12", f.Location?.ToString());
    }

    [Fact]
    public void A_SemaphoreSlim_nobody_releases_further_down_the_apps_code_keeps_its_warning()
    {
        var node = new TaskInfo { Address = 0x800, TypeName = "System.Threading.SemaphoreSlim+TaskNode", Kind = TaskKind.SemaphoreWait, Description = "SemaphoreSlim.WaitAsync()" };
        var pass = AppMethod("MyApp.Gate", "PassAsync", "Gate.cs", 8, node);

        var f = Assert.Single(Detect(Process(MainThread(AsyncMain(pass)))));

        Assert.Equal("semaphore-wait", f.Id);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is waiting for the app's async Main, which is paused at Program.cs:12, awaiting Gate.PassAsync (paused at Gate.cs:8), " +
                     "which is waiting to enter a SemaphoreSlim (SemaphoreSlim.WaitAsync()).", f.Summary);
        Assert.Equal("If this is how the app waits to be told to stop, waiting here for as long as it runs is normal. If the app should have moved on by now, this is where it is stuck.", f.Details[^1]);
    }

    [Fact]
    public void A_task_that_was_never_started_is_a_hang_even_behind_async_Main()
    {
        var never = new TaskInfo { Address = 0x900, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.NotStarted, Description = "new Task(Exporter.Write)" };

        var f = Assert.Single(Detect(Process(MainThread(AsyncMain(never)))));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is waiting for the app's async Main, which is paused at Program.cs:12, awaiting new Task(Exporter.Write), which was never started.", f.Summary);
    }

    [Fact]
    public void The_same_wait_outside_async_Main_keeps_its_warning()
    {
        // A synchronous .Wait() in the app's own code (HangApp's never-completes): the app wrote that wait.
        var import = AppMethod("MyApp.NightlyImport", "RunAsync", "NightlyImport.cs", 21, StopSignal);
        var thread = Thread(1, [.. ResultWait, App("MyApp.Program", "Main", "Program.cs", 5)]) with { BlockedOnTask = import };

        Assert.Equal(Severity.Warning, Assert.Single(Detect(Process(thread))).Severity);
    }

    [Fact]
    public void The_main_thread_is_described_by_where_Main_is_paused()
    {
        Assert.Equal("waiting for the app's async Main, which is paused at Program.cs:12", Describe.Activity(MainThread(AsyncMain(StopSignal))));
        Assert.Equal("waiting for the app's async Main to finish", Describe.Activity(Thread(1, EntryPointWait)));
    }

    // ---------------------------------------------------------------- the generic host

    private static TaskInfo HostTask(string type, string description, TaskInfo awaiting, ulong address) => new()
    {
        Address = address, Kind = TaskKind.AsyncMethod, Description = description, Awaiting = awaiting,
        TypeName = $"System.Runtime.CompilerServices.AsyncTaskMethodBuilder<System.Threading.Tasks.VoidTaskResult>+AsyncStateMachineBox<{type}>",
    };

    /// RunAsync -> Host.StartAsync -> BrokerConnection.StartAsync -> a TaskCompletionSource nobody completes.
    private static TaskInfo StuckStart()
    {
        var start = AppMethod("MyApp.BrokerConnection", "StartAsync", "BrokerConnection.cs", 14, StopSignal, address: 0x610);
        var hostStart = HostTask("Microsoft.Extensions.Hosting.Internal.Host+<StartAsync>d__14", "Host.StartAsync", start, 0x620);
        return HostTask("Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions+<RunAsync>d__4", "HostingAbstractionsHostExtensions.RunAsync", hostStart, 0x630);
    }

    private static TaskInfo Running()
    {
        var shutdown = HostTask("Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions+<WaitForShutdownAsync>d__5", "HostingAbstractionsHostExtensions.WaitForShutdownAsync", StopSignal, 0x620);
        return HostTask("Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions+<RunAsync>d__4", "HostingAbstractionsHostExtensions.RunAsync", shutdown, 0x630);
    }

    private static ThreadSnapshot HostRunThread(TaskInfo run) => Thread(1,
    [
        .. ResultWait[..4],
        Fx("System.Runtime.CompilerServices.TaskAwaiter", "HandleNonSuccessAndDebuggerNotification"),
        new() { TypeName = "Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions", MethodName = "Run", ModuleName = "/usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.0/Microsoft.Extensions.Hosting.Abstractions.dll" },
        App("Program", "<Main>$", "Program.cs", 9),
    ]) with { BlockedOnTask = run };

    [Fact]
    public void Host_Run_stuck_in_a_hosted_services_StartAsync_is_reported()
    {
        var f = Assert.Single(Detect(Process(HostRunThread(StuckStart()))));

        Assert.Equal("Waiting for a task nobody completes", f.Title);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is blocked in .Run() at Program.cs:9, waiting for HostingAbstractionsHostExtensions.RunAsync, which awaits Host.StartAsync, " +
                     "which awaits BrokerConnection.StartAsync (paused at BrokerConnection.cs:14), which awaits a TaskCompletionSource task that nothing has completed yet.", f.Summary);
        Assert.Contains("The generic host is still starting: Host.StartAsync waits for every hosted service's StartAsync to return, and until they all have, the app has not finished starting.", f.Details);
    }

    [Fact]
    public void Awaiting_RunAsync_stuck_in_a_hosted_services_StartAsync_is_reported()
    {
        var f = Assert.Single(Detect(Process(MainThread(AsyncMain(StuckStart())))));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Thread 1 is waiting for the app's async Main, which is paused at Program.cs:12, awaiting HostingAbstractionsHostExtensions.RunAsync, which awaits Host.StartAsync, " +
                     "which awaits BrokerConnection.StartAsync (paused at BrokerConnection.cs:14), which awaits a TaskCompletionSource task that nothing has completed yet.", f.Summary);
        Assert.DoesNotContain("If this is how the app waits to be told to stop, waiting here for as long as it runs is normal. If the app should have moved on by now, this is where it is stuck.", f.Details);
    }

    [Fact]
    public void A_host_stuck_starting_on_a_slow_call_is_a_warning_with_startup_advice()
    {
        // A retry loop with Task.Delay in StartAsync: the host isn't serving yet, whatever it waits for.
        var delay = new TaskInfo { Address = 0x900, TypeName = "System.Threading.Tasks.Task+DelayPromise", Kind = TaskKind.Delay, Description = "Task.Delay(...)" };
        var start = AppMethod("MyApp.CacheWarmup", "StartAsync", "CacheWarmup.cs", 20, delay, address: 0x610);
        var run = HostTask("Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions+<RunAsync>d__4", "HostingAbstractionsHostExtensions.RunAsync",
            HostTask("Microsoft.Extensions.Hosting.Internal.Host+<StartAsync>d__14", "Host.StartAsync", start, 0x620), 0x630);

        var f = Assert.Single(Detect(Process(HostRunThread(run))));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("The host hasn't finished starting", f.Title);
        Assert.Equal("Thread 1 is blocked in .Run() at Program.cs:9, waiting for HostingAbstractionsHostExtensions.RunAsync, which awaits Host.StartAsync, " +
                     "which awaits CacheWarmup.StartAsync (paused at CacheWarmup.cs:20), which awaits a Task.Delay that hasn't elapsed yet.", f.Summary);
        Assert.Contains("The generic host is still starting: Host.StartAsync waits for every hosted service's StartAsync to return, and until they all have, the app has not finished starting.", f.Details);
        Assert.Equal("Don't wait for long-running work in StartAsync: start it in the background (BackgroundService.ExecuteAsync), or give the wait a timeout.", f.Advice);
    }

    [Fact]
    public void A_host_stuck_starting_on_a_task_still_running_is_a_warning()
    {
        var work = new TaskInfo { Address = 0x900, TypeName = "System.Threading.Tasks.Task", Kind = TaskKind.Delegate, Description = "Task.Run(SchemaMigration.Apply)" };
        var start = AppMethod("MyApp.SchemaMigration", "StartAsync", "SchemaMigration.cs", 11, work, address: 0x610);
        var run = HostTask("Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions+<RunAsync>d__4", "HostingAbstractionsHostExtensions.RunAsync",
            HostTask("Microsoft.Extensions.Hosting.Internal.Host+<StartAsync>d__14", "Host.StartAsync", start, 0x620), 0x630);

        var f = Assert.Single(Detect(Process(MainThread(AsyncMain(run)))));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("The host hasn't finished starting", f.Title);
        Assert.EndsWith("which awaits Task.Run(SchemaMigration.Apply), which is still running.", f.Summary);
    }

    [Fact]
    public void A_running_host_is_not_reported()
    {
        Assert.Empty(Detect(Process(HostRunThread(Running()))));
        Assert.Empty(Detect(Process(MainThread(AsyncMain(Running())))));
    }
}
