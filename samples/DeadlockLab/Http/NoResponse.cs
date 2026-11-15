namespace DeadlockLab.Http;

/// A payment gateway client with no timeout, called with .Result from synchronous code. The
/// gateway accepted the connection and never answered, so checkout waits forever.
public sealed class PaymentGateway(HttpClient http)
{
    public bool Charge(decimal amount)
    {
        var response = http.PostAsync("charges", new StringContent($"{{\"amount\":{amount}}}")).Result; // BUG: no timeout, blocks forever
        return response.IsSuccessStatusCode;
    }
}

internal static class HttpNoResponseScenario
{
    public static void Run()
    {
        var server = new LabServer(LabServer.Mode.Silent);
        // Timeout.InfiniteTimeSpan: "the gateway is slow sometimes, so we turned the timeout off".
        var gateway = new PaymentGateway(new HttpClient { BaseAddress = server.BaseAddress, Timeout = Timeout.InfiniteTimeSpan });
        Ready.SignalLater(1000);
        gateway.Charge(42m);
    }
}
