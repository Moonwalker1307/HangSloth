namespace DeadlockLab.Code;

// TaskCompletionSource.SetResult runs awaiting continuations right there, on the calling thread,
// unless the TCS was created with TaskCreationOptions.RunContinuationsAsynchronously.

public sealed class OrderPipeline
{
    private readonly object _pipelineLock = new();
    private TaskCompletionSource<string> _paid = new();
    internal readonly object ShippingLock = new();

    public Task<string> WhenPaid => _paid.Task;

    /// Called by the payment webhook thread.
    public void MarkPaid(string orderId)
    {
        lock (_pipelineLock)
        {
            // The awaiting ShipWhenPaidAsync resumes *inside* this call, still under _pipelineLock.
            _paid.SetResult(orderId);
        }
    }

    public async Task ShipWhenPaidAsync()
    {
        var order = await WhenPaid;
        lock (ShippingLock) // runs on the webhook thread, which holds _pipelineLock
        {
            Console.WriteLine($"shipping {order}");
        }
    }

    /// The nightly report: shipping first, then the pipeline.
    public void Report(Action whileHoldingShipping)
    {
        lock (ShippingLock)
        {
            whileHoldingShipping();
            lock (_pipelineLock) Console.WriteLine("report");
        }
    }
}

internal static class InlineContinuationScenario
{
    public static void Run()
    {
        var pipeline = new OrderPipeline();
        _ = pipeline.ShipWhenPaidAsync();

        using var reportHasShipping = new ManualResetEventSlim();
        new Thread(() => pipeline.Report(() => { reportHasShipping.Set(); Thread.Sleep(300); })) { Name = "nightly-report" }.Start();
        reportHasShipping.Wait();
        new Thread(() => pipeline.MarkPaid("order-7")) { Name = "payment-webhook" }.Start();

        Ready.SignalLater();
        Ready.Forever();
    }
}
