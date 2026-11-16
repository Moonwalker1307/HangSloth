using System.Diagnostics.Tracing;

namespace HangApp;

/// Listens to the runtime's own JIT events to learn when InvoiceCheck.Validate has been compiled with
/// optimizations: at tier 1 normally, or straight away when tiered compilation is switched off.
sealed class OptimizedCodeListener : EventListener
{
    private const EventKeywords JitKeyword = (EventKeywords)0x10;
    private volatile bool _validateIsOptimized;

    public bool ValidateIsOptimized => _validateIsOptimized;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, JitKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal)) return;
        if (e.PayloadNames is not { } names || e.Payload is not { } payload) return;
        if (Value("MethodNamespace") as string != "HangApp.InvoiceCheck" || Value("MethodName") as string != "Validate") return;
        if (Value("MethodFlags") is not { } flags) return;

        // Bits 7 to 9 of MethodFlags say how the code was compiled: 2 is optimized (no tiering), 4 is tier 1.
        var tier = (Convert.ToUInt32(flags) >> 7) & 7;
        if (tier is 2 or 4) _validateIsOptimized = true;

        object? Value(string name)
        {
            var index = names.IndexOf(name);
            return index >= 0 && index < payload.Count ? payload[index] : null;
        }
    }
}
