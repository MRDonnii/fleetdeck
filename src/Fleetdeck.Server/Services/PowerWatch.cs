using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Vagten over genstart og slukning (ren logik, uden database). Et genstart-job er først lykkedes, når maskinen er tilbage
/// med en ny opstartstid; et sluk-job, når maskinen er væk (og strømmåleren, hvis der er en, viser at den er slukket).
/// Er det ikke sket <see cref="PowerWatchInput.HangMinutes"/> minutter efter det planlagte tidspunkt, "hænger" jobbet.
/// </summary>
public static class PowerWatch
{
    public const int DefaultHangMinutes = 5;
    public const int DefaultMinWatts = 15;
    /// <summary>Hvor længe vagten følger et job, der hænger (kommer maskinen tilbage senere, lykkes jobbet stadig).</summary>
    public static readonly TimeSpan FollowHung = TimeSpan.FromHours(24);

    /// <summary>Opstartstid fra seneste check-in: agentens egen (på sekundet), ellers check-in-tid minus oppetid (±3 min).</summary>
    public static (DateTime? At, bool Precise) Boot(LiveFacts? f, DateTime? seenAt)
    {
        if (f?.BootAt is { } b) return (DateTime.SpecifyKind(b, DateTimeKind.Utc), true);
        if (f?.UptimeH is double up && up >= 0 && seenAt is { } s) return (s.AddHours(-up), false);
        return (null, false);
    }

    /// <summary>Ny opstart i forhold til før genstarten (eller, uden "før", efter at jobbet blev startet).</summary>
    public static bool NewBoot(PowerWatchInput i)
    {
        if (i.BootNow is not { } now) return false;
        var tol = i.BootNowPrecise && i.BootBeforePrecise ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(7);
        if (i.BootBefore is { } before) return now - before > tol;
        return i.Started is { } st && now > st + (i.BootNowPrecise ? TimeSpan.Zero : TimeSpan.FromMinutes(4));
    }

    /// <summary>Samme grænse som "online" i maskinlisten (MachineViews.Online).</summary>
    public static TimeSpan OfflineAfter(int pollSeconds) => TimeSpan.FromSeconds(Math.Max(120, 3 * Math.Max(5, pollSeconds) + 30));

    public static PowerVerdict Evaluate(PowerWatchInput i)
    {
        var online = i.LastSeen is { } ls && i.Now - ls < OfflineAfter(i.PollSeconds);
        var deadline = i.PlannedAt.AddMinutes(Math.Max(1, i.HangMinutes));
        var back = NewBoot(i) && i.LastSeen is { } seen && (i.BootNow is not { } bn || seen >= bn);
        var reboot = i.Type == JobTypes.Reboot;

        // tilbage med ny opstartstid: genstarten er lykkedes (og en sluk-test af Wake-on-LAN også)
        if (back && online)
        {
            if (reboot) return new(PowerVerdictKind.Back);
            if (i.WolTest) return i.WakeSentAt is { } w ? new(PowerVerdictKind.WakeOk, (i.LastSeen!.Value - w).TotalSeconds) : new(PowerVerdictKind.Back);
            return new(PowerVerdictKind.Back);
        }

        // stadig oppe med samme opstart: planen er flyttet (Udsæt/Genstart nu) eller annulleret på maskinen
        if (online && !back)
        {
            if (i.LivePowerAt is { } lp && i.LivePowerMode == i.Type && Math.Abs((lp - i.PlannedAt).TotalSeconds) > 30)
                return new(PowerVerdictKind.Moved, NewPlannedAt: lp);
            if (i.LivePowerAt is null && i.Now < i.PlannedAt.AddSeconds(-30) && i.LastSeen > i.ReportedAt.AddSeconds(5))
                return new(PowerVerdictKind.Cancelled);
        }

        if (!reboot)
        {
            var gone = !online && i.Now > i.PlannedAt;
            if (gone)
            {
                var drawing = i.Watts is double wt && wt >= i.MinWatts;
                if (drawing) return i.Now > deadline ? new(PowerVerdictKind.Hung, i.Watts, Reason: HangReason.StillPowered) : new(PowerVerdictKind.Wait);
                if (!i.WolTest) return new(PowerVerdictKind.Off);
                // Wake-on-LAN-test: vent lidt, så pc'en er helt slukket, send så et magic packet og mål, om den kommer op
                if (i.WakeSentAt is not { } sent)
                    return i.LastSeen is { } l && i.Now - l > OfflineAfter(i.PollSeconds) + TimeSpan.FromSeconds(30) ? new(PowerVerdictKind.SendWake) : new(PowerVerdictKind.Wait);
                return i.Now - sent > TimeSpan.FromMinutes(Math.Max(1, i.HangMinutes)) ? new(PowerVerdictKind.WakeFailed, Reason: HangReason.NoWake) : new(PowerVerdictKind.Wait);
            }
        }

        if (i.Now <= deadline) return new(PowerVerdictKind.Wait);
        // fristen er gået
        if (online) return new(PowerVerdictKind.Hung, i.Watts, Reason: HangReason.SameBoot);
        return new(PowerVerdictKind.Hung, i.Watts, Reason: i.Watts is double w2 && w2 >= i.MinWatts ? HangReason.StillPowered : HangReason.NotBack);
    }

