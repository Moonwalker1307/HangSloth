namespace DeadlockLab.Http;

/// A typed HTTP client with a connection limit per server. Fetching a product page reads the
/// product's headers, and before reading (or disposing) that response, fetches its reviews from
/// the same server. The first response keeps its connection busy, so with as many concurrent
/// pages as the limit allows, every connection is held by a request waiting for another one.
public sealed class CatalogApi(HttpClient http)
{
    public async Task<string> GetProductPageAsync(int id)
    {
        var product = await http.GetAsync($"products/{id}", HttpCompletionOption.ResponseHeadersRead); // BUG: never disposed before the next call
        var reviews = await http.GetStringAsync($"reviews/{id}");
        return $"{product.StatusCode} {reviews}";
    }
}

internal static class HttpConnectionLimitScenario
{
    public static void Run()
    {
        var server = new LabServer(LabServer.Mode.StallBody);
        var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 2 };
        var api = new CatalogApi(new HttpClient(handler) { BaseAddress = server.BaseAddress, Timeout = Timeout.InfiniteTimeSpan });
        var pages = Enumerable.Range(1, 2).Select(api.GetProductPageAsync).ToArray();
        Ready.SignalLater(1500);
        Task.WaitAll(pages);
    }
}
