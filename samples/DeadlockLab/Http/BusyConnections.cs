namespace DeadlockLab.Http;

/// A storefront page keeps the product response in a local while it fetches the reviews, like the
/// http-connection-limit scenario, but reads it with the default HttpCompletionOption: the body is
/// already buffered and the connection is back in the pool. The page waits because two slow report
/// downloads use both allowed connections, not because of its own response.
public sealed class StorefrontPage(HttpClient http)
{
    public async Task<string> RenderAsync(int id)
    {
        var product = await http.GetAsync($"products/{id}"); // buffered: holds no connection
        await Task.Delay(1000); // "rendering the header"
        var reviews = await http.GetStringAsync($"reviews/{id}");
        return $"{product.StatusCode} {reviews}";
    }
}

internal static class HttpBusyConnectionsScenario
{
    public static void Run()
    {
        var server = new LabServer(LabServer.Mode.SlowReports);
        var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 2 };
        var http = new HttpClient(handler) { BaseAddress = server.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        var page = new StorefrontPage(http).RenderAsync(1);
        server.Answered.Wait();
        // Two downloads the server never finishes take both connections while the page renders.
        var reports = new[] { http.GetStringAsync("reports/sales"), http.GetStringAsync("reports/stock") };
        Ready.SignalLater(2000);
        page.Wait();
        GC.KeepAlive(reports);
    }
}
