using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// "Opdatér alt" bagefter: det, der stadig står på listen, og hvorfor. Grunden findes i agentens linjer ("--  navn: …",
/// "!!  navn: …"), i låsene og i id'ets præfiks (Windows Update-drivere, grafik- og chipset-driver, firmware …), som
/// "Opdatér alt" bevidst ikke tager. Bygges ud fra den seneste færdige "Opdatér alt" og den liste, maskinen har nu.
/// </summary>
public static class UpgradeAllReport
{
    /// <summary>Grupperne i den rækkefølge, siden viser dem.</summary>
    public const string User = "user", Silent = "silent", Fail = "fail", Blocked = "blocked", Separate = "separate", Ignored = "ignored", Other = "other";
    public static readonly string[] Order = [Silent, User, Fail, Blocked, Separate, Other, Ignored];

    /// <summary>Hvad knappen ud for linjen gør: upgrade = opdatér/installér den enkeltvis, amd = AMD's driverdialog, null = ingen knap.</summary>
    public sealed record Skipped(string Name, string WingetId, string Group, string Reason, string? Advice, string? Action);

    /// <param name="Blocked">Kan ikke installeres – forklaringen (UpgradeMark), ellers null.</param>
    public sealed record Item(string Name, string WingetId, bool Driver = false, string? Advice = null, DateTime? FirstSeen = null, string? Blocked = null);

    private static readonly Dictionary<string, string> LockLabel = new() { ["alle"] = "alle maskiner", ["maskine"] = "denne maskine", ["profil"] = "profilen" };

