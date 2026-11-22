using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Bios;

public static class BiosRuleClasses
{
    /// <summary>Almindelig: forvalgt, når værdien er ændret og stadig gyldig.</summary>
    public const string Common = "common";
    /// <summary>Kræver eget valg: aldrig forvalgt, og serveren kræver, at feltet er bekræftet enkeltvis.</summary>
    public const string Ask = "ask";
    /// <summary>Aldrig: vises, men kan ikke vælges.</summary>
    public const string Never = "never";
}

/// <summary>Regelfilen (Services/Bios/bios-settings-rules.json): hvilke indstillinger der forvælges, kræver eget valg eller aldrig røres.</summary>
public sealed class BiosSettingsRules
{
    public sealed class Rule
    {
        public string Class { get; set; } = BiosRuleClasses.Common;
        public List<string> Match { get; set; } = [];
        public string? Warn { get; set; }
        public List<string>? Drivers { get; set; }
        internal List<Regex> Compiled { get; set; } = [];
    }

    private sealed class FileModel
    {
        public int Version { get; set; }
        public List<Rule> Rules { get; set; } = [];
    }

    public IReadOnlyList<Rule> Rules { get; }

    private BiosSettingsRules(List<Rule> rules)
    {
        foreach (var r in rules)
        {
            if (r.Class is not (BiosRuleClasses.Common or BiosRuleClasses.Ask or BiosRuleClasses.Never)) throw new InvalidDataException($"Ukendt regelklasse: {r.Class}");
            r.Compiled = r.Match.Select(m => new Regex(m, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))).ToList();
        }
        // "aldrig" vinder altid, uanset rækkefølgen i filen
        Rules = rules.OrderBy(r => r.Class == BiosRuleClasses.Never ? 0 : 1).ToList();
    }

    public static BiosSettingsRules Parse(string json) =>
        new(JsonSerializer.Deserialize<FileModel>(json, Json.Web)?.Rules ?? throw new InvalidDataException("Regelfilen er tom."));

    private static readonly Lazy<BiosSettingsRules> Embedded = new(() =>
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("bios-settings-rules.json")
            ?? throw new InvalidOperationException("bios-settings-rules.json mangler i serveren.");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    });

    /// <summary>Den indbyggede regelfil.</summary>
    public static BiosSettingsRules Default => Embedded.Value;

    public static string Key(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>Klassen og advarslen for en indstilling. Passer ingen regel, er den "common".</summary>
    public (string Class, string? Warn) Classify(string driver, string name, string? display = null)
    {
        var keys = new[] { Key(name), Key(display) }.Where(k => k.Length > 0).Distinct().ToArray();
        foreach (var r in Rules)
        {
            if (r.Drivers is { Count: > 0 } d && !d.Contains(driver, StringComparer.OrdinalIgnoreCase)) continue;
            if (r.Compiled.Any(rx => keys.Any(rx.IsMatch))) return (r.Class, r.Warn);
        }
        return (BiosRuleClasses.Common, null);
    }
}

public static class BiosDiffStates
{
    public const string Same = "same", Changed = "changed", New = "new", Gone = "gone";
}

public static class BiosCheckColors
{
    /// <summary>grøn = matcher, gul = BIOS'en har normaliseret værdien, rød = afvist eller mangler.</summary>
    public const string Green = "green", Yellow = "yellow", Red = "red";
}

/// <summary>Én række i sammenligningen af to snapshots.</summary>
public sealed record BiosDiffRow(string Name, string? Display, string? Before, string? Now, string State, string Rule, string? Warn,
    bool Valid, bool Selectable, bool Preselected, string? Why);

public sealed record BiosCheckRow(string Name, string Expected, string? Actual, string Color, string? Why);

