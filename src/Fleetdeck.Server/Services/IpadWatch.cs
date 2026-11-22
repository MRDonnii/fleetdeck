using Fleetdeck.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Om styringen bruges lige nu: active, stale eller missing – og om der er en åben advarsel.</summary>
public sealed record IpadControl(string State, DateTime? Seen, DateTime? LanSeen, bool Watch, DateTime? AlertSince, string? AlertText, DateTime? SnoozeUntil, double Hours);

public enum WatchAction { None, Raise, Recovered, Cleared }

/// <summary>
/// Fortæller, om en enhed faktisk bruger styringen. På iPad og iPhone tæller kun DNS-opslag med enhedens klient-id: opslag fra den faste
/// IP-adresse derhjemme kommer også fra en iPad, hvor profilen er fjernet. Fleetdeck kan ikke skelne en slukket enhed fra en fjernet profil,
/// så teksterne må aldrig påstå det ene eller det andet.
/// </summary>
public static class IpadWatch
{
    public const string HoursSetting = "ipad_watch_hours";
    public const double DefaultHours = 24, MinHours = 1, MaxHours = 720;
    public const string Active = "active", Stale = "stale", Missing = "missing";
    public const int MaxSnoozeDays = 60;
    /// <summary>Så længe regnes en enhed som aktiv; derefter er den "ikke set i et stykke tid" (en advarsel kommer først efter den valgte periode).</summary>
    private const double ActiveHours = 6;

    public static double Hours(double v) => double.IsFinite(v) && v > 0 ? Math.Clamp(v, MinHours, MaxHours) : DefaultHours;
    public static async Task<double> Hours(SettingsService s) => Hours(await s.GetNum(HoursSetting, DefaultHours));

    /// <summary>Advarslen er som standard kun slået til for iPad og iPhone: Apple TV og Android styres via IP-adressen og er tit væk fra hjemmet eller slukket.</summary>
    public static bool Enabled(Ipad p) => p.WatchEnabled ?? DeviceTypes.IsIos(p.DeviceType);

    /// <summary>Hvornår enheden sidst brugte styringen.</summary>
    public static DateTime? Seen(Ipad p) => DeviceTypes.IsIos(p.DeviceType) ? p.ProfileSeen : p.LastSeen;

    /// <summary>Gemmer det nyeste opslag fra loggen. <paramref name="viaClient"/> er kun rækker med klient-id, <paramref name="viaAny"/> også dem fra IP-adressen.</summary>
    public static void Observe(Ipad p, DateTime? viaClient, DateTime? viaAny)
    {
        if (viaClient is { } c && (p.ProfileSeen is null || c > p.ProfileSeen)) p.ProfileSeen = c;
        if (viaAny is { } a && (p.LastSeen is null || a > p.LastSeen)) p.LastSeen = a;
    }

    public static IpadControl Status(Ipad p, DateTime now, double hours)
    {
        var seen = Seen(p);
        var state = seen is not { } s ? Missing : (now - s).TotalHours < Math.Min(ActiveHours, hours) ? Active : Stale;
        // tændt og på hjemmets netværk, men uden om profilen
        var lan = DeviceTypes.IsIos(p.DeviceType) && p.LastSeen is { } l && (seen is null || l > seen) ? p.LastSeen : null;
        return new(state, seen, lan, Enabled(p), p.WatchAlertSince, p.WatchAlertSince is null || seen is null ? null : AlertText(p, seen.Value, now),
            p.WatchSnoozeUntil > now ? p.WatchSnoozeUntil : null, hours);
    }

    /// <summary>Hvad der skal ske med advarslen for én enhed lige nu.</summary>
    public static WatchAction Decide(Ipad p, DateTime now, double hours)
    {
        var open = p.WatchAlertSince is not null;
        if (!Enabled(p) || Seen(p) is not { } seen || p.WatchSnoozeUntil > now) return open ? WatchAction.Cleared : WatchAction.None;
        return Due(p, seen, now, hours) ? open ? WatchAction.None : WatchAction.Raise : open ? WatchAction.Recovered : WatchAction.None;
    }

