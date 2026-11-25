using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    public static string Ser<T>(T v) => JsonSerializer.Serialize(v, Web);
    public static T Des<T>(string? s) where T : new()
    {
        if (string.IsNullOrWhiteSpace(s)) return new T();
        try { return JsonSerializer.Deserialize<T>(s, Web) ?? new T(); } catch (JsonException) { return new T(); }
    }
}

public static class Crypto
{
    public static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    public static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static string Token(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private const int Iterations = 210_000;
    public static string HashPassword(string pw)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(pw, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToHexString(salt)}${Convert.ToHexString(h)}";
    }
    public static bool VerifyPassword(string pw, string stored)
    {
        var p = stored.Split('$');
        if (p.Length != 4 || !int.TryParse(p[1], out var it)) return false;
        var h = Rfc2898DeriveBytes.Pbkdf2(pw, Convert.FromHexString(p[2]), it, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromHexString(p[3]));
    }
}

public sealed class Paths
{
    public Paths() { }
    /// <summary>Til test: data og WinSetup-sharet i en midlertidig mappe.</summary>
    public Paths(string data, string winSetup) { Data = data; WinSetup = winSetup; }
    public string Data { get; } = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
    public string WinSetup { get; } = Environment.GetEnvironmentVariable("WINSETUP_DIR") ?? "/winsetup";
    public string AgentDir { get; } = Environment.GetEnvironmentVariable("AGENT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "agent");
    public string Installers => Path.Combine(WinSetup, "Installers");
    public string Pxe => Path.Combine(WinSetup, "PXE");
}

public sealed class SettingsService(AppDb db)
{
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["poll_seconds"] = "30", ["inventory_hours"] = "6", ["health_minutes"] = "30", ["server_url"] = "",
        ["share_path"] = "", ["share_user"] = "", ["share_pass"] = "", ["default_profile_id"] = "",
        ["enroll_key"] = "", ["webhook_url"] = "", ["alert_offline_enabled"] = "0", ["alert_offline_days"] = "1", ["alert_disk_pct"] = "10",
        ["alert_cpu_temp"] = "90", ["alert_disk_temp"] = "70", ["alert_backup_days"] = "7", ["alert_battery_pct"] = "60",
        ["pxe_http"] = "", ["pxe_default_user"] = "",
        ["default_extensions"] = "diskhelbred,fjernskrivebord",
        ["mqtt_host"] = "", ["mqtt_port"] = "1883", ["mqtt_user"] = "", ["mqtt_pass"] = "", ["mqtt_prefix"] = "homeassistant",
        // tillid mellem agent og server (docs/sikkerhed-agent.md): off | sign | enforce; global nøgle til/fra; fastlåst TLS-nøgle
        ["signed_jobs"] = "sign", ["enroll_key_enabled"] = "1", ["tls_pin"] = "",
        // https-adresse, som agenter med pinnet jobnøgle flytter til (installationer og ældre agenter bruger fortsat server_url)
        ["agent_https_url"] = "",
        // revisionslog: dage rækkerne gemmes (0 = altid)
        ["audit_retention_days"] = "365",
        // Beskeder (helpdesk): afsendernavnet brugeren ser, notifikation ved nye svar, skabeloner (JSON; tom = standardskabelonerne)
        ["helpdesk_sender"] = "IT-support", ["helpdesk_notify"] = "1", ["helpdesk_templates"] = "",
        // ESP-flash: hvor firmwarekataloget hentes (tom = intet katalog; brugeren sætter sit eget op). Nøglen (esp_catalog_token) gemmes krypteret.
        [EspCatalog.UrlSetting] = EspCatalog.DefaultUrl,
        // printere: søg selv på netværket hver 6. time (0 = fra)
        ["printer_auto_search"] = "1", ["printer_search_hours"] = "6",
        // "Anmod om adgang" uden nøgle: off | lan (kun hjemmenet og VPN) | on; højst åbne anmodninger; timer før de udløber
        [AccessRequestService.ModeSetting] = AccessMode.Lan, [AccessRequestService.MaxSetting] = "10", [AccessRequestService.HoursSetting] = "24",
    };
    public static readonly string[] Public = ["poll_seconds", "inventory_hours", "health_minutes", "server_url", "share_path",
        "share_user", "default_profile_id", "webhook_url", "alert_offline_enabled", "alert_offline_days", "alert_disk_pct", "alert_cpu_temp",
        "alert_disk_temp", "alert_backup_days", "alert_battery_pct", "pxe_http", "pxe_default_user", "mqtt_host", "mqtt_port", "mqtt_user", "mqtt_prefix", "default_extensions",
        "signed_jobs", "enroll_key_enabled", "tls_pin", "agent_https_url",
        AccessRequestService.ModeSetting, AccessRequestService.MaxSetting, AccessRequestService.HoursSetting];

