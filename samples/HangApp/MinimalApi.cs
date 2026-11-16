using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HangApp;

/// A minimal API whose endpoint blocks on async code. ASP.NET Core compiles each endpoint into a dynamic method
/// (lambda_method), which is always optimized, and inlines the small handler and service method into it: the request
/// threads that block have no frame of the app's code at all. The tests assert line numbers in this file.
static class MinimalApiScenario
{
    public static void Run()
    {
        // Every pool thread ends up in the endpoint: thread pool starvation, the same in every run.
        var threads = Math.Max(8, Environment.ProcessorCount);
        ThreadPool.SetMinThreads(threads, threads);
        ThreadPool.SetMaxThreads(threads, threads);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<PriceList>();
        var app = builder.Build();
        app.MapGet("/prices/{id:int}", (int id, PriceList prices) => prices.GetPrice(id));
        app.Start();

        // Plain sockets on this thread, so that sending the requests needs no thread pool thread.
        var address = new Uri(app.Urls.First());
        var clients = new List<TcpClient>();
        for (var i = 1; i <= threads + 2; i++)
        {
            var client = new TcpClient();
            client.Connect(address.Host, address.Port);
            client.GetStream().Write(Encoding.ASCII.GetBytes($"GET /prices/{i} HTTP/1.1\r\nHost: localhost\r\n\r\n"));
            clients.Add(client);
        }
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (PriceList.Lookups < threads && waited.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(50);
        Ready.SignalLater(500);
        Thread.Sleep(Timeout.Infinite);
        GC.KeepAlive(clients);
    }
}

public sealed class PriceList
{
    private static int _lookups;

    public static int Lookups => Volatile.Read(ref _lookups);

    // BUG: blocks a request thread for as long as the lookup takes.
    public decimal GetPrice(int id) => LookupAsync(id).GetAwaiter().GetResult();

    private async Task<decimal> LookupAsync(int id)
    {
        Interlocked.Increment(ref _lookups);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return id * 1.5m;
    }
}
