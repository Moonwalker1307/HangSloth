// Top-level statements, the way `dotnet new console` writes a program, with local functions and lambdas whose
// compiler names must never reach a report. One wait per mode; each prints READY once it is stuck.
using System.Collections.Concurrent;
using System.Diagnostics;

var mode = args.Length > 0 ? args[0] : "";
var pending = new TaskCompletionSource<int>();
Ready.After(1500);

switch (mode)
{
    case "local-async":
        await ImportAsync();
        break;
    case "local-result":
        Console.WriteLine(LoadTotal());
        break;
    case "lambda-deadlock":
        var orders = new object();
        var stock = new object();
        new Thread(() => { lock (orders) { Thread.Sleep(300); lock (stock) { } } }) { IsBackground = true, Name = "place-order" }.Start();
        new Thread(() => ReturnItem(orders, stock)) { IsBackground = true, Name = "return-item" }.Start();
        Thread.Sleep(Timeout.Infinite);
        break;
    case "lock-this": Ledgers.Run(); break;
    case "lazy-cycle": new RateTables().Run(); break;
    case "same-wait": Imports.Run(); break;
    case "lock-owner-task": PriceBook.Run(); break;
    case "delay-then-lock": Settlement.Run(); break;
    case "process-grandchild": Shell.Run(); break;
    default:
        Console.Error.WriteLine($"unknown mode {mode}");
        return 1;
}
return 0;

async Task ImportAsync() => Console.WriteLine(await pending.Task);

int LoadTotal() => SumAsync().Result;

async Task<int> SumAsync() => await pending.Task + 1;

static void ReturnItem(object orders, object stock)
{
    lock (stock) { Thread.Sleep(300); lock (orders) { } }
}

static class Ready
{
    public static void After(int milliseconds) =>
        new Thread(() => { Thread.Sleep(milliseconds); Console.WriteLine("READY"); }) { IsBackground = true }.Start();
}

/// Two ledgers that lock themselves (lock (this)) and then the other one, from two threads at once. Nothing holds
/// them in a field, so only the ledgers' own ids tell the two locks apart.
sealed class Ledger(string id)
{
    public string Id => id;

    public void MoveTo(Ledger other, Barrier bothHoldTheirOwn)
    {
        lock (this)
        {
            bothHoldTheirOwn.SignalAndWait();
            lock (other) { }
        }
    }
}

static class Ledgers
{
    private static readonly Barrier BothHoldTheirOwn = new(2);

    public static void Run()
    {
        var cash = new Ledger("cash");
        var bank = new Ledger("bank");
        new Thread(Move) { IsBackground = true, Name = "cash->bank" }.Start((cash, bank));
        new Thread(Move) { IsBackground = true, Name = "bank->cash" }.Start((bank, cash));
        Thread.Sleep(Timeout.Infinite);
    }

    private static void Move(object? state)
    {
        var (from, to) = ((Ledger, Ledger))state!;
        from.MoveTo(to, BothHoldTheirOwn);
    }
}

/// ConcurrentDictionary.GetOrAdd with Lazy values whose factories need each other, built on two threads at once.
sealed class RateTables
{
    private readonly ConcurrentDictionary<string, Lazy<RateTable>> _tables = new();
    private readonly Barrier _bothInside = new(2);

    public void Run()
    {
        new Thread(() => Get("retail")) { IsBackground = true, Name = "retail-rates" }.Start();
        new Thread(() => Get("wholesale")) { IsBackground = true, Name = "wholesale-rates" }.Start();
        Thread.Sleep(Timeout.Infinite);
    }

    private RateTable Get(string name) =>
        _tables.GetOrAdd(name, n => new Lazy<RateTable>(() => Build(n))).Value;

    private RateTable Build(string name)
    {
        _bothInside.SignalAndWait(TimeSpan.FromSeconds(2));
        // Retail rates are derived from wholesale ones, and wholesale discounts from retail ones.
        var other = Get(name == "retail" ? "wholesale" : "retail");
        return new RateTable(name, other.Name);
    }
}

sealed record RateTable(string Name, string BasedOn);

/// Thirty workers blocked at the same line on the same kind of task, and two packers blocked at one line on two
/// different semaphores that look alike: one finding for the thirty, one for each packer.
static class Imports
{
    private static readonly TaskCompletionSource<string> Feed = new();

    public static void Run()
    {
        for (var i = 0; i < 30; i++)
            new Thread(() => Console.WriteLine(FetchAsync().GetAwaiter().GetResult())) { IsBackground = true, Name = "import-worker" }.Start();
        var first = new Bay("returns");
        var second = new Bay("returns");
        first.Gate.Wait();
        second.Gate.Wait();
        foreach (var bay in new[] { first, second })
            new Thread(b => PackAsync((Bay)b!).GetAwaiter().GetResult()) { IsBackground = true, Name = "packer" }.Start(bay);
        Thread.Sleep(Timeout.Infinite);
    }

    private static async Task<string> FetchAsync()
    {
        var text = await Feed.Task;
        return text.Trim();
    }

    private static async Task PackAsync(Bay bay)
    {
        await bay.Gate.WaitAsync();
        bay.Gate.Release();
    }
}

sealed class Bay(string name)
{
    public readonly SemaphoreSlim Gate = new(1, 1);
    public string Name => name;
}

/// A rebuild holds the price book's lock while it blocks on a supplier call that never completes; two readers queue
/// behind the lock. The lock contention names the rebuild; what the rebuild waits for is a finding of its own.
static class PriceBook
{
    private static readonly object Sync = new();
    private static readonly TaskCompletionSource<int> Supplier = new();

    public static void Run()
    {
        new Thread(Rebuild) { IsBackground = true, Name = "rebuilder" }.Start();
        Thread.Sleep(300);
        for (var i = 0; i < 2; i++)
            new Thread(Read) { IsBackground = true, Name = "reader" }.Start();
        Thread.Sleep(Timeout.Infinite);
    }

    private static void Rebuild()
    {
        lock (Sync)
            Console.WriteLine(LoadFromSupplierAsync().Result);
    }

    private static void Read()
    {
        lock (Sync)
            Console.WriteLine("read");
    }

    private static async Task<int> LoadFromSupplierAsync() => await Supplier.Task;
}

/// The day closes after a Task.Delay: the rest of the async method runs on the timer's thread and waits there for the
/// books' lock, which the main thread holds. That is the continuation of a delay, not a timer callback of the app.
static class Settlement
{
    private static readonly object Books = new();

    public static void Run()
    {
        _ = CloseDayAsync();
        lock (Books)
            Thread.Sleep(Timeout.Infinite);
    }

    private static async Task CloseDayAsync()
    {
        await Task.Delay(300);
        lock (Books)
            Console.WriteLine("closed");
    }
}

/// Process.WaitForExit() with the output read in the background: the shell exits at once, but the sleep it started in
/// the background keeps the pipe open, so WaitForExit() waits for the end of the output.
static class Shell
{
    public static void Run()
    {
        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("sleep 120 & echo started");
        using var process = Process.Start(info)!;
        process.OutputDataReceived += (_, e) => GC.KeepAlive(e.Data);
        process.ErrorDataReceived += (_, e) => GC.KeepAlive(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        Console.WriteLine($"exit {process.ExitCode}");
    }
}