    /// <summary>Indstillinger i <see cref="Public"/>, som kun administratorer ser (en webhook-adresse bærer ofte et token).</summary>
    public static readonly string[] AdminOnly = ["webhook_url"];

    public async Task<string> Get(string key)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key);
        return s?.Value ?? Defaults.GetValueOrDefault(key, "");
    }
    public async Task<double> GetNum(string key, double fallback)
        => double.TryParse(await Get(key), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public async Task Set(string key, string value)
    {
        var s = await db.Settings.FindAsync(key);
        if (s is null) db.Settings.Add(new Setting { Key = key, Value = value }); else s.Value = value;
        await db.SaveChangesAsync();
    }
    public async Task<int?> DefaultProfileId() => int.TryParse(await Get("default_profile_id"), out var v) ? v : null;
}

public sealed class AuditService(AppDb db)
{
    public async Task Log(string user, string action, string detail = "", int? machineId = null)
    {
        // rækker, som alle uden login kan udløse (en forkert tilmeldingsnøgle), må ikke kunne fylde databasen
        if (action == "enroll.bad_key" && !UnauthenticatedAuditLimit.Allow(DateTime.UtcNow)) return;
        db.Audit.Add(Entry(user, action, detail, machineId));
        await db.SaveChangesAsync();
    }

    public static AuditEntry Entry(string user, string action, string detail, int? machineId = null) =>
        new() { User = user, Action = action, Detail = detail.Length > 1000 ? detail[..1000] : detail, MachineId = machineId };
}

/// <summary>Live-beskeder: browseren opdaterer sig, og agenten vækkes når der er nye jobs.</summary>
public sealed class Live(IHubContext<UiHub> ui, IHubContext<AgentHub> agents)
{
    public Task Changed(string kind, int? machineId = null) =>
        ui.Clients.Groups(UiHub.GroupsForKind(kind)).SendAsync(UiHubMethods.Changed, new { kind, machineId });
    public Task Progress(int jobId, int machineId, int? progress, string step, DateTime? stepSince = null) =>
        ui.Clients.Group(UiHub.Staff).SendAsync(UiHubMethods.Progress, new { jobId, machineId, progress, step, stepSince });
    public Task Wake(int machineId) => agents.Clients.Group(AgentHub.Group(machineId)).SendAsync(AgentHubMethods.Wake);
}

public sealed class JobService(AppDb db, Live live)
{
    public static readonly Dictionary<string, (string Title, string RunAs)> WinSetupSteps = new()
    {
        ["Capture"] = ("Gem baggrund og opsætning", RunAs.UserAdmin),
        ["CaptureDrivers"] = ("Gem drivere", RunAs.System),
        ["Backup"] = ("Backup af filer og indstillinger", RunAs.UserAdmin),
        ["Restore"] = ("Gendan filer og indstillinger", RunAs.UserAdmin),
        ["Appearance"] = ("Læg baggrund og opsætning på", RunAs.UserAdmin),
        ["Drivers"] = ("Installér drivere", RunAs.System),
    };

