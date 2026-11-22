using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Offentlig adgang kørt i selve Fleetdeck-containeren: cloudflared startes som en underproces med tunnel-nøglen (dekrypteret i
/// hukommelsen, sendt som miljøvariabel – aldrig på kommandolinjen eller i en log) og startes igen, hvis den stopper.
/// Fleetdecks container ligger på husets net (br0), så den når AdGuard direkte. To connectorer til samme tunnel er i orden,
/// så den gamle container kan stoppes, når denne kører.
/// </summary>
public sealed partial class TunnelInside(IServiceScopeFactory scopes, ILogger<TunnelInside> log) : BackgroundService
{
    public const string Setting = "tunnel_inside";
    public static string Binary => Environment.GetEnvironmentVariable("CLOUDFLARED_PATH") is { Length: > 0 } p ? p : "/usr/local/bin/cloudflared";
    public static bool Available => File.Exists(Binary);

    private readonly Lock _gate = new();
    private readonly LinkedList<string> _lines = new();
    private readonly HashSet<string> _connected = [];
    private Process? _proc;
    private DateTime? _since, _lastStart;
    private string? _error;
    private int _failures;

    public object Status()
    {
        lock (_gate)
            return new
            {
                available = Available, running = _proc is { HasExited: false }, since = _since, connections = _connected.Count,
                error = _error, lines = _lines.TakeLast(8).ToList(),
            };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await Tick(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Tunnel i Fleetdeck"); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        Stop("Fleetdeck lukker");
    }

    private async Task Tick(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var pa = scope.ServiceProvider.GetRequiredService<PublicAccessSettings>();
        var want = await s.Get(Setting) == "1";
        var running = _proc is { HasExited: false };
        if (!want) { if (running) Stop("slået fra i Indstillinger"); return; }
        if (running) return;
        if (!Available) { lock (_gate) _error = "cloudflared findes ikke i containeren"; return; }
        var token = await pa.Token();
        if (token is null) { lock (_gate) _error = "Ingen tunnel-nøgle gemt"; return; }
        // ventetid efter et stop: 10 s, så længere ved gentagne fejl (højst 5 min)
        var wait = TimeSpan.FromSeconds(Math.Min(300, 10 * Math.Pow(2, Math.Min(_failures, 5))));
        if (_lastStart is { } last && DateTime.UtcNow - last < wait) return;
        Alias(ReserveDns.OriginHost(await s.Get("adguard_url")));
        Start(token);
    }

    /// <summary>Med Reserve-DNS sender tunnelen til navnet fleetdeck-dns: det skal pege på hoved-AdGuard (som --add-host i den gamle container).</summary>
    private void Alias(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return;
        try
        {
            var ip = IPAddress.TryParse(origin, out var a) ? a : Dns.GetHostAddresses(origin).FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip is null) return;
            const string hosts = "/etc/hosts";
            var line = $"{ip} {ReserveDns.Alias}";
            var text = File.Exists(hosts) ? File.ReadAllText(hosts) : "";
            if (!text.Split('\n').Any(l => l.Trim() == line))
                File.WriteAllText(hosts, string.Join('\n', text.Split('\n').Where(l => !Regex.IsMatch(l, $@"\s{Regex.Escape(ReserveDns.Alias)}\s*$"))).TrimEnd() + "\n" + line + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException) { log.LogInformation("fleetdeck-dns kunne ikke sættes: {Msg}", e.Message); }
    }

    private void Start(string token)
    {
        var psi = new ProcessStartInfo(Binary) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "tunnel", "--no-autoupdate", "run" }) psi.ArgumentList.Add(a);
        psi.Environment["TUNNEL_TOKEN"] = token;
        psi.Environment["NO_AUTOUPDATE"] = "true";
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Line(string? l)
        {
            if (string.IsNullOrWhiteSpace(l)) return;
            var masked = PublicAccess.Mask(l, token);
            lock (_gate)
            {
                _lines.AddLast(masked.Length > 300 ? masked[..300] : masked);
                while (_lines.Count > 40) _lines.RemoveFirst();
                var idx = ConnIndex().Match(l) is { Success: true } m ? m.Groups[1].Value : "?";
                if (l.Contains("Registered tunnel connection", StringComparison.Ordinal)) { _connected.Add(idx); _error = null; _failures = 0; }
                else if (l.Contains("Connection terminated", StringComparison.Ordinal) || l.Contains("Unregistered tunnel connection", StringComparison.Ordinal)) _connected.Remove(idx);
            }
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.Exited += (_, _) =>
        {
            lock (_gate)
            {
                _connected.Clear();
                _failures++;
                _error = $"cloudflared stoppede (kode {SafeCode(p)}) – startes igen af sig selv";
            }
            log.LogWarning("Tunnel i Fleetdeck: cloudflared stoppede");
        };
        if (!p.Start()) { lock (_gate) _error = "cloudflared kunne ikke startes"; return; }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (_gate) { _proc = p; _since = DateTime.UtcNow; _lastStart = DateTime.UtcNow; _error = null; }
        log.LogInformation("Tunnel i Fleetdeck: cloudflared startet");
    }

    private static string SafeCode(Process p) { try { return p.ExitCode.ToString(); } catch (InvalidOperationException) { return "?"; } }

    public void Stop(string why)
    {
        Process? p;
        lock (_gate) { p = _proc; _proc = null; _connected.Clear(); _since = null; }
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
        log.LogInformation("Tunnel i Fleetdeck stoppet: {Why}", why);
    }

    [GeneratedRegex(@"connIndex=(\d+)")] private static partial Regex ConnIndex();
}
