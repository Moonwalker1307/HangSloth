// DriverVersions: the same database hangs, built against an older or newer driver, so HangSloth can be
// checked against the internals of each version (pool types and field names change between versions).
//
//   dotnet build samples/DriverVersions -p:NpgsqlVersion=6.0.13 -o out/npgsql-6
//   dotnet out/npgsql-6/DriverVersions.dll <mode>
//
// Modes: pool-exhaustion, async-pool-exhaustion, row-lock (PostgreSQL: DEADLOCKLAB_PG or
// Host=localhost;Username=postgres;Password=postgres;Database=postgres) and sqlserver-pool-exhaustion
// (SQL Server: DEADLOCKLAB_SQLSERVER). Each mode prints READY once the hang is in place.
using DriverVersions;

switch (args.Length == 1 ? args[0] : "")
{
    case "pool-exhaustion": InvoiceScenarios.PoolExhaustion(); break;
    case "async-pool-exhaustion": InvoiceScenarios.AsyncPoolExhaustion(); break;
    case "row-lock": InvoiceScenarios.RowLock(); break;
    case "sqlserver-pool-exhaustion": SqlInvoiceScenarios.PoolExhaustion(); break;
    default:
        Console.Error.WriteLine("usage: DriverVersions pool-exhaustion | async-pool-exhaustion | row-lock | sqlserver-pool-exhaustion");
        return 2;
}
return 0;

namespace DriverVersions
{
    internal static class Ready
    {
        /// Signal readiness from a background thread after the hang has had time to settle, then park forever.
        public static void After(int ms)
        {
            new Thread(() =>
            {
                Thread.Sleep(ms);
                Console.WriteLine("READY");
                Console.Out.Flush();
            }) { IsBackground = true, Name = "ready-signal" }.Start();
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
