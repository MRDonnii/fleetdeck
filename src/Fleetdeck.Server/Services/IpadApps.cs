using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Apps på iPads: ren logik uden database og netværk (så den kan testes) – App Stores opslags-svar (iTunes Lookup), versionssammenligning,
/// MDM-kommandoen InstalledApplicationList og koblingen mellem en app og en tjeneste i AdGuard.
/// </summary>
public static class AppStore
{
    public const string Country = "dk";
    public const string MdmRequestType = "InstalledApplicationList";
    /// <summary>Hvor længe App Stores oplysninger om en app gælder.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);

    public sealed record Info(bool Found, long? TrackId, string? Name, string? Version, string? IconUrl, string? StoreUrl, DateTime? Released);

    public static string LookupUrl(string bundleId) => $"https://itunes.apple.com/lookup?bundleId={Uri.EscapeDataString(bundleId)}&country={Country}";

    /// <summary>Svaret fra iTunes Lookup for ét bundle-id. Found=false, hvis appen ikke findes i butikken. Kaster JsonException ved ugyldig JSON.</summary>
    public static Info ParseLookup(string json, string bundleId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return new(false, null, null, null, null, null, null);
        JsonElement? hit = null;
        foreach (var r in results.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            if (Str(r, "bundleId") is { } b && b.Equals(bundleId, StringComparison.OrdinalIgnoreCase)) { hit = r; break; }
            hit ??= r;
        }
        if (hit is not { } a || Str(a, "version") is null && Str(a, "trackName") is null) return new(false, null, null, null, null, null, null);
        var released = Str(a, "currentVersionReleaseDate") is { } d && DateTime.TryParse(d, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : (DateTime?)null;
        long? track = a.TryGetProperty("trackId", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tid) ? tid : null;
        return new(true, track, Clip(Str(a, "trackName"), 200), Clip(Str(a, "version"), 64),
            Clip(Https(Str(a, "artworkUrl512") ?? Str(a, "artworkUrl100") ?? Str(a, "artworkUrl60")), 500), Clip(Https(Str(a, "trackViewUrl")), 500), released);
    }

    /// <summary>Sammenlign versioner som "1.10.2" og "1.9": tal for tal, manglende dele tæller som 0, og tekst efter tallet i en del ignoreres.</summary>
    public static int CompareVersions(string? a, string? b)
    {
        var x = Parts(a); var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Count, y.Count); i++)
        {
            var c = (i < x.Count ? x[i] : 0).CompareTo(i < y.Count ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>App Store har en nyere version end den installerede. Ukendte eller ulæselige versioner giver aldrig "forældet".</summary>
    public static bool IsOutdated(string? installed, string? store) =>
        Parts(installed).Count > 0 && Parts(store).Count > 0 && CompareVersions(store, installed) > 0;

    private static List<long> Parts(string? v)
    {
        var res = new List<long>();
        if (string.IsNullOrWhiteSpace(v)) return res;
        foreach (var p in v.Trim().Split('.'))
        {
            var digits = new string(p.Trim().TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0) break;   // "1.2 beta" → 1.2; "abc" → ingen version
            res.Add(long.TryParse(digits[..Math.Min(digits.Length, 18)], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0);
        }
        return res;
    }

    /// <summary>MDM-kommandoen, der beder iPad'en om alle installerede apps (ikke kun dem, MDM har installeret).</summary>
    public static MdmCommands.Built MdmListCommand()
    {
        var uuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var cmd = new PDict
        {
            { "RequestType", MdmRequestType },
            { "ManagedAppsOnly", false },
            { "Items", new List<object> { "Identifier", "Name", "ShortVersion", "Version", "Installing" } },
        };
        return new(uuid, MdmRequestType, Plist.Write(new PDict { { "Command", cmd }, { "CommandUUID", uuid } }));
    }

    /// <summary>Apps fra iPad'ens svar på InstalledApplicationList.</summary>
    public static List<IosAppInfo> MdmApps(Dictionary<string, object> report)
    {
        if (!report.TryGetValue("InstalledApplicationList", out var v) || v is not List<object> list) return [];
        return list.OfType<Dictionary<string, object>>()
            .Select(a => new IosAppInfo
            {
                BundleId = (a.Str("Identifier") ?? "").Trim(), Name = a.Str("Name")?.Trim(),
                Version = (a.Str("ShortVersion") ?? a.Str("Version"))?.Trim(), Build = a.Str("Version")?.Trim(),
            })
            .Where(a => a.BundleId.Length is > 0 and <= 200).GroupBy(a => a.BundleId, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    /// <summary>Kendte apps → AdGuards tjeneste-id (bruges kun, hvis tjenesten findes i AdGuard).</summary>
    private static readonly Dictionary<string, string> KnownServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["com.google.ios.youtube"] = "youtube", ["com.google.ios.youtubekids"] = "youtube", ["com.zhiliaoapp.musically"] = "tiktok",
        ["com.toyopagroup.picaboo"] = "snapchat", ["com.burbn.instagram"] = "instagram", ["com.burbn.barcelona"] = "threads",
        ["com.facebook.Facebook"] = "facebook", ["com.facebook.Messenger"] = "messenger", ["net.whatsapp.WhatsApp"] = "whatsapp",
        ["ph.telegra.Telegraph"] = "telegram", ["com.hammerandchisel.discord"] = "discord", ["com.roblox.robloxmobile"] = "roblox",
        ["com.epicgames.FortniteGame"] = "fortnite", ["com.netflix.Netflix"] = "netflix", ["tv.twitch"] = "twitch",
        ["com.mojang.minecraftpe"] = "minecraft", ["com.spotify.client"] = "spotify", ["com.atebits.Tweetie2"] = "twitter",
        ["pinterest"] = "pinterest", ["com.reddit.Reddit"] = "reddit", ["com.disney.disneyplus"] = "disneyplus",
        ["com.amazon.aiv.AIVApp"] = "amazon_streaming", ["com.supercell.magic"] = "clashofclans", ["com.supercell.laser"] = "brawlstars",
    };

    /// <summary>Firmaer, hvis domæne ikke siger noget om den enkelte app (com.google.ios.maps er ikke "Google-tjenesten").</summary>
    private static readonly HashSet<string> GenericVendors = new(StringComparer.OrdinalIgnoreCase) { "apple", "google", "microsoft", "amazon", "adobe", "samsung" };

    /// <summary>Den AdGuard-tjeneste, en app hører til (kendt app, samme navn, eller appens domæne i tjenestens regler) – ellers null.</summary>
    public static string? MatchService(string bundleId, string? appName, IEnumerable<(string Id, string Name, string[] Domains)> services)
    {
        var list = services.Where(s => s.Id.Length > 0).ToList();
        if (KnownServices.TryGetValue(bundleId, out var known) && list.Any(s => s.Id == known)) return known;
        var name = Norm(ShortName(appName));
        if (name.Length >= 3 && list.FirstOrDefault(s => Norm(s.Name) == name) is { Id.Length: > 0 } byName) return byName.Id;
        var parts = bundleId.Split('.');
        if (parts.Length >= 3 && parts[1].Length >= 3 && !GenericVendors.Contains(parts[1]))
        {
            var domain = $"{parts[1]}.{parts[0]}".ToLowerInvariant();
            if (list.FirstOrDefault(s => s.Domains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase))) is { Id.Length: > 0 } byDomain) return byDomain.Id;
        }
        return null;
    }

    /// <summary>"TikTok – Videoer og musik" → "TikTok".</summary>
    private static string ShortName(string? name)
    {
        var n = (name ?? "").Trim();
        var cut = n.IndexOfAny(['–', '—', ':', '|', '(']);
        var dash = n.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && (cut < 0 || dash < cut)) cut = dash;
        return cut > 0 ? n[..cut] : n;
    }

    private static string Norm(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string? Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
    private static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
    private static string? Https(string? url) => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http"
        ? (u.Scheme == "http" ? "https" + url[4..] : url) : null;
}

/// <summary>Gemmer en app-scanning af en iPad (fra Mac'en eller MDM). Gemmer ikke selv – det gør den, der kalder.</summary>
public static class IpadAppStore
{
    public const string ViaMac = "mac", ViaMdm = "mdm";

    public static async Task Apply(AppDb db, int ipadId, string source, List<IosAppInfo> apps, DateTime now)
    {
        apps = apps.Where(a => a.BundleId.Trim().Length is > 0 and <= 200).GroupBy(a => a.BundleId.Trim(), StringComparer.Ordinal)
            .Select(g => { var a = g.First(); a.BundleId = g.Key; return a; }).Take(2000).ToList();
        var existing = await db.IpadApps.Where(x => x.IpadId == ipadId).ToListAsync();
        var ids = apps.Select(a => a.BundleId).ToList();
        var store = await db.AppStoreApps.AsNoTracking().Where(s => ids.Contains(s.BundleId)).ToDictionaryAsync(s => s.BundleId, StringComparer.Ordinal);
        var byId = existing.ToDictionary(x => x.BundleId, StringComparer.Ordinal);
        foreach (var a in apps)
        {
            if (!byId.TryGetValue(a.BundleId, out var row)) { row = new IpadApp { IpadId = ipadId, BundleId = a.BundleId }; db.IpadApps.Add(row); byId[a.BundleId] = row; }
            row.Name = Clip(a.Name, 120) ?? row.Name;
            row.Version = Clip(a.Version, 64);
            row.Build = Clip(a.Build, 64);
            row.Seen = now;
            row.Outdated = store.TryGetValue(a.BundleId, out var s) && s.Found && AppStore.IsOutdated(row.Version, s.Version);
        }
        var keep = ids.ToHashSet(StringComparer.Ordinal);
        db.IpadApps.RemoveRange(existing.Where(x => !keep.Contains(x.BundleId)));
        var scan = await Scan(db, ipadId);
        scan.ScannedAt = now; scan.Source = source; scan.Error = null;
    }

    public static async Task Failed(AppDb db, int ipadId, string error)
    {
        var scan = await Scan(db, ipadId);
        var e = error.Trim().Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "Scanningen mislykkedes";
        scan.Error = Clip(e, 400);
    }

    public static async Task<IpadAppScan> Scan(AppDb db, int ipadId)
    {
        var scan = db.IpadAppScans.Local.FirstOrDefault(x => x.IpadId == ipadId) ?? await db.IpadAppScans.FirstOrDefaultAsync(x => x.IpadId == ipadId);
        if (scan is null) { scan = new IpadAppScan { IpadId = ipadId }; db.IpadAppScans.Add(scan); }
        return scan;
    }

    /// <summary>Antal forældede apps pr. iPad.</summary>
    public static Task<Dictionary<int, int>> OutdatedCounts(AppDb db) =>
        db.IpadApps.AsNoTracking().Where(x => x.Outdated).GroupBy(x => x.IpadId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);

    private static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}

/// <summary>
/// Henter App Stores nyeste version og ikon for de apps, iPads har (iTunes Lookup, dansk butik). Hver app slås højst op én gang i døgnet, og
/// opslagene spredes (ca. 20 i minuttet, Apples grænse). Beder også iPads under Apple-styring om deres app-liste én gang i døgnet.
/// </summary>
public sealed class AppStoreEngine(IServiceScopeFactory scopes, IHttpClientFactory http, ILogger<AppStoreEngine> log) : BackgroundService
{
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(3.2), RetryAfterError = TimeSpan.FromHours(1), Backoff = TimeSpan.FromMinutes(30),
        MdmScanEvery = TimeSpan.FromHours(24);
    private const int PerRound = 40;
    private readonly Channel<bool> kick = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private DateTime pausedUntil = DateTime.MinValue, lastMdm = DateTime.MinValue;
    public string? LastError { get; private set; }

    public void Kick() => kick.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var more = await Lookups(stop);
                await MdmScans(stop);
                if (more) continue;   // flere apps venter: fortsæt med det samme (opslagene er allerede spredt)
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)   // bevidst bred: en fejl her må ikke stoppe serveren
            {
                LastError = e.Message; log.LogWarning(e, "App Store-opslag: {Msg}", e.Message);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
            wait.CancelAfter(TimeSpan.FromSeconds(60));
            try { await kick.Reader.ReadAsync(wait.Token); await Task.Delay(1000, stop); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        }
    }

    /// <summary>Bundle-id'er på iPads, som App Store-cachen mangler eller har ældre end <paramref name="stale"/>.</summary>
    public static Task<List<string>> Due(AppDb db, DateTime stale, int take, CancellationToken ct = default) =>
        db.IpadApps.AsNoTracking().Select(x => x.BundleId).Distinct()
            .Where(b => !db.AppStoreApps.Any(s => s.BundleId == b && s.Checked > stale)).OrderBy(b => b).Take(take).ToListAsync(ct);

    /// <summary>Slår de apps op, der mangler eller er for gamle. true = der venter flere.</summary>
    private async Task<bool> Lookups(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < pausedUntil) return false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var stale = now - AppStore.CacheFor;
        var due = await Due(db, stale, PerRound + 1, ct);
        if (due.Count == 0) return false;
        var client = http.CreateClient("appstore");
        var changed = false;
        foreach (var bundle in due.Take(PerRound))
        {
            var row = await db.AppStoreApps.FirstOrDefaultAsync(s => s.BundleId == bundle, ct);
            if (row is null) { row = new AppStoreApp { BundleId = bundle }; db.AppStoreApps.Add(row); }
            try
            {
                using var res = await client.GetAsync(AppStore.LookupUrl(bundle), ct);
                if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    pausedUntil = DateTime.UtcNow + Backoff; LastError = $"App Store svarede {(int)res.StatusCode} – prøver igen om {Backoff.TotalMinutes:0} min.";
                    if (db.Entry(row).State == EntityState.Added) db.Entry(row).State = EntityState.Detached;
                    break;
                }
                res.EnsureSuccessStatusCode();
                var info = AppStore.ParseLookup(await res.Content.ReadAsStringAsync(ct), bundle);
                row.Found = info.Found; row.Error = null; row.Checked = DateTime.UtcNow;
                if (info.Found)
                {
                    row.TrackId = info.TrackId; row.Name = info.Name; row.Version = info.Version; row.IconUrl = info.IconUrl; row.StoreUrl = info.StoreUrl; row.Released = info.Released;
                }
                LastError = null;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // prøv igen om en time (Checked sættes, så den først er "for gammel" til den tid)
                row.Error = e.Message.Length > 300 ? e.Message[..300] : e.Message;
                row.Checked = DateTime.UtcNow - AppStore.CacheFor + RetryAfterError;
            }
            foreach (var app in await db.IpadApps.Where(x => x.BundleId == bundle).ToListAsync(ct))
            {
                var outdated = row.Found && AppStore.IsOutdated(app.Version, row.Version);
                if (app.Outdated != outdated) { app.Outdated = outdated; }
            }
            await db.SaveChangesAsync(ct);
            changed = true;
            await Task.Delay(Spacing, ct);
        }
        if (changed) await scope.ServiceProvider.GetRequiredService<Live>().Changed("ipads");
        return due.Count > PerRound && DateTime.UtcNow >= pausedUntil;
    }

    /// <summary>iPads under Apple-styring: bed om app-listen, når den er mere end et døgn gammel (uden push – den kommer ved næste kontakt).</summary>
    private async Task MdmScans(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - lastMdm < TimeSpan.FromMinutes(10)) return;
        lastMdm = now;
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDb>();
        var devices = await db.MdmDevices.AsNoTracking().Where(d => d.State == MdmStates.Enrolled && d.Approved && d.EnrollmentId != null).ToListAsync(ct);
        if (devices.Count == 0) return;
        var svc = sp.GetRequiredService<MdmService>();
        foreach (var d in devices)
        {
            var scan = await IpadAppStore.Scan(db, d.IpadId);
            var last = new[] { scan.ScannedAt, scan.Requested }.Max();
            if (last is { } l && now - l < MdmScanEvery) continue;
            if (await db.Ipads.FindAsync([d.IpadId], ct) is not { } p) continue;
            scan.Requested = now; scan.RequestedVia = IpadAppStore.ViaMdm;
            await db.SaveChangesAsync(ct);
            var tracked = await db.MdmDevices.FirstAsync(x => x.IpadId == d.IpadId, ct);
            try { await svc.Send(p, tracked, AppStore.MdmListCommand(), "Fleetdeck", "Automatisk scanning af apps", push: false, ct: ct); }
            catch (MdmException e) { log.LogInformation("Apps via Apple-styring ({Ipad}): {Msg}", p.Name, e.Message); }
        }
    }
}