    /// <summary>Tekst til jobbets output og beskeden (dansk, kort).</summary>
    public static string Describe(PowerVerdict v, string type, int hangMinutes, DateTime? localTime = null) => v.Kind switch
    {
        PowerVerdictKind.Back => type == JobTypes.Reboot
            ? $"Tilbage{(localTime is { } t ? $" kl. {t:HH:mm}" : "")} med ny opstartstid – genstarten er lykkedes."
            : $"Maskinen er tændt igen{(localTime is { } t2 ? $" kl. {t2:HH:mm}" : "")}.",
        PowerVerdictKind.Off => "Slukket (svarer ikke længere).",
        PowerVerdictKind.WakeOk => $"Wake-on-LAN virker: maskinen var oppe {Math.Round(v.Value ?? 0)} sek. efter magic packet.",
        PowerVerdictKind.WakeFailed => $"Wake-on-LAN virkede ikke: maskinen kom ikke op inden for {hangMinutes} min. efter magic packet. Tjek BIOS (ErP = Off, Wake on LAN / Power On By PCI-E = On) og netkortet (Gør klar til WoL).",
        PowerVerdictKind.SendWake => "Slukket. Sender Wake-on-LAN (magic packet) …",
        PowerVerdictKind.Cancelled => $"{(type == JobTypes.Reboot ? "Genstarten" : "Slukningen")} blev annulleret på maskinen.",
        PowerVerdictKind.Moved => $"{(type == JobTypes.Reboot ? "Genstarten" : "Slukningen")} er flyttet{(v.NewPlannedAt is { } n ? $" til kl. {n.ToLocalTime():HH:mm}" : "")}.",
        PowerVerdictKind.Hung => v.Reason switch
        {
            HangReason.SameBoot => $"Hænger: maskinen er stadig online med samme opstartstid {hangMinutes} min. efter det planlagte tidspunkt.",
            HangReason.StillPowered => $"Hænger: maskinen svarer ikke, men trækker stadig {Math.Round(v.Value ?? 0)} W {hangMinutes} min. efter det planlagte tidspunkt.",
            _ => $"Hænger: maskinen er ikke tilbage {hangMinutes} min. efter det planlagte tidspunkt{(v.Value is double w ? $" (strømmåleren viser {Math.Round(w)} W)" : "")}.",
        },
        _ => "",
    };

    /// <summary>Bekræftelsen til "Sluk og tænd strømmen": altid et ja, og på en beskyttet maskine også maskinens navn.</summary>
    public static string? ConfirmRefusal(bool confirm, string? typed, bool isProtected, string label)
    {
        if (!confirm) return "Bekræft, at strømmen skal slås fra og til.";
        if (isProtected && !string.Equals((typed ?? "").Trim(), label.Trim(), StringComparison.CurrentCultureIgnoreCase))
            return "Maskinen er beskyttet: skriv dens navn præcis for at bekræfte.";
        return null;
    }

