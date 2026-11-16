using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HangApp;

// The generic host (Host.CreateApplicationBuilder, as in a worker service), with a hosted service that never finishes
// starting, or running normally with a background loop. Run() blocks on the host's RunAsync task; RunAsync() is
// awaited by the app's async Main.

static class GenericHostScenario
{
    public static void Run(bool stuckStart) => Build(stuckStart).Run();

    public static Task RunAsync(bool stuckStart) => Build(stuckStart).RunAsync();

    /// A host with a hosted service whose StartAsync never returns (stuck) or with a background loop (healthy).
    private static IHost Build(bool stuckStart)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (stuckStart) builder.Services.AddHostedService<BrokerConnection>();
        builder.Services.AddHostedService<Heartbeat>();
        var host = builder.Build();
        // The healthy host signals only once it has started, so the dump never catches it still in StartAsync.
        if (stuckStart) Ready.SignalLater(1500);
        else host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() => Ready.SignalLater());
        return host;
    }

    /// Connects to a message broker in StartAsync; the broker never answers, so the host never finishes starting.
    private sealed class BrokerConnection : IHostedService
    {
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken) => await ConnectAsync(cancellationToken);

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            await _connected.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Heartbeat : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