    /// <param name="items">Opdateringerne, maskinen har nu (skjulte er ikke med).</param>
    /// <param name="locks">id → hvor låsen kommer fra (UpgradeLocks.For).</param>
    /// <param name="output">Resultatet fra "Opdatér alt: alle programmer".</param>
    /// <param name="started">Hvornår kørslen startede: opdateringer, der først er set bagefter, var ikke med.</param>
    /// <param name="withSystem">Kørslen havde et Windows Update-/systemjob med (automatisk).</param>
    /// <param name="systemOk">Det job lykkedes.</param>
    public static List<Skipped> Build(IEnumerable<Item> items, IReadOnlyDictionary<string, string> locks, string? output, DateTime? started, bool withSystem, bool systemOk)
    {
        var lines = (output ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var list = new List<Skipped>();
        foreach (var u in items)
        {
            if (started is { } s && u.FirstSeen is { } fs && fs > s) continue;   // kom til, efter at Opdatér alt gik i gang
            if (Classify(u, locks, lines, withSystem, systemOk) is { } x) list.Add(x);
        }
        return list.OrderBy(x => Array.IndexOf(Order, x.Group)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Skipped? Classify(Item u, IReadOnlyDictionary<string, string> locks, List<string> lines, bool withSystem, bool systemOk)
    {
        Skipped S(string g, string why, string? action) => new(u.Name, u.WingetId, g, why, u.Advice, action);
        // agenten har meldt, at den ikke kan installeres (fx firmwaren afviser en Secure Boot-nøgle): ingen knap – det hjælper ikke at prøve igen
        if (u.Blocked is { Length: > 0 } blocked) return S(Blocked, blocked, null);
        if (locks.TryGetValue(u.WingetId, out var lk))
            return S(Ignored, $"Opdateres aldrig – låst på {LockLabel.GetValueOrDefault(lk, lk)}.", null);
        var line = LineFor(lines, u.Name);
        if (line is { } l)
        {
            var mark = l[..2]; var text = l.Length > 4 + u.Name.Length + 1 ? l[(4 + u.Name.Length + 1)..].Trim() : "";
            if (mark == "OK") return null;   // opdateret (fx Office, der gør det færdigt i baggrunden) – forsvinder ved næste inventar
            if (mark == "!!") return S(Fail, Sentence(Regex.Replace(text, @"\s*–\s*sprunget over$", "")), "upgrade");
            if (text.Contains("pr. bruger", StringComparison.Ordinal))
                return S(User, "Installeres pr. bruger, og ingen var logget ind. Prøv igen, når nogen er logget ind.", "upgrade");
            if (text.Contains("undtagelse i profilen", StringComparison.Ordinal)) return S(Ignored, "Udeladt – undtagelse i profilen.", null);
            if (text.Contains("kan ikke opdateres via winget", StringComparison.Ordinal))
                return S(Silent, "Kan ikke opdateres via winget – programmet opdaterer sig selv.", null);
            if (text.Contains("annulleret", StringComparison.Ordinal)) return S(Fail, "Ikke opdateret – Opdatér alt blev annulleret.", "upgrade");
        }
        var id = u.WingetId;
        if (id.StartsWith("wu:", StringComparison.Ordinal))
            return u.Driver ? S(Silent, "Valgfri driver fra Windows Update – installér den enkeltvis.", "upgrade")
                : withSystem && systemOk ? S(Fail, "Windows Update installerede den ikke – prøv den enkeltvis.", "upgrade")
                : withSystem ? S(Fail, "Windows-opdateringerne fejlede – se resultatet, eller prøv den enkeltvis.", "upgrade")
                : S(Separate, "Windows-opdatering – kommer med, når Opdatér alt kører automatisk, eller installér den enkeltvis.", "upgrade");
        if (id.StartsWith("gpu:", StringComparison.Ordinal)) return S(Silent, "Grafikdriver – skærmen blinker under installationen. Brug Installér driver.", "amd");
        if (id.StartsWith("chipset:", StringComparison.Ordinal))
            return S(Silent, "Chipset-driver – kræver en indlogget administrator og en genstart. Brug Installér driver.", "amd");
        if (id.StartsWith("fwupd:", StringComparison.Ordinal)) return S(Silent, "Firmware – opdateres kun, når du trykker Opdatér firmware.", "upgrade");
        if (id.StartsWith("macos:", StringComparison.Ordinal))
            return withSystem ? S(Fail, "macOS-opdateringen blev ikke installeret – prøv den enkeltvis.", "upgrade")
                : S(Separate, "macOS-opdatering – kommer med, når Opdatér alt kører automatisk.", "upgrade");
        return S(Other, line is null ? "Var ikke med i Opdatér alt – prøv den enkeltvis." : Sentence(line[4..].Trim()), "upgrade");
    }

    /// <summary>Agentens linje for programmet: "OK  navn …", "--  navn: …" eller "!!  navn: …".</summary>
    public static string? LineFor(IEnumerable<string> lines, string name) =>
        name.Length == 0 ? null : lines.LastOrDefault(l => l.Length > 4 && l[..2] is "OK" or "--" or "!!" && l[2..4] == "  "
            && l[4..].StartsWith(name, StringComparison.Ordinal) && (l.Length == 4 + name.Length || l[4 + name.Length] is ':' or ' '));

    private static string Sentence(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return "Fejlede – se resultatet.";
        s = char.ToUpperInvariant(s[0]) + s[1..];
        return s.EndsWith('.') ? s : s + ".";
    }

    /// <summary>Den seneste færdige "Opdatér alt" (programjobbet uden et bestemt program) og det, den sprang over. null = ingen, eller en kører.</summary>
    public static async Task<object?> ForMachine(AppDb db, int machineId, IReadOnlyDictionary<string, string> locks)
    {
        var recent = await db.Jobs.AsNoTracking().Where(j => j.MachineId == machineId && (j.Type == JobTypes.Upgrade || j.Type == JobTypes.WindowsUpdate || j.Type == JobTypes.UpdateRound))
            .OrderByDescending(j => j.Id).Take(40).Select(j => new { j.Id, j.Type, j.Title, j.Status, j.Created, j.Started, j.Finished, j.ParamsJson }).ToListAsync();
        // opdateringsmotoren: en runde tager både systemet og programmerne
        var all = recent.FirstOrDefault(j => j.Type == JobTypes.UpdateRound || j.Type == JobTypes.Upgrade && Json.Des<JobParams>(j.ParamsJson).WingetId is null);
        if (all is null || JobStatus.Active.Contains(all.Status)) return null;
        // systemdelen er oprettet i samme omgang ("Opdatér alt: Windows-opdateringer")
        var sys = all.Type == JobTypes.UpdateRound ? all : recent.FirstOrDefault(j => j.Type == JobTypes.WindowsUpdate && j.Title.StartsWith("Opdatér alt", StringComparison.Ordinal)
            && Math.Abs((j.Created - all.Created).TotalMinutes) < 2);
        var output = await db.Jobs.AsNoTracking().Where(j => j.Id == all.Id).Select(j => j.Output).FirstAsync();
        var items = await db.Upgrades.IgnoreQueryFilters().AsNoTracking().Where(u => u.MachineId == machineId && !u.Hidden)
            .Select(u => new Item(u.Name, u.WingetId, u.Driver, u.Advice, u.FirstSeen, u.Blocked)).ToListAsync();
        var skipped = Build(items, locks, output, all.Started ?? all.Created, sys is not null, sys?.Status == JobStatus.Success);
        var done = Regex.Match(output ?? "", @"(\d+) opdateret, (\d+) fejlede") is { Success: true } mt ? int.Parse(mt.Groups[1].Value) : (int?)null;
        return new
        {
            jobId = all.Id, all.Status, all.Finished, updated = done, skipped,
            // "Skjul dem, der anbefales ikke": kun Windows Update kan skjule
            hideable = skipped.Where(x => x.Advice is not null && x.WingetId.StartsWith("wu:", StringComparison.Ordinal)).Select(x => x.WingetId).ToList(),
        };
    }
}

/// <summary>Skjul/vis igen i Windows Update: kun id'er med "wu:" og et UpdateID (GUID) – aldrig noget, agenten ikke selv har meldt.
/// Andre opdateringer skjules i Fleetdeck selv (UpgradeMarks).</summary>
public static class UpdateHide
{
    public const int Max = 50;

    public static List<string> Valid(IEnumerable<string>? ids) =>
        (ids ?? []).Select(x => (x ?? "").Trim()).Where(x => x.StartsWith("wu:", StringComparison.Ordinal) && Guid.TryParse(x[3..], out _))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(Max).ToList();

    /// <param name="inFleetdeck">Skjult i Fleetdeck selv (alt andet end Windows Update) – sker med det samme, intet job.</param>
    public static string Title(bool hide, IReadOnlyList<string> names, int count, bool inFleetdeck = false)
    {
        var what = names.Count == 1 ? names[0] : $"{count} opdateringer";
        if (inFleetdeck) return hide ? $"Skjult: {what}" : $"Vises igen: {what}";
        return hide ? $"Skjul i Windows Update: {what}" : $"Vis igen i Windows Update: {what}";
    }
}
