using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public enum UnlockState { None, Active, NeedsReauth }

/// <summary>Brugerens pinkode til terminalen: 4 til 8 cifre, gemt som hash i indstillingerne (aldrig udleveret af API'et).</summary>
public static class TerminalPin
{
    public static string Key(int userId) => $"terminal_pin_{userId}";
    public static bool Valid(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);
}

/// <summary>
/// Oplåsning af terminal og nøgleboks med brugerens egen pinkode, fordi en almindelig session kan være logget ind længe
/// (husket enhed, single sign-on). Efter 15 minutter uden brug skal pinkoden skrives igen. Holdes kun i hukommelsen.
/// </summary>
public sealed class TerminalGate(TimeProvider clock)
{
    public const string Header = "X-Terminal-Unlock";
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(15), Absolute = TimeSpan.FromHours(12);
    private sealed class Entry { public int UserId; public string Stamp = ""; public DateTimeOffset Issued, Last; }
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private static string Key(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public string Issue(User u)
    {
        var now = clock.GetUtcNow();
        foreach (var old in _entries.Where(kv => now - kv.Value.Issued > Absolute).Select(kv => kv.Key).ToList()) _entries.TryRemove(old, out _);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _entries[Key(token)] = new Entry { UserId = u.Id, Stamp = u.SecurityStamp, Issued = now, Last = now };
        return token;
    }

    public UnlockState State(string? token, User u)
    {
        if (string.IsNullOrEmpty(token) || !_entries.TryGetValue(Key(token), out var e) || e.UserId != u.Id || e.Stamp != u.SecurityStamp) return UnlockState.None;
        var now = clock.GetUtcNow();
        if (now - e.Issued > Absolute) { _entries.TryRemove(Key(token), out _); return UnlockState.None; }
        return now - e.Last > Idle ? UnlockState.NeedsReauth : UnlockState.Active;
    }

    /// <summary>Brug af terminalen tæller som aktivitet, men vækker ikke en oplåsning, der allerede er udløbet.</summary>
    public void Touch(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_entries.TryGetValue(Key(token), out var e)) return;
        var now = clock.GetUtcNow();
        if (now - e.Last <= Idle) e.Last = now;
    }

    /// <summary>Efter fornyet bekræftelse (adgangskode eller kode).</summary>
    public bool Renew(string? token, User u)
    {
        if (State(token, u) == UnlockState.None) return false;
        _entries[Key(token!)].Last = clock.GetUtcNow();
        return true;
    }

    public void Revoke(string? token) { if (!string.IsNullOrEmpty(token)) _entries.TryRemove(Key(token), out _); }
}

public static class TerminalSecurity
{
    public const string RateLimitPolicy = "terminal";

    /// <summary>Terminalen må bruges over https (også bag en proxy som Home Assistants Ingress eller Cloudflare) og fra hjemmenettet eller VPN.
    /// Alt andet afvises. Ejerens valg 2026-11-18: Fleetdeck nås kun ad de veje.</summary>
    public static bool SecureChannel(HttpContext ctx) =>
        ctx.Features.Get<ITlsConnectionFeature>() is not null || ctx.Request.IsHttps || IsPrivate(ctx.Connection.RemoteIpAddress);

    /// <summary>Hjemmenet, VPN (også Tailscales 100.64.0.0/10) og maskinen selv. Null er en lokal unix-socket.</summary>
    public static bool IsPrivate(System.Net.IPAddress? ip)
    {
        if (ip is null) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (b.Length == 4)
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] is >= 64 and <= 127) || (b[0] == 169 && b[1] == 254);
        return (b[0] & 0xFE) == 0xFC || ip.IsIPv6LinkLocal;
    }

    /// <summary>WebSocket fra Fleetdecks egen side. Browseren sætter Origin selv, så en anden side kan ikke udgive sig for at være os.</summary>
    public static bool OriginAllowed(string? origin, string host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
        && string.Equals(u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}", host, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Kan denne maskine åbne en terminal via agenten? Null = ja, ellers årsagen til brugeren.</summary>
public static class TerminalEligibility
{
    public static string? Why(Machine m, string? signedJobsMode)
    {
        if (m.ReplacesId is not null) return "Maskinen venter på at blive godkendt.";
        if (!m.TerminalCapable) return "Agenten på maskinen skal opdateres, før terminalen kan bruges.";
        return Channel(m, signedJobsMode);
    }

    /// <summary>Den signerede, krypterede kanal til agenten, som både terminal og live-log kræver.</summary>
    public static string? Channel(Machine m, string? signedJobsMode)
    {
        if (SignedJobsMode.Normalize(signedJobsMode) == SignedJobsMode.Off) return "Signerede jobs er slået fra under Agent-sikkerhed.";
        if (m.JobTrust != AgentTrust.Pinned) return "Agenten har ikke fastlåst serverens jobnøgle endnu.";
        if (m.Transport is not ("https" or "socket")) return "Agenten forbinder over http. Slå https til for agenterne under Agent-sikkerhed.";
        return null;
    }
}

/// <summary>Maskiner, der er beskyttede fra starten. Markeringen sættes én gang pr. navn, så en administrator kan fjerne den igen.</summary>
public static class TerminalDefaults
{
    public const string SeededSetting = "terminal_protected_seeded";
    public static IReadOnlyList<string> Names() =>
        (Environment.GetEnvironmentVariable("TERMINAL_PROTECTED_MACHINES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Et par almindelige kommandoer at starte med. Lægges kun ind én gang, så de kan slettes.</summary>
    public static async Task SeedSnippets(AppDb db, SettingsService s)
    {
        if (await s.Get("terminal_snippets_seeded") == "1") return;
        db.TerminalSnippets.AddRange(
            new TerminalSnippet { Name = "Diskplads", Command = "df -h" },
            new TerminalSnippet { Name = "Hukommelse", Command = "free -h", Platform = "linux" },
            new TerminalSnippet { Name = "Seneste fejl i loggen", Command = "journalctl -p err -n {{antal}} --no-pager", Platform = "linux" },
            new TerminalSnippet { Name = "Docker: kørende containere", Command = "docker ps" },
            new TerminalSnippet { Name = "Genstart tjeneste", Command = "systemctl restart {{tjeneste}}", Platform = "linux", Risky = true },
            new TerminalSnippet { Name = "Stoppede tjenester", Command = "Get-Service | Where-Object Status -eq Stopped", Platform = "windows" });
        await db.SaveChangesAsync();
        await s.Set("terminal_snippets_seeded", "1");
    }

    public static async Task MarkProtected(AppDb db, SettingsService s, AuditService audit)
    {
        var seeded = (await s.Get(SeededSetting)).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var name in Names().Where(n => !seeded.Contains(n)))
        {
            var up = name.ToUpperInvariant();
            var hits = await db.Machines.Where(m => m.Name.ToUpper() == up || (m.DisplayName != null && m.DisplayName.ToUpper() == up)).ToListAsync();
            if (hits.Count == 0) continue;     // maskinen er ikke tilmeldt endnu: prøv igen ved næste start
            foreach (var m in hits) { m.Protected = true; await audit.Log("system", "terminal.protected", $"{m.Label}: beskyttet som standard", m.Id); }
            seeded.Add(name); changed = true;
        }
        await db.SaveChangesAsync();
        if (changed) await s.Set(SeededSetting, string.Join(',', seeded));
    }
}
