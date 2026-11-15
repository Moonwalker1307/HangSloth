namespace DeadlockLab.Code;

// Lazy<T> (ExecutionAndPublication, the default) runs the factory under a lock.
// The factory parallelizes loading with Task.Run, and one section needs... the catalog.

public sealed class CatalogService
{
    private readonly Lazy<Catalog> _catalog;

    public CatalogService() => _catalog = new Lazy<Catalog>(Load);

    public Catalog Catalog => _catalog.Value;

    private Catalog Load()
    {
        var sections = Task.Run(LoadRecommendations);
        sections.Wait();
        return new Catalog(sections.Result);
    }

    private string[] LoadRecommendations()
    {
        // "Recommendations are just the catalog's top items" -- reads the catalog being built.
        return Catalog.TopItems.Take(3).ToArray();
    }
}

public sealed record Catalog(string[] TopItems);

internal static class CatalogScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        Console.WriteLine(new CatalogService().Catalog.TopItems.Length);
    }
}