/// <summary>
/// Sammenlign to snapshots og planlæg en gendannelse. Kun ændrede, stadig gyldige værdier kan vælges; "aldrig" kan ikke vælges;
/// "kræver eget valg" skal bekræftes felt for felt. Alt skrives i ét job med én gemning.
/// </summary>
public static class BiosRestorePlanner
{
    public static List<BiosDiffRow> Diff(IReadOnlyList<BiosSettingItem> before, IReadOnlyList<BiosSettingItem> now, string driver, BiosSettingsRules? rules = null)
    {
        rules ??= BiosSettingsRules.Default;
        var b = before.GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var n = now.GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rows = new List<BiosDiffRow>();
        foreach (var it in n.Values)
        {
            var (cls, warn) = rules.Classify(driver, it.Name, it.Display);
            if (!b.TryGetValue(it.Name, out var old))
            {
                rows.Add(new(it.Name, it.Display, null, it.Value, BiosDiffStates.New, cls, warn, false, false, false, "Ny i denne BIOS. Røres ikke."));
                continue;
            }
            var nowV = BiosValues.Clean(it.Value);
            var oldV = BiosValues.Clean(old.Value);
            if (it.Unreadable == true || old.Unreadable == true)
            {
                // en tom værdi kan ikke læses (og ikke skrives tilbage)
                var same = it.Unreadable == old.Unreadable && string.Equals(nowV, oldV, StringComparison.Ordinal);
                rows.Add(new(it.Name, it.Display ?? old.Display, old.Unreadable == true ? null : oldV, it.Unreadable == true ? null : nowV,
                    same ? BiosDiffStates.Same : BiosDiffStates.Changed, cls, warn, false, false, false, "Værdien kunne ikke læses (tom i BIOS'en). Røres ikke."));
                continue;
            }
            if (string.Equals(nowV, oldV, StringComparison.Ordinal))
            {
                rows.Add(new(it.Name, it.Display ?? old.Display, oldV, nowV, BiosDiffStates.Same, cls, warn, true, false, false, null));
                continue;
            }
            var valid = BiosValues.IsValid(it, oldV);
            // bootlister uden possible_values (think-lmi melder dem som "string"): elementerne kontrolleres mod den nuværende liste
            var bootList = IsBootList(it.Name, it.Display ?? old.Display) && it.Possible is not { Count: > 0 };
            if (valid && bootList && !BootListOk(oldV, nowV)) valid = false;
            string? why = null;
            if (cls == BiosRuleClasses.Never) why = "Springes altid over.";
            else if (!valid) why = bootList || BiosValues.IsList(it) || BiosValues.ListParts(oldV).Length > 1
                ? "Listen har elementer, der ikke findes i den nye BIOS."
                : "Den gamle værdi er ikke gyldig i den nye BIOS.";
            var selectable = cls != BiosRuleClasses.Never && valid;
            rows.Add(new(it.Name, it.Display ?? old.Display, oldV, nowV, BiosDiffStates.Changed, cls, warn, valid, selectable,
                selectable && cls == BiosRuleClasses.Common, why));
        }
        foreach (var old in b.Values.Where(o => !n.ContainsKey(o.Name)))
        {
            var (cls, warn) = rules.Classify(driver, old.Name, old.Display);
            rows.Add(new(old.Name, old.Display, BiosValues.Clean(old.Value), null, BiosDiffStates.Gone, cls, warn, false, false, false, "Findes ikke mere."));
        }
        return rows;
    }

    private static readonly Regex BootListName = new("bootsequence|bootorder|bootpriority|startupsequence", RegexOptions.CultureInvariant);

    public static bool IsBootList(string name, string? display) =>
        BootListName.IsMatch(BiosSettingsRules.Key(name)) || BootListName.IsMatch(BiosSettingsRules.Key(display));

