namespace DeadlockLab.Code;

/// Parallel.ForEach looks like a loop, but its body runs on other threads.
public sealed class ThumbnailService
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, int> _sizes = [];

    public void RebuildAll(IReadOnlyList<string> images)
    {
        lock (_cacheLock)
        {
            _sizes.Clear();
            // BUG: the body takes _cacheLock too. The calling thread holds it and waits for the
            // other workers, which can't get it.
            Parallel.ForEach(images, new ParallelOptions { MaxDegreeOfParallelism = 4 }, image =>
            {
                Thread.Sleep(50);
                lock (_cacheLock) _sizes[image] = image.Length;
            });
        }
    }
}

internal static class ParallelScenario
{
    public static void Run()
    {
        Ready.SignalLater(1200);
        new ThumbnailService().RebuildAll(Enumerable.Range(0, 8).Select(i => $"img-{i}.png").ToList());
    }
}
