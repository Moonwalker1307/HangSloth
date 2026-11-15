using HangSloth.Analysis;
using HangSloth.Collection;
using HangSloth.Model;
using static HangSloth.Tests.SnapshotFactory;

namespace HangSloth.Tests;

public class NamesTests
{
    [Theory]
    [InlineData("HangApp.OrderService+<GetOrderAsync>d__0", "OrderService.GetOrderAsync")]
    [InlineData("A.B.Outer+Inner+<RunAsync>d__12", "Outer.Inner.RunAsync")]
    [InlineData("A.Generic`1+<DoAsync>d__3<System.Int32>", "Generic`1.DoAsync")]
    [InlineData("System.Net.Http.HttpClient+<<SendAsync>g__Core|83_0>d", "HttpClient.SendAsync")]
    [InlineData("MyApp.Jobs+<>c__DisplayClass0_0+<<Run>b__0>d", "Jobs.Run (lambda)")]
    public void State_machine_names_become_method_names(string typeName, string expected) =>
        Assert.Equal(expected, Names.ShortMethod(typeName, ""));

    [Theory]
    [InlineData("HangApp.Scenario+<>c", "<Run>b__0_0", "Scenario.Run (lambda)")]
    [InlineData("HangApp.Scenario+<>c__DisplayClass2_0", "<Run>b__1", "Scenario.Run (lambda)")]
    [InlineData("System.Linq.Enumerable", "ToDictionary[[System.Collections.Generic.KeyValuePair`2[[System.__Canon, System.Private.CoreLib]], System.Private.CoreLib]]", "Enumerable.ToDictionary")]
    [InlineData("HangApp.OrderController", "Get", "OrderController.Get")]
    public void Lambdas_are_named_after_their_method(string type, string method, string expected) =>
        Assert.Equal(expected, Names.ShortMethod(type, method));

    [Theory]
    [InlineData("System.Threading.Monitor", "<Wait>g____PInvoke|24_0", "Monitor.Wait")]
    [InlineData("MyApp.Worker", "<Run>g__Drain|3_0", "Worker.Run.Drain")]
    [InlineData("", "lambda_method42", "lambda_method")]
    public void Generated_method_names_are_cleaned_up(string type, string method, string expected) =>
        Assert.Equal(expected, Names.ShortMethod(type, method));

    [Theory]
    [InlineData("System.Threading.Tasks.Task<HangApp.Order>", "Task<Order>")]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Int32>", "Dictionary<string, int>")]
    [InlineData("HangApp.Scenario+Gate", "Scenario.Gate")]
    public void Types_are_shortened(string typeName, string expected) => Assert.Equal(expected, Names.ShortType(typeName));

    [Fact]
    public void Generic_argument_respects_nesting() =>
        Assert.Equal("HangApp.OrderService+<GetOrderAsync>d__0",
            SnapshotBuilder.GenericArgument("System.Runtime.CompilerServices.AsyncTaskMethodBuilder<HangApp.Order>+AsyncStateMachineBox<HangApp.OrderService+<GetOrderAsync>d__0>", "AsyncStateMachineBox<"));
}

public class FrameClassifierTests
{
    [Fact]
    public void Result_wait_is_task_wait()
    {
        var frames = (StackFrameInfo[])[.. ResultWait, App("A.B", "C", "B.cs", 1)];
        Assert.Equal(BlockingKind.TaskWait, FrameClassifier.Classify(frames));
        Assert.Equal(".Result", FrameClassifier.DescribeTaskWait(frames));
    }

    [Fact]
    public void GetAwaiter_GetResult_is_named_as_such()
    {
        StackFrameInfo[] frames =
        [
            Fx("System.Threading.Tasks.Task", "InternalWaitCore"),
            Fx("System.Runtime.CompilerServices.TaskAwaiter", "HandleNonSuccessAndDebuggerNotification"),
            Fx("System.Runtime.CompilerServices.TaskAwaiter`1", "GetResult"),
            App("A.B", "C", "B.cs", 1),
        ];
        Assert.Equal(".GetAwaiter().GetResult()", FrameClassifier.DescribeTaskWait(frames));
    }

    [Fact]
    public void Idle_pool_worker_is_idle()
    {
        StackFrameInfo[] frames =
        [
            Fx("System.Threading.LowLevelLifoSemaphore", "WaitNative"),
            Fx("System.Threading.LowLevelLifoSemaphore", "Wait"),
            Fx("System.Threading.PortableThreadPool+WorkerThread", "WorkerThreadStart"),
        ];
        Assert.Equal(BlockingKind.Idle, FrameClassifier.Classify(frames));
    }

    [Fact]
    public void Main_thread_parked_in_host_Run_is_idle()
    {
        // What app.Run() looks like in every ASP.NET Core app's dump.
        StackFrameInfo[] frames =
        [
            .. ResultWait[..4],
            Fx("System.Runtime.CompilerServices.TaskAwaiter", "HandleNonSuccessAndDebuggerNotification"),
            new() { TypeName = "Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions", MethodName = "Run", ModuleName = "/usr/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.0/Microsoft.Extensions.Hosting.Abstractions.dll" },
            App("Program", "<Main>$", "Program.cs", 10),
        ];
        Assert.Equal(BlockingKind.Idle, FrameClassifier.Classify(frames));
    }

    [Fact]
    public void Lock_enter_on_net9_lock_type_is_monitor_enter()
    {
        StackFrameInfo[] frames = [Fx("System.Threading.WaitHandle", "WaitOneNoCheck"), Fx("System.Threading.Lock", "TryEnterSlow"), Fx("System.Threading.Lock", "EnterScope"), App("A.B", "C", "B.cs", 1)];
        Assert.Equal(BlockingKind.MonitorEnter, FrameClassifier.Classify(frames));
    }

    [Theory]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/System.Private.CoreLib.dll", true)]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.AspNetCore.App\10.0.0\Microsoft.AspNetCore.dll", true)]
    [InlineData("/app/Newtonsoft.Json.dll", false)]
    [InlineData("/app/MyCompany.Orders.dll", false)]
    public void Framework_modules_are_recognised(string path, bool expected) => Assert.Equal(expected, FrameClassifier.IsFrameworkModule(path));
}
