namespace Fleetdeck.Shared;

/// <summary>Kører overvågningstjek fra agentens egen maskine i baggrunden (højst én runde ad gangen) og sender resultaterne.</summary>
public static class CheckLoop
{
    private static int _busy;

    public static void Start(List<AgentCheck> checks, Func<List<AgentCheckResult>, Task> send, Action<string>? log = null)
    {
        if (checks.Count == 0 || Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var results = await Task.WhenAll(checks.Select(c => Probe.Run(c)));
                await send(results.ToList());
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
            {
                log?.Invoke($"Overvågningstjek kunne ikke sendes: {e.Message}");
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }
}
