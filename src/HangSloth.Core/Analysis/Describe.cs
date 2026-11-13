using HangSloth.Model;

namespace HangSloth.Analysis;

/// Small phrasing helpers shared by the detectors.
internal static class Describe
{
    public static string Where(StackFrameInfo? frame)
    {
        if (frame is null) return "an unknown place";
        var method = Names.ShortMethod(frame.TypeName, frame.MethodName);
        return frame.Source is null ? method : $"{method} ({frame.Source})";
    }

    /// "OrderService.cs:88", or the method name when there's no PDB.
    public static string Spot(StackFrameInfo? frame) =>
        frame?.Source?.ToString() ?? (frame is null ? "unknown code" : Names.ShortMethod(frame.TypeName, frame.MethodName));

    public static string Task(TaskInfo task) =>
        task.AwaitLocation is null ? task.Description : $"{task.Description} (paused at {task.AwaitLocation})";

    public static string Activity(ThreadSnapshot thread)
    {
        var kind = FrameClassifier.Classify(thread.Frames);
        var at = Spot(FrameClassifier.BestUserFrame(thread.Frames));
        return kind switch
        {
            BlockingKind.Sleep => $"sleeping (Thread.Sleep) in {at}",
            BlockingKind.TaskWait => $"blocked on {FrameClassifier.DescribeTaskWait(thread.Frames)} in {at}",
            BlockingKind.MonitorEnter => $"itself waiting for another lock in {at}",
            BlockingKind.MonitorWait => $"waiting in Monitor.Wait in {at}",
            BlockingKind.Join => $"waiting for another thread to finish (Thread.Join) in {at}",
            BlockingKind.SemaphoreWait => $"waiting on a SemaphoreSlim in {at}",
            BlockingKind.WaitHandle => $"waiting on a WaitHandle in {at}",
            _ => $"running {at}",
        };
    }

    public static string Plural(int n, string singular, string? plural = null) => n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";

    public static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => string.Join(", ", list.Take(list.Count - 1)) + $" and {list[^1]}",
        };
    }

    /// Walk an async chain from the waited task to the innermost thing it is waiting for.
    public static IEnumerable<TaskInfo> Chain(TaskInfo task)
    {
        for (var t = task; t is not null; t = t.Awaiting)
            yield return t;
    }
}
