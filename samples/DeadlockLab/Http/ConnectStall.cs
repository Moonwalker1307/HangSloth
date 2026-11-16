namespace DeadlockLab.Http;

/// A supplier's price API over HTTPS. The supplier's server accepts the TCP connection and never
/// answers the TLS handshake (a stuck load balancer, a proxy that swallows traffic). The default
/// handler has no ConnectTimeout, so the connection is never established and never fails.
public sealed class SupplierPrices(HttpClient http)
{
    public string GetPrice(string sku) => http.GetStringAsync($"prices/{sku}").Result; // BUG: no connect timeout
}

internal static class HttpConnectStallScenario
{
    public static void Run()
    {
        var server = new LabServer(LabServer.Mode.Silent);
        var prices = new SupplierPrices(new HttpClient { BaseAddress = new Uri($"https://127.0.0.1:{server.Port}/"), Timeout = Timeout.InfiniteTimeSpan });
        Ready.SignalLater(1000);
        prices.GetPrice("A-1");
    }
}
