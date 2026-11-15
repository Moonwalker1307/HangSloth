namespace HangApp;

/// A synchronous validator that blocks on an async lookup, called for many invoices at once.
/// Once the JIT recompiles it with optimizations (tier 1), Task<T>.Result is inlined into Validate,
/// which used to make HangSloth report the line of the statement before the .Result, and call it .Wait().
public sealed class InvoiceCheck
{
    private readonly CustomerDirectory _customers;

    public InvoiceCheck(CustomerDirectory customers) => _customers = customers;

    public int CheckAll(IEnumerable<int> invoiceIds) =>
        invoiceIds.Aggregate(0, (errors, id) => errors + Validate(id));
    // Kept out of line, like a real validator too big to inline, so that only Task<T>.Result gets inlined into it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] public int Validate(int invoiceId)
    {
        if (invoiceId <= 0) throw new ArgumentOutOfRangeException(nameof(invoiceId));
        var reference = $"invoice-{invoiceId}";
        var line = new LineItem(
            invoiceId,
            reference.ToUpperInvariant(),
            invoiceId * 10m);
        var customers = _customers.LoadCustomersAsync(line).Result;
        var errors = 0;
        foreach (var customer in customers)
        {
            if (string.IsNullOrWhiteSpace(customer)) errors++;
            else if (!customer.StartsWith("INVOICE-", StringComparison.Ordinal)) errors += 2;
        }
        return customers.Count == 0 ? errors + 1 : errors;
    }
}

public sealed record LineItem(int InvoiceId, string Reference, decimal Amount);

public sealed class CustomerDirectory
{
    /// While warming up, answers immediately; afterwards waits for an upstream service that never answers.
    public volatile bool Upstream;
    private readonly TaskCompletionSource<List<string>> _never = new();

    public Task<List<string>> LoadCustomersAsync(LineItem line) =>
        Upstream ? WaitForUpstreamAsync(line) : Task.FromResult(new List<string> { line.Reference });

    private async Task<List<string>> WaitForUpstreamAsync(LineItem line)
    {
        await Task.Yield();
        var result = await _never.Task;
        result.Add(line.Reference);
        return result;
    }
}
