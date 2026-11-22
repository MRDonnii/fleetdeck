using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Opsætningsfilen: alt det, en bruger har skrevet ind (indstillinger, profilernes felter, maskinernes profil, mærker og
/// beskyttelse, netboot), som én JSON-fil – uden adgangskoder, nøgler, tokens og tilstand. Koden på GitHub er neutral; en
/// installation husker sin opsætning i databasen, og filen kan gemmes privat og læses ind igen efter en geninstallation.
/// Indlæsningen overskriver kun det, filen nævner, og opretter profiler, der mangler.
/// </summary>
public static partial class SiteConfig
{
    public const int Version = 1;

    /// <summary>Hemmeligheder – kommer aldrig med i filen og kan ikke sættes fra den.</summary>
    [GeneratedRegex(@"(pass|token|secret|nonce|cert|webhook|_key$|^krsso:|^terminal_pin_)", RegexOptions.IgnoreCase)]
    public static partial Regex Secret();

    /// <summary>Tilstand, som Fleetdeck selv skriver (tidspunkter, cache, planlægning) – hører ikke til opsætningen.</summary>
    [GeneratedRegex(@"^(backup_last_|backup_since$|catalog_imported$|sched:|terminal_.*_seeded$|update_cache$|update_checked$|update_state$|appwatch_rate$|kontrolrum_sso_created$|api_key_rejected$|server_url_env$|tunnel_nonce$|printer_found$|printer_search_at$)")]
    public static partial Regex State();

    public static bool Exportable(string key) => !Secret().IsMatch(key) && !State().IsMatch(key);

    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Profilens felter, der kan stå i filen: tekst, tal, sand/falsk og lister af tekst (ikke id og relationer).</summary>
    private static IEnumerable<PropertyInfo> ProfileFields() => typeof(Profile).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(Profile.Id) && p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute>() is null
                    && (p.PropertyType == typeof(string) || p.PropertyType == typeof(bool) || p.PropertyType == typeof(int) || p.PropertyType == typeof(List<string>)));

    public static async Task<JsonObject> Export(AppDb db, Paths paths)
    {
        var settings = new JsonObject();
        foreach (var s in await db.Settings.AsNoTracking().OrderBy(x => x.Key).ToListAsync())
            if (Exportable(s.Key) && s.Value is { Length: > 0 }) settings[s.Key] = s.Value;
        var profiles = new JsonArray();
        foreach (var p in await db.Profiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync())
        {
            var o = new JsonObject();
            foreach (var f in ProfileFields()) o[Camel(f.Name)] = JsonSerializer.SerializeToNode(f.GetValue(p), f.PropertyType);
            profiles.Add(o);
        }
        var names = (await db.Profiles.AsNoTracking().ToListAsync()).ToDictionary(p => p.Id, p => p.Name);
        var machines = new JsonArray();
        var netbootDir = Path.Combine(paths.Pxe, "pi", "hosts");
        foreach (var m in await db.Machines.AsNoTracking().OrderBy(x => x.Name).ToListAsync())
        {
            var o = new JsonObject { ["name"] = m.Name };
            if (m.DisplayName is { Length: > 0 }) o["displayName"] = m.DisplayName;
            if (m.ProfileId is int pid && names.TryGetValue(pid, out var pn)) o["profile"] = pn;
            if (m.Protected) o["protected"] = true;
            if (m.Tags.Count > 0) o["tags"] = JsonSerializer.SerializeToNode(m.Tags);
            if (m.UpgradeExclude.Count > 0) o["upgradeExclude"] = JsonSerializer.SerializeToNode(m.UpgradeExclude);
            if (m.PowerSwitch is { Length: > 0 }) o["powerSwitch"] = m.PowerSwitch;
            if (m.PowerSensor is { Length: > 0 }) o["powerSensor"] = m.PowerSensor;
            if (m.PowerMinWatts != PowerWatch.DefaultMinWatts) o["powerMinWatts"] = m.PowerMinWatts;
            if (MacAddr.Normalize(m.Mac) is { } mac && File.Exists(Path.Combine(netbootDir, MacAddr.Dash(mac)))) o["netboot"] = true;
            machines.Add(o);
        }
        return new JsonObject
        {
            ["fleetdeck"] = Version, ["exported"] = DateTime.UtcNow.ToString("O"),
            ["note"] = "Fleetdecks opsætning uden adgangskoder, nøgler og tokens. Gem den privat; læs den ind under Indstillinger → Backup.",
            ["settings"] = settings, ["profiles"] = profiles, ["machines"] = machines,
            ["printers"] = await ExportPrinters(db, names),
            ["apps"] = await ExportApps(db),
        };
    }

    public sealed record Applied(int Settings, int ProfilesChanged, int ProfilesCreated, int Machines, List<string> Skipped, int Printers = 0, int Apps = 0);

    public static async Task<Applied> Import(AppDb db, JsonObject doc)
    {
        if (doc["fleetdeck"]?.GetValue<int>() is not int v || v > Version) throw new InvalidDataException("Filen er ikke en Fleetdeck-opsætning (eller fra en nyere version)");
        var skipped = new List<string>();
        int nSettings = 0, changed = 0, created = 0, nMachines = 0;
        if (doc["settings"] is JsonObject so)
            foreach (var (k, node) in so)
            {
                if (!Exportable(k) || k.Length > 64) { skipped.Add($"indstilling {k}"); continue; }
                var val = node?.ToString() ?? "";
                var row = await db.Settings.FindAsync(k);
                if (row is null) db.Settings.Add(new Setting { Key = k, Value = val });
                else if (row.Value != val) row.Value = val; else continue;
                nSettings++;
            }
        if (doc["profiles"] is JsonArray pa)
            foreach (var node in pa.OfType<JsonObject>())
            {
                var name = node["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var p = await db.Profiles.FirstOrDefaultAsync(x => x.Name == name);
                var isNew = p is null;
                p ??= new Profile { Name = name };
                var dirty = false;
                foreach (var f in ProfileFields())
                {
                    if (node[Camel(f.Name)] is not { } jn) continue;
                    object? val;
                    try { val = jn.Deserialize(f.PropertyType, Opts); } catch (JsonException) { skipped.Add($"{name}: {f.Name}"); continue; }
                    if (val is null || Same(f.GetValue(p), val)) continue;
                    f.SetValue(p, val); dirty = true;
                }
                if (isNew) { db.Profiles.Add(p); created++; } else if (dirty) changed++;
            }
        await db.SaveChangesAsync();
        if (doc["machines"] is JsonArray ma)
        {
            var profiles = await db.Profiles.ToListAsync();
            foreach (var node in ma.OfType<JsonObject>())
            {
                var name = node["name"]?.ToString();
                var m = name is null ? null : await db.Machines.FirstOrDefaultAsync(x => x.Name == name);
                if (m is null) { if (name is not null) skipped.Add($"maskine {name} (ikke tilmeldt)"); continue; }
                if (node["displayName"]?.ToString() is { Length: > 0 } dn) m.DisplayName = dn;
                if (node["profile"]?.ToString() is { Length: > 0 } pn && profiles.FirstOrDefault(p => p.Name == pn) is { } prof) m.ProfileId = prof.Id;
                if (node["protected"] is { } pr) m.Protected = pr.GetValue<bool>();
                if (node["tags"] is JsonArray t) m.Tags = t.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
                if (node["upgradeExclude"] is JsonArray ue) m.UpgradeExclude = ue.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
                if (node["powerSwitch"]?.ToString() is { } psw && HaEntity.IsSwitch(psw)) m.PowerSwitch = psw;
                if (node["powerSensor"]?.ToString() is { } pse && HaEntity.IsSensor(pse)) m.PowerSensor = pse;
                if (node["powerMinWatts"] is JsonValue pmw && pmw.TryGetValue<int>(out var pw)) m.PowerMinWatts = Math.Clamp(pw, 1, 2000);
                nMachines++;
            }
            await db.SaveChangesAsync();
        }
        var nPrinters = doc["printers"] is JsonArray pra ? await ImportPrinters(db, pra, skipped) : 0;
        var nApps = doc["apps"] is JsonArray apa ? await ImportApps(db, apa, skipped) : 0;
        return new(nSettings, changed, created, nMachines, skipped, nPrinters, nApps);
    }

    /// <summary>Printerne og hvor de er lagt på (profil- og maskinnavne, ikke id'er). Status gemmes ikke.</summary>
    private static async Task<JsonArray> ExportPrinters(AppDb db, Dictionary<int, string> profileNames)
    {
        var machineNames = await db.Machines.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name);
        var list = new JsonArray();
        foreach (var p in await db.Printers.AsNoTracking().Include(x => x.Assignments).OrderBy(x => x.Name).ToListAsync())
        {
            var on = new JsonArray();
            foreach (var a in p.Assignments.OrderBy(a => a.Id))
            {
                var o = new JsonObject { ["makeDefault"] = a.MakeDefault, ["keep"] = a.Keep };
                if (a.ProfileId is int pid && profileNames.TryGetValue(pid, out var pn)) o["profile"] = pn;
                else if (a.MachineId is int mid && machineNames.TryGetValue(mid, out var mn)) o["machine"] = mn;
                else continue;
                on.Add(o);
            }
            list.Add(new JsonObject
            {
                ["name"] = p.Name, ["uri"] = p.Uri, ["location"] = p.Location, ["makeModel"] = p.MakeModel, ["paper"] = p.Paper, ["duplex"] = p.Duplex,
                ["color"] = p.Color, ["needsDriver"] = p.NeedsDriver, ["driver"] = p.Driver, ["on"] = on,
            });
        }
        return list;
    }

    /// <summary>Printere fra filen: findes en med samme adresse, rettes den; ellers oprettes den. Tildelinger tilføjes (fjernes aldrig).</summary>
    private static async Task<int> ImportPrinters(AppDb db, JsonArray arr, List<string> skipped)
    {
        var n = 0;
        var profiles = await db.Profiles.ToListAsync();
        var machines = await db.Machines.ToListAsync();
        foreach (var node in arr.OfType<JsonObject>())
        {
            string? S(string k) => node[k]?.ToString();
            bool B(string k, bool d) => node[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
            var name = Fleetdeck.Shared.PrinterNames.Clean(S("name"));
            if (Printers.PrinterService.Validate(name, S("uri"), S("paper") ?? "A4", S("duplex") ?? "long") is { } err) { skipped.Add($"printer {name}: {err}"); continue; }
            var uri = Fleetdeck.Shared.PrinterUri.Normalize(S("uri"))!;
            var p = await db.Printers.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Uri == uri);
            if (p is null) { p = new Printer { Uri = uri, Created = DateTime.UtcNow, Source = "manual" }; db.Printers.Add(p); }
            p.Name = name.Length > 100 ? name[..100] : name;
            p.Location = Cut(S("location"), 100); p.MakeModel = Cut(S("makeModel"), 200);
            p.Paper = S("paper") ?? p.Paper; p.Duplex = S("duplex") ?? p.Duplex; p.Color = B("color", p.Color);
            p.NeedsDriver = B("needsDriver", p.NeedsDriver); p.Driver = Cut(S("driver"), 200);
            await db.SaveChangesAsync();
            foreach (var on in (node["on"] as JsonArray ?? []).OfType<JsonObject>())
            {
                int? pid = on["profile"]?.ToString() is { } pn ? profiles.FirstOrDefault(x => x.Name == pn)?.Id : null;
                int? mid = pid is null && on["machine"]?.ToString() is { } mn ? machines.FirstOrDefault(x => x.Name == mn)?.Id : null;
                if (pid is null && mid is null) { skipped.Add($"printer {name}: {on["profile"] ?? on["machine"]} findes ikke"); continue; }
                if (p.Assignments.Any(a => a.ProfileId == pid && a.MachineId == mid)) continue;
                db.PrinterAssignments.Add(new PrinterAssignment
                {
                    PrinterId = p.Id, ProfileId = pid, MachineId = mid,
                    MakeDefault = on["makeDefault"] is JsonValue d && d.TryGetValue<bool>(out var dv) && dv,
                    Keep = on["keep"] is not JsonValue k || !k.TryGetValue<bool>(out var kv) || kv,
                });
            }
            await db.SaveChangesAsync();
            n++;
        }
        return n;
    }

    /// <summary>Apps (siden Apps): det, brugeren har skrevet ind – maskinen som navn. Ingen GitHub-nøgle, versioner eller udgivelser.</summary>
    private static async Task<JsonArray> ExportApps(AppDb db)
    {
        var machineNames = await db.Machines.AsNoTracking().ToDictionaryAsync(m => m.Id, m => m.Name);
        var list = new JsonArray();
        foreach (var a in await db.WatchedApps.AsNoTracking().OrderBy(x => x.Name).ToListAsync())
        {
            var o = new JsonObject
            {
                ["name"] = a.Name, ["repo"] = a.Repo, ["channel"] = a.Channel, ["placeKind"] = a.PlaceKind, ["placeName"] = a.PlaceName,
                ["readKind"] = a.ReadKind, ["readTarget"] = a.ReadTarget, ["readField"] = a.ReadField, ["updateCommand"] = a.UpdateCommand,
                ["autoUpdate"] = a.AutoUpdate, ["neverAuto"] = a.NeverAuto, ["notify"] = a.Notify,
            };
            if (a.MachineId is int mid && machineNames.TryGetValue(mid, out var mn)) o["machine"] = mn;
            list.Add(o);
        }
        return list;
    }

    /// <summary>Apps fra filen: en med samme navn og GitHub-projekt rettes, ellers oprettes den. Maskinen findes ud fra navnet.</summary>
    private static async Task<int> ImportApps(AppDb db, JsonArray arr, List<string> skipped)
    {
        var n = 0;
        var machines = await db.Machines.AsNoTracking().ToListAsync();
        foreach (var node in arr.OfType<JsonObject>())
        {
            string S(string k) => node[k]?.ToString() ?? "";
            bool B(string k, bool d) => node[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
            var name = S("name").Trim(); var repo = S("repo").Trim();
            int? mid = null;
            if (S("machine") is { Length: > 0 } mn)
            {
                mid = machines.FirstOrDefault(x => x.Name == mn)?.Id;
                if (mid is null) skipped.Add($"app {name}: maskinen {mn} er ikke tilmeldt");
            }
            var a = await db.WatchedApps.FirstOrDefaultAsync(x => x.Name == name && x.Repo == repo);
            var isNew = a is null;
            a ??= new WatchedApp();
            var before = (a.Name, a.Repo, a.Channel, a.MachineId, a.PlaceKind, a.PlaceName, a.ReadKind, a.ReadTarget, a.ReadField, a.UpdateCommand, a.AutoUpdate, a.NeverAuto, a.Notify);
            a.Name = name; a.Repo = repo; a.Channel = S("channel") is { Length: > 0 } ch ? ch : ReleaseFeed.Stable;
            a.MachineId = mid ?? (isNew ? null : a.MachineId);
            a.PlaceKind = S("placeKind") is { Length: > 0 } pk ? pk : "container"; a.PlaceName = S("placeName");
            a.ReadKind = S("readKind") is { Length: > 0 } rk ? rk : "none"; a.ReadTarget = S("readTarget"); a.ReadField = S("readField");
            a.UpdateCommand = S("updateCommand"); a.AutoUpdate = B("autoUpdate", false); a.NeverAuto = B("neverAuto", false); a.Notify = B("notify", true);
            AppWatch.Clean(a);
            if (AppWatch.Validate(a) is { } err)
            {
                skipped.Add($"app {name}: {err}");
                if (!isNew) await db.Entry(a).ReloadAsync();   // ret intet i den gamle
                continue;
            }
            if (isNew) db.WatchedApps.Add(a);
            else if (before == (a.Name, a.Repo, a.Channel, a.MachineId, a.PlaceKind, a.PlaceName, a.ReadKind, a.ReadTarget, a.ReadField, a.UpdateCommand, a.AutoUpdate, a.NeverAuto, a.Notify)) continue;
            await db.SaveChangesAsync();
            n++;
        }
        return n;
    }

    private static string Cut(string? s, int n) { var c = Fleetdeck.Shared.PrinterNames.Clean(s); return c.Length > n ? c[..n] : c; }

    private static bool Same(object? a, object b) =>
        a is IEnumerable ea && b is IEnumerable eb && a is not string ? ea.Cast<object>().SequenceEqual(eb.Cast<object>()) : Equals(a, b);

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];
}