    /// <summary>Efter en snooze får enheden hele perioden til at melde sig igen, så hjemkomsten fra ferien ikke giver en advarsel.</summary>
    private static bool Due(Ipad p, DateTime seen, DateTime now, double hours)
    {
        var from = p.WatchSnoozeUntil is { } u && u > seen ? u : seen;
        return (now - from).TotalHours >= hours;
    }

    /// <summary>Er alle overvågede iPads stille på én gang, er det snarere DNS-adressen, der er nede, end profilerne, der er fjernet.</summary>
    public static bool AllQuiet(IReadOnlyCollection<Ipad> ipads, DateTime now, double hours)
    {
        var watched = ipads.Where(p => DeviceTypes.IsIos(p.DeviceType) && Enabled(p) && p.ProfileSeen is not null && !(p.WatchSnoozeUntil > now)).ToList();
        return watched.Count >= 2 && watched.All(p => Due(p, p.ProfileSeen!.Value, now, hours));
    }

    public static string AlertText(Ipad p, DateTime seen, DateTime now)
    {
        var h = Math.Max(1, (int)(now - seen).TotalHours);
        var span = h == 1 ? "1 time" : $"{h} timer";
        return DeviceTypes.IsIos(p.DeviceType)
            ? $"{p.Name} har ikke brugt styringen i {span}. Den kan være slukket, eller profilen kan være fjernet."
            : $"{p.Name} har ikke brugt styringen i {span}. Den kan være slukket eller væk fra hjemmets netværk.";
    }

    /// <summary>Én advarsel pr. episode: tilstanden gemmes før beskederne sendes, så en genstart ikke sender den igen.</summary>
    public static async Task<(bool Changed, bool Held)> Run(AppDb db, Action<string, string> send, double hours, DateTime now, CancellationToken ct = default)
    {
        var ipads = await db.Ipads.ToListAsync(ct);
        var held = AllQuiet(ipads, now, hours);
        var outbox = new List<(string Text, string Kind)>();
        var changed = false;
        foreach (var p in ipads)
        {
            switch (Decide(p, now, hours))
            {
                case WatchAction.Raise when !held:
                    p.WatchAlertSince = now; changed = true;
                    outbox.Add(($"🟠 {p.Name}: styringen bruges ikke\n{AlertText(p, Seen(p)!.Value, now)}", "warning"));
                    break;
                case WatchAction.Recovered:
                    p.WatchAlertSince = null; changed = true;
                    outbox.Add(($"🟢 {p.Name}: styringen bruges igen\n{p.Name} har brugt styringen igen.", "resolved"));
                    break;
                case WatchAction.Cleared:
                    p.WatchAlertSince = null; changed = true;
                    break;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
        foreach (var (text, kind) in outbox) send(text, kind);
        return (changed, held);
    }

    /// <summary>Slår advarslen til/fra og sætter snooze (0 dage = stop snooze). Lukker en åben advarsel med det samme, hvis den ikke længere gælder.</summary>
    public static void Configure(Ipad p, bool? enabled, int? snoozeDays, DateTime now)
    {
        if (enabled is { } e) p.WatchEnabled = e;
        if (snoozeDays is { } d) p.WatchSnoozeUntil = d <= 0 ? null : now.AddDays(Math.Min(d, MaxSnoozeDays));
        if (!Enabled(p) || p.WatchSnoozeUntil > now) p.WatchAlertSince = null;
    }
}

/// <summary>Husker, om forespørgselsloggen er læst uden afbrydelser. Først efter en halv time kan fravær i loggen betyde noget.</summary>
public sealed class WatchMonitor
{
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(30);
    private DateTime? okSince;
    public void Note(bool ok, DateTime now) => okSince = ok ? okSince ?? now : null;
    public bool Ready(DateTime now) => okSince is { } s && now - s >= Settle;
}