    public async Task<Job> Create(int machineId, string type, string title, JobParams? p = null, string runAs = RunAs.System,
        int? packageId = null, string source = "manual", string by = "system", bool save = true)
    {
        // Opdateringer: send pakkernes "Kør som" med, så fx pr.-bruger-programmer opdateres i brugerens session
        if (type is JobTypes.Upgrade or JobTypes.UpdateRound)
        {
            p ??= new JobParams();
            var map = await db.Packages.Where(x => (x.Type == PackageTypes.Winget || x.Type == PackageTypes.MsStore) && x.RunAs != RunAs.System && x.Identifier != "")
                .Select(x => new { x.Identifier, x.RunAs }).ToListAsync();
            if (map.Count > 0) p.RunAsById = map.GroupBy(x => x.Identifier, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().RunAs, StringComparer.OrdinalIgnoreCase);
            // programmer, der aldrig må opdateres (alle maskiner, denne maskine, profilen)
            // … og dem, der er skjult i Fleetdeck eller ikke kan installeres (UpgradeMarks)
            if (p.WingetId is null) p.Exclude = (p.Exclude ?? []).Concat((await UpgradeLocks.For(db, machineId)).Keys).Concat(await UpgradeMarks.Exclude(db, machineId))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        var j = new Job
        {
            MachineId = machineId, Type = type, Title = title, ParamsJson = Json.Ser(p ?? new JobParams()), RunAs = runAs,
            PackageId = packageId, Source = source, CreatedBy = by,
        };
        db.Jobs.Add(j);
        // revisionslog: jobs, en person har bedt om (planlæggeren og systemet skriver ikke en række pr. job)
        if (by is not ("system" or "planlægger") && source != "schedule")
            db.Audit.Add(AuditService.Entry(by, "job.create", $"{title} ({type})", machineId));
        if (save) await Commit(machineId);
        return j;
    }

    public async Task Commit(int machineId)
    {
        await db.SaveChangesAsync();
        await live.Wake(machineId);
        await live.Changed("jobs", machineId);
    }

    public static JobParams PackageParams(Package p) => new()
    {
        Name = p.Name, PackageType = p.Type, Identifier = p.Identifier, Args = p.Args, Detect = p.Detect,
        FileUrl = p.Type == PackageTypes.File ? $"/api/agent/file/{p.Id}" : null,
    };

    public Task<Job> ForPackage(int machineId, Package p, string action, string source = "manual", string by = "system", bool save = true)
    {
        var verb = action == JobTypes.Install ? (p.Type == PackageTypes.Steam ? "Installér spil" : "Installér") : "Afinstallér";
        var runAs = PackageTypes.IsUserOnly(p.Type) ? RunAs.User : p.RunAs;
        return Create(machineId, action, $"{verb} {p.Name}", PackageParams(p), runAs, p.Id, source, by, save);
    }

    public Task<Job> WinSetup(int machineId, string step, string source = "manual", string by = "system", bool save = true)
    {
        var (title, runAs) = WinSetupSteps[step];
        return Create(machineId, JobTypes.WinSetup, title, new JobParams { Step = step }, runAs, null, source, by, save);
    }
}

/// <summary>
/// Højst <see cref="PerWindow"/> uautentificerede revisionsrækker (fx "enroll.bad_key") pr. 10 minutter i alt. Resten droppes –
/// rate limits pr. IP kan omgås med mange adresser, en fælles grænse kan ikke.
/// </summary>
public static class UnauthenticatedAuditLimit
{
    public const int PerWindow = 50;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly Lock Gate = new();
    private static DateTime _start = DateTime.MinValue;
    private static int _count;

    public static bool Allow(DateTime now)
    {
        lock (Gate)
        {
            if (now - _start >= Window || now < _start) { _start = now; _count = 0; }
            return ++_count <= PerWindow;
        }
    }

    /// <summary>Kun til test.</summary>
    public static void Reset() { lock (Gate) { _start = DateTime.MinValue; _count = 0; } }
}