    /// <summary>"Sluk og tænd strømmen" må kun tilbydes, når der er en kontakt og en måler, og måleren viser, at maskinen trækker strøm.</summary>
    public static string? CycleRefusal(string? sw, string? sensor, double? watts, int minWatts)
    {
        if (string.IsNullOrWhiteSpace(sw) || !HaEntity.IsSwitch(sw)) return "Vælg først en kontakt (switch.…) på maskinens side.";
        if (string.IsNullOrWhiteSpace(sensor) || !HaEntity.IsSensor(sensor)) return "Vælg først en strømmåler (sensor.…) på maskinens side.";
        if (watts is not double w) return "Strømmåleren kan ikke læses lige nu.";
        if (w < minWatts) return $"Strømmåleren viser {Math.Round(w)} W – maskinen ser ud til at være slukket. Strømmen slås ikke fra.";
        return null;
    }
}

public enum PowerVerdictKind { Wait, Back, Off, Hung, Cancelled, Moved, SendWake, WakeOk, WakeFailed }
public enum HangReason { None, SameBoot, NotBack, StillPowered, NoWake }

public sealed record PowerVerdict(PowerVerdictKind Kind, double? Value = null, DateTime? NewPlannedAt = null, HangReason Reason = HangReason.None);

/// <param name="Type">reboot eller shutdown.</param>
/// <param name="PlannedAt">Det planlagte tidspunkt (UTC).</param>
/// <param name="ReportedAt">Hvornår agenten meldte planen (jobbets slut-tid fra agenten).</param>
/// <param name="Started">Hvornår agenten startede jobbet.</param>
/// <param name="BootBefore">Opstartstiden før genstarten.</param>
/// <param name="BootNow">Opstartstiden ved seneste check-in.</param>
/// <param name="Watts">Strømmålerens seneste værdi (null = ingen måler eller kan ikke læses).</param>
public sealed record PowerWatchInput(string Type, DateTime PlannedAt, DateTime ReportedAt, DateTime? Started,
    DateTime? BootBefore, bool BootBeforePrecise, DateTime? BootNow, bool BootNowPrecise, DateTime? LastSeen,
    string? LivePowerMode, DateTime? LivePowerAt, DateTime Now, int HangMinutes = PowerWatch.DefaultHangMinutes, int PollSeconds = 30,
    double? Watts = null, int MinWatts = PowerWatch.DefaultMinWatts, bool WolTest = false, DateTime? WakeSentAt = null);

/// <summary>Home Assistant-entiteter, som maskinens side kan pege på.</summary>
public static class HaEntity
{
    private static readonly System.Text.RegularExpressions.Regex Id = new(@"^[a-z_]+\.[a-z0-9_]+$");
    public static bool Valid(string? e) => e is { Length: > 2 and <= 120 } && Id.IsMatch(e);
    public static bool IsSwitch(string? e) => Valid(e) && e!.StartsWith("switch.", StringComparison.Ordinal);
    public static bool IsSensor(string? e) => Valid(e) && e!.StartsWith("sensor.", StringComparison.Ordinal);
    /// <summary>Tom tekst → null; ellers kun gyldige id'er (små bogstaver, tal og _).</summary>
    public static string? Clean(string? e, Func<string?, bool> ok)
    {
        var t = e?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(t) ? null : ok(t) ? t : throw new ArgumentException($"Ugyldigt entitets-id: {e}");
    }

    /// <summary>Strøm i watt fra en Home Assistant-tilstand (kW regnes om). Null, hvis den ikke er et tal.</summary>
    public static double? Watts(string? state, string? unit)
    {
        if (!double.TryParse(state, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || double.IsNaN(v)) return null;
        return unit?.Trim() is "kW" or "kw" ? v * 1000 : v;
    }
}