    /// <summary>Alle elementer i den gamle liste findes i den nuværende, og ingen står der to gange.</summary>
    public static bool BootListOk(string old, string now)
    {
        var o = BiosValues.ListParts(old);
        var n = BiosValues.ListParts(now);
        return o.Length > 0 && o.Distinct(StringComparer.OrdinalIgnoreCase).Count() == o.Length && o.All(x => n.Contains(x, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Lenovo afviser en værdi, der strider mod en anden indstilling. Skriv derfor i en rækkefølge, der altid er lovlig undervejs:
    /// først det, der slår sikkerhed fra (OS Optimized Defaults og Secure Boot = Disabled), så Boot Mode/CSM, så resten,
    /// og til sidst Secure Boot og OS Optimized Defaults = Enabled.
    /// </summary>
    public static int WriteOrder(BiosSettingChange c)
    {
        var k = BiosSettingsRules.Key(c.Name);
        var off = BiosSettingsRules.Key(c.Value) is "disabled" or "disable" or "off" or "0";
        if (k.Contains("osoptimized")) return off ? 0 : 5;
        if (k.Contains("secureboot") && !k.Contains("mode")) return off ? 1 : 4;
        if (k.Contains("csm") || k.Contains("bootmode") || k.Contains("legacyboot")) return 2;
        return 3;
    }

    /// <summary>
    /// De ændringer, der skal skrives: de valgte rækker, hvis de er ændret og kan vælges, og – for "kræver eget valg" – er bekræftet.
    /// Fejl giver en tekst pr. række; så skrives intet.
    /// </summary>
    public static (List<BiosSettingChange> Changes, List<string> Errors) Plan(IReadOnlyList<BiosDiffRow> diff, IEnumerable<string> selected, IEnumerable<string>? acknowledged)
    {
        var rows = diff.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var ack = new HashSet<string>(acknowledged ?? [], StringComparer.Ordinal);
        var changes = new List<BiosSettingChange>();
        var errors = new List<string>();
        foreach (var name in selected.Distinct(StringComparer.Ordinal))
        {
            if (!rows.TryGetValue(name, out var r)) { errors.Add($"{name}: findes ikke i sammenligningen."); continue; }
            if (r.State != BiosDiffStates.Changed) { errors.Add($"{name}: er ikke ændret."); continue; }
            if (r.Rule == BiosRuleClasses.Never) { errors.Add($"{name}: springes altid over."); continue; }
            if (!r.Selectable || r.Before is null || r.Now is null) { errors.Add($"{name}: {r.Why ?? "kan ikke gendannes."}"); continue; }
            if (r.Rule == BiosRuleClasses.Ask && !ack.Contains(name)) { errors.Add($"{name}: kræver, at du bekræfter netop dette felt."); continue; }
            changes.Add(new BiosSettingChange { Name = r.Name, Value = r.Before, ExpectOld = r.Now });
        }
        if (changes.Count == 0 && errors.Count == 0) errors.Add("Vælg mindst én indstilling.");
        return (changes.OrderBy(WriteOrder).ToList(), errors);   // OrderBy er stabil: ellers samme rækkefølge som valgt
    }

    /// <summary>Efter genstarten: blev værdierne, som de skulle? Grøn = som forventet, gul = BIOS'en har normaliseret den, rød = afvist eller mangler.</summary>
    public static List<BiosCheckRow> Check(IReadOnlyList<BiosSettingChange> expected, IReadOnlyList<BiosSettingItem> after, BiosApplyResult? apply = null)
    {
        var a = after.GroupBy(i => i.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var lines = apply?.Lines.ToDictionary(l => l.Name, StringComparer.Ordinal) ?? [];
        return expected.Select(c =>
        {
            if (!a.TryGetValue(c.Name, out var it)) return new BiosCheckRow(c.Name, c.Value, null, BiosCheckColors.Red, "Findes ikke efter genstarten.");
            var v = BiosValues.Clean(it.Value);
            if (string.Equals(v, BiosValues.Clean(c.Value), StringComparison.Ordinal)) return new BiosCheckRow(c.Name, c.Value, v, BiosCheckColors.Green, null);
            if (lines.TryGetValue(c.Name, out var l) && l.Status is not BiosApplyStatus.Written)
                return new BiosCheckRow(c.Name, c.Value, v, BiosCheckColors.Red, $"Blev ikke skrevet ({BiosSettingsJob.Label(l.Status)}).");
            if (BiosValues.SameNormalized(v, c.Value))
                return new BiosCheckRow(c.Name, c.Value, v, BiosCheckColors.Yellow, "BIOS'en har normaliseret værdien.");
            return new BiosCheckRow(c.Name, c.Value, v, BiosCheckColors.Red, string.Equals(v, BiosValues.Clean(c.ExpectOld), StringComparison.Ordinal)
                ? "BIOS'en beholdt den gamle værdi." : "BIOS'en har en anden værdi.");
        }).ToList();
    }
}
