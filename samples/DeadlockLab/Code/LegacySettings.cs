namespace DeadlockLab.Code;

// Looks like a third-party SDK: a "convenient" sync API on top of an async core.
// The author did add ConfigureAwait(false)... to the last await only. The first await,
// three calls down, still captures the caller's SynchronizationContext.

public sealed class SettingsClient
{
    private readonly SettingsCache _cache = new();

    /// Sync wrapper "for legacy callers".
    public string GetSetting(string key) => GetSettingAsync(key).GetAwaiter().GetResult();

    public async Task<string> GetSettingAsync(string key)
    {
        var raw = await _cache.GetOrLoadAsync(key);
        return await Normalize(raw).ConfigureAwait(false);
    }

    private static Task<string> Normalize(string raw) => Task.FromResult(raw.Trim());
}

internal sealed class SettingsCache
{
    private readonly SettingsStore _store = new();

    public async Task<string> GetOrLoadAsync(string key) => await _store.LoadAsync(key);
}

internal sealed class SettingsStore
{
    public async Task<string> LoadAsync(string key)
    {
        await Task.Delay(30); // the "network" call; resumes on the captured context
        return $" value-of-{key} ";
    }
}

public sealed class SettingsWindow
{
    private readonly SettingsClient _client = new();

    public void OnLoad() => Console.WriteLine(_client.GetSetting("theme"));
}

internal static class LegacySettingsScenario
{
    public static void Run()
    {
        Ready.SignalLater();
        new SingleThreadSynchronizationContext().RunOnCurrentThread(() => new SettingsWindow().OnLoad());
    }
}
