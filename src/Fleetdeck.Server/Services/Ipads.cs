using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Hvilke iPads en bruger ser, og hvad brugeren må (administratorer ser og må alt).</summary>
public sealed record FamilyScope(bool Admin, bool All, HashSet<int> Ids, HashSet<string> Perms)
{
    public bool Sees(int id) => Admin || All || Ids.Contains(id);
    /// <summary>
    /// Forældre kan kun det, der styrer barnet: pause/ekstra tid (pause) og regler (rules). Alt om selve enheden – Mac/USB, backup,
    /// Apple-styring, opdatering, sletning, profiler – kræver administrator, også hvis brugeren tidligere fik "update", "lock" eller "erase".
    /// </summary>
    public bool Can(string perm) => Admin || (Perms.Contains(perm) && FamilyPerms.Parent.Contains(perm));
    public string[] PermList => Admin ? FamilyPerms.All : [.. Perms.Where(FamilyPerms.Parent.Contains)];

    public static async Task<FamilyScope> Of(ClaimsPrincipal p, AppDb db)
    {
        if (p.IsInRole(Roles.Admin)) return new(true, true, [], [.. FamilyPerms.All]);
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.UserId());
        return u is null || u.Role != Roles.Parent ? new(false, false, [], []) : new(false, u.IpadAll, [.. u.Ipads], [.. u.Perms]);
    }
}

/// <summary>Det, en iPad skal have lige nu: tilstand, YouTube og hvornår næste skift sker.</summary>
public sealed record IpadState(string Mode, DateTime? Until, DateTime? NextChange, string NextMode, string Yt, int YtUsed, int YtLimit, bool Extra,
    string Kids = KidsModes.Allowed, int KidsUsed = 0, int KidsLimit = 0);

/// <summary>Dagens minutter: Pool er hele AdGuard-tjenesten "youtube" (begge apps og fælles adresser), Main og Kids kun adresser, der hører til én app.</summary>
public readonly record struct YtUsage(int Pool, int Main, int Kids);

/// <summary>Hvad YouTube og YouTube Kids tilsammen giver af regler i AdGuard.</summary>
public sealed record YtPlan(bool BlockService, bool KidsOnlyRules, bool BlockKids, string? RestrictTarget);

/// <summary>Reglerne omsat til AdGuard: spærrede tjenester, egne regler og filtre for én klient.</summary>
public sealed record IpadDesired(string Name, List<string> Services, List<string> Rules, bool Parental, bool SafeSearch, bool Child, string? LanIp = null)
{
    public string Hash(string clientId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{Name}|{clientId}|{LanIp}|{string.Join(',', Services)}|{string.Join('\n', Rules)}|{Parental}|{SafeSearch}|{Child}")))[..32];
}

/// <summary>Ren logik (uden database og netværk), så den kan testes.</summary>
public static class IpadPolicy
{
    public const int Slots = 336;
    /// <summary>Det, en iPad altid må, også ved sengetid og pause: Apples tjenester (beskeder, FaceTime, opdateringer, iCloud).</summary>
    public static readonly string[] Essentials = ["apple.com", "icloud.com", "icloud-content.com", "apple-cloudkit.com", "mzstatic.com", "aaplimg.com",
        "apple-dns.net", "cdn-apple.com", "apple.news", "itunes.com", "push-apple.com.akadns.net"];
    /// <summary>Googles adresser, der giver YouTube i Begrænset tilstand via DNS.</summary>
    public static readonly string[] YtHosts = ["www.youtube.com", "m.youtube.com", "youtubei.googleapis.com", "youtube.googleapis.com", "www.youtube-nocookie.com"];

    // ── "Kun YouTube Kids" ─────────────────────────────────────────────────────────────────────────────────────────────
    // AdGuards tjeneste "youtube" (HostlistsRegistry services.json) indeholder også youtubekids.com, googlevideo.com, ytimg.com,
    // ggpht.com og youtubei.googleapis.com – den spærrer altså YouTube Kids med. I denne tilstand bruges den derfor IKKE; i stedet
    // spærres kun det, der er unikt for almindelig YouTube. YouTube Kids-appen deler dog API (youtubei.googleapis.com), video
    // (*.googlevideo.com) og billeder (*.ytimg.com, *.ggpht.com) med den almindelige YouTube-app, så med DNS alene kan de ikke
    // skilles helt ad:
    //  • YouTube i browseren (youtube.com, m.youtube.com, youtu.be, indlejrede videoer) spærres.
    //  • Den almindelige YouTube-app taler mest med youtubei.googleapis.com og kan derfor stadig virke. Den fælles API tvinges i
    //    stedet i streng "Begrænset tilstand" (restrict.youtube.com), så appen i værste fald kun viser børnevenligt indhold.
    //    Den sikre løsning er at skjule appen (com.google.ios.youtube) i den låste profil eller med Skærmtid.
    // Dagsgrænse og YouTube-tider gælder også YouTube Kids: uden for tiden spærres tjenesten "youtube", som rammer begge.

    /// <summary>Domæner, der kun bruges af almindelig YouTube (ikke af YouTube Kids) – spærres helt, inkl. underdomæner.</summary>
    public static readonly string[] YtOnlyDomains = ["youtube.com", "youtu.be", "yt.be", "youtube-nocookie.com", "youtubeembeddedplayer.googleapis.com",
        "youtube.dk", "youtubegaming.com", "youtubego.com", "withyoutube.com"];
    /// <summary>API-adresser, som YouTube Kids deler med den almindelige app – de spærres ikke, men tvinges i streng Begrænset tilstand.</summary>
    public static readonly string[] YtSharedApiHosts = ["youtubei.googleapis.com", "youtube.googleapis.com"];
    /// <summary>Det, YouTube Kids skal bruge (holdes åbent; nævnt her for dokumentation og tests).</summary>
    public static readonly string[] YtKidsDomains = ["youtubekids.com", "googlevideo.com", "ytimg.com", "ggpht.com", "youtubei.googleapis.com"];
    /// <summary>Googles adresser for Begrænset tilstand. De ligger under youtube.com og skal undtages fra spærringen ovenfor,
    /// ellers rammer AdGuards CNAME-tjek selve omskrivningen.</summary>
    public static readonly string[] YtRestrictTargets = ["restrict.youtube.com", "restrictmoderate.youtube.com"];
    public static readonly string[] DefaultSchoolGroups = ["gaming", "social_network", "streaming"];
    /// <summary>Det eneste domæne, der kun bruges af YouTube Kids.</summary>
    public const string KidsDomain = "youtubekids.com";
    /// <summary>Forbrug pr. app gemmes som egne rækker ved siden af AdGuards tjenester.</summary>
    public const string AppPrefix = "app:", AppYoutube = AppPrefix + "youtube", AppKids = AppPrefix + "kids";

    /// <summary>
    /// Hvilken app et opslag hører til. Fælles adresser (youtubei.googleapis.com, googlevideo.com, ytimg.com, ggpht.com) giver null:
    /// DNS kan ikke se, hvilken af de to apps der spørger, så de tæller ikke for nogen af dem.
    /// </summary>
    public static string? YtAppOf(string host)
    {
        var h = host.TrimEnd('.').ToLowerInvariant();
        static bool Under(string h, string d) => h == d || h.EndsWith("." + d, StringComparison.Ordinal);
        if (Under(h, KidsDomain)) return AppKids;
        if (YtRestrictTargets.Contains(h)) return null;
        return YtOnlyDomains.Any(d => Under(h, d)) ? AppYoutube : null;
    }

    /// <summary>
    /// Reglerne for alle kombinationer. AdGuards tjeneste "youtube" rammer også YouTube Kids, så den bruges kun, når begge apps er spærret.
    /// En spærret tjeneste (fx valgt under Regler eller i skoletid) gælder altid begge apps.
    /// </summary>
    public static YtPlan PlanYoutube(string yt, string kids, bool serviceBlocked = false)
    {
        var ytOff = serviceBlocked || yt is YtModes.Blocked or YtModes.KidsOnly or "window" or "quota" or "school";
        var kidsOff = serviceBlocked || kids != KidsModes.Allowed;
        if (ytOff) return kidsOff ? new(true, false, true, null) : new(false, true, false, null);
        var target = yt == YtModes.Strict ? "restrict.youtube.com" : yt == YtModes.Moderate ? "restrictmoderate.youtube.com" : null;
        return new(false, false, kidsOff, target);
    }

    /// <summary>Ét klik på kortet. Var hele tjenesten spærret, forbliver den anden app spærret, når tjenesten fjernes.</summary>
    public static void SetApp(Ipad p, string app, bool allowed)
    {
        var serviceWasBlocked = p.BlockedServices.Contains("youtube");
        if (p.YtMode == YtModes.KidsOnly) { p.YtMode = YtModes.Blocked; p.KidsMode = KidsModes.Allowed; }
        if (app == AppKids) p.KidsMode = allowed ? KidsModes.Allowed : KidsModes.Blocked;
        else if (!allowed) p.YtMode = YtModes.Blocked;
        else if (p.YtMode == YtModes.Blocked) p.YtMode = p.Kind == IpadKinds.Child ? YtModes.Moderate : YtModes.Open;
        if (!allowed || !serviceWasBlocked) return;
        p.BlockedServices = [.. p.BlockedServices.Where(s => s != "youtube")];
        if (app == AppKids) p.YtMode = YtModes.Blocked; else p.KidsMode = KidsModes.Blocked;
    }

    public static string ClientName(int id) => $"fd-ipad-{id}";

    public static int SlotOf(DateTime local) => ((int)local.DayOfWeek + 6) % 7 * 48 + local.Hour * 2 + local.Minute / 30;

    public static string Norm(string? s, char fill = '0') => s is { Length: Slots } ? s : (s ?? "").PadRight(Slots, fill)[..Slots];

    /// <summary>Standard for et barn: sengetid kl. 20–07 før skoledage og 21–08 i weekenden.</summary>
    public static string DefaultBedtime()
    {
        var c = new char[Slots]; Array.Fill(c, '0');
        for (var d = 0; d < 7; d++)
        {
            var nextIsSchool = d is <= 3 or 6;      // søn–tor aften
            var eve = nextIsSchool ? 40 : 42;       // 20:00 / 21:00
            for (var s = eve; s < 48; s++) c[d * 48 + s] = 'b';
            var morning = d is 5 or 6 ? 16 : 14;    // 08:00 lør/søn, ellers 07:00
            for (var s = 0; s < morning; s++) c[d * 48 + s] = 'b';
        }
        return new string(c);
    }

    private static DateTime SlotStart(DateTime local) => new(local.Year, local.Month, local.Day, local.Hour, local.Minute < 30 ? 0 : 30, 0, local.Kind);

    /// <summary>Første tidspunkt efter nu, hvor skemaet skifter (inden for en uge).</summary>
    public static (DateTime? At, char To) Next(string sched, DateTime local)
    {
        sched = Norm(sched);
        var i = SlotOf(local); var cur = sched[i]; var t = SlotStart(local);
        for (var k = 1; k <= Slots; k++)
        {
            var c = sched[(i + k) % Slots];
            if (c != cur) return (t.AddMinutes(30 * k), c);
        }
        return (null, cur);
    }

    public static IpadState State(Ipad p, DateTime utcNow, TimeZoneInfo tz, int ytUsedToday) => State(p, utcNow, tz, new YtUsage(ytUsedToday, 0, 0));

    public static IpadState State(Ipad p, DateTime utcNow, TimeZoneInfo tz, YtUsage use)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        var sched = Norm(p.Schedule);
        var c = sched[SlotOf(local)];
        var extra = p.FreeUntil > utcNow && c is 'b' or 's';
        var (nextLocal, nextC) = Next(sched, local);
        DateTime? next = nextLocal is { } n ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(n, DateTimeKind.Unspecified), tz) : null;
        string mode; DateTime? until = null;
        if (p.PauseUntil > utcNow) { mode = "pause"; until = p.PauseUntil; }
        else if (extra) { mode = "free"; until = p.FreeUntil; }
        else mode = c switch { 'b' => "bed", 's' => "school", _ => "free" };
        var nextMode = nextC switch { 'b' => "bed", 's' => "school", _ => "free" };

        var limit = p.YtDailyMin <= 0 ? 0 : p.YtDailyMin + (p.YtBonusDay == DateOnly.FromDateTime(local) ? p.YtBonusMin : 0);
        var linked = p.KidsLinked || p.YtMode == YtModes.KidsOnly;
        var ytUsedToday = linked ? use.Pool : use.Main;
        string yt;
        if (p.YtMode == YtModes.Blocked) yt = "blocked";
        else if (mode is "pause" or "bed") yt = "blocked";
        else if (mode == "school") yt = "school";
        else if (!string.IsNullOrEmpty(p.YtWindows) && Norm(p.YtWindows)[SlotOf(local)] != '1') yt = "window";
        else if (limit > 0 && ytUsedToday >= limit) yt = "quota";
        else yt = p.YtMode;   // open | moderate | strict | kids

        var kidsWindows = linked ? p.YtWindows : p.KidsWindows;
        var kidsLimit = linked ? limit : Math.Max(0, p.KidsDailyMin);
        var kidsUsed = linked ? use.Pool : use.Kids;
        string kids;
        if (p.YtMode != YtModes.KidsOnly && p.KidsMode == KidsModes.Blocked) kids = KidsModes.Blocked;
        else if (mode is "pause" or "bed") kids = KidsModes.Blocked;
        else if (mode == "school") kids = "school";
        else if (!string.IsNullOrEmpty(kidsWindows) && Norm(kidsWindows)[SlotOf(local)] != '1') kids = "window";
        else if (kidsLimit > 0 && kidsUsed >= kidsLimit) kids = "quota";
        else kids = KidsModes.Allowed;
        return new(mode, until, next, nextMode, yt, ytUsedToday, limit, extra, kids, kidsUsed, kidsLimit);
    }

    public static IpadDesired Build(Ipad p, IpadState st, IReadOnlyDictionary<string, string> serviceGroups, IReadOnlyCollection<string> schoolGroups)
    {
        var name = ClientName(p.Id);
        var mod = $"$client={name}";
        var services = new SortedSet<string>(p.BlockedServices, StringComparer.Ordinal);
        if (st.Mode == "school")
            foreach (var (id, g) in serviceGroups) if (schoolGroups.Contains(g)) services.Add(id);
        var plan = PlanYoutube(st.Yt, st.Kids, services.Contains("youtube"));
        if (plan.BlockService) services.Add("youtube");

        var rules = new List<string>();
        foreach (var d in Domains(p.BlockedDomains)) rules.Add($"||{d}^{mod}");
        if (st.Mode is "pause" or "bed")
        {
            rules.Add($"/.*/{mod}");
            foreach (var d in Essentials.Concat(Domains(p.AllowedDomains)).Distinct()) rules.Add($"@@||{d}^{mod}");
        }
        else
        {
            foreach (var d in Domains(p.AllowedDomains)) rules.Add($"@@||{d}^{mod}");
            if (plan.BlockKids) rules.Add($"||{KidsDomain}^{mod}");
            // Er YouTube spærret som tjeneste, må Begrænset tilstand ikke sende www.youtube.com videre: omskrivningen vinder ellers over spærringen
            if (plan.RestrictTarget is { } target)
            {
                foreach (var h in YtHosts) rules.Add($"|{h}^$dnsrewrite=NOERROR;CNAME;{target},client={name}");
            }
            else if (plan.KidsOnlyRules)
            {
                // se kommentaren ved YtOnlyDomains: browser-YouTube spærres, den fælles API tvinges i streng Begrænset tilstand
                foreach (var d in YtOnlyDomains) rules.Add($"||{d}^{mod}");
                foreach (var h in YtSharedApiHosts) rules.Add($"|{h}^$dnsrewrite=NOERROR;CNAME;restrict.youtube.com,client={name}");
                foreach (var t in YtRestrictTargets) rules.Add($"@@||{t}^{mod}");
            }
        }
        return new(name, [.. services], rules, p.FilterAdult, p.SafeSearch, p.Kind == IpadKinds.Child, p.LanIp);
    }

    /// <summary>Rens domæner: "https://www.Skole.dk/x" → "www.skole.dk".</summary>
    public static IEnumerable<string> Domains(IEnumerable<string> list)
    {
        foreach (var raw in list)
        {
            var d = raw.Trim().ToLowerInvariant();
            if (d.Contains("://")) d = d[(d.IndexOf("://", StringComparison.Ordinal) + 3)..];
            d = d.Split('/', '?', '#')[0].TrimStart('*', '.').TrimEnd('.');
            if (d.Length is > 2 and < 254 && d.Contains('.') && d.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-')) yield return d;
        }
    }
}

/// <summary>Tjenestekataloget fra AdGuard (YouTube, TikTok, Roblox …) og dets domæner, så trafik kan henføres til en tjeneste.</summary>
public sealed class ServiceCatalog
{
    public sealed record Svc(string Id, string Name, string Group, string? Icon, string[] Domains);
    public List<Svc> Items { get; init; } = [];
    public Dictionary<string, string> Groups => Items.ToDictionary(s => s.Id, s => s.Group);
    private Dictionary<string, string>? byDomain;

    public string? Classify(string host)
    {
        byDomain ??= Items.SelectMany(s => s.Domains.Select(d => (d, s.Id))).GroupBy(x => x.d).ToDictionary(g => g.Key, g => g.First().Id);
        var h = host.TrimEnd('.').ToLowerInvariant();
        while (true)
        {
            if (byDomain.TryGetValue(h, out var id)) return id;
            var i = h.IndexOf('.');
            if (i < 0) return null;
            h = h[(i + 1)..];
        }
    }

    public static ServiceCatalog Parse(JsonNode? root)
    {
        var list = new List<Svc>();
        foreach (var s in root?["blocked_services"]?.AsArray() ?? [])
        {
            if (s is null) continue;
            var doms = (s["rules"]?.AsArray() ?? []).Select(r => r?.GetValue<string>() ?? "")
                .Where(r => r.StartsWith("||", StringComparison.Ordinal)).Select(r => r[2..].Split('^', '$')[0].TrimStart('*', '.'))
                .Where(d => d.Contains('.') && !d.Contains('*')).Distinct().ToArray();
            list.Add(new(s["id"]?.GetValue<string>() ?? "", s["name"]?.GetValue<string>() ?? "", s["group_id"]?.GetValue<string>() ?? "", s["icon_svg"]?.GetValue<string>(), doms));
        }
        // YouTube Kids tæller med i YouTubes dagsgrænse: AdGuards "youtube" har youtubekids.com i dag, men sikr det, hvis listen ændres
        if (list.FirstOrDefault(x => x.Id == "youtube") is { } yt && !yt.Domains.Contains("youtubekids.com"))
            list[list.IndexOf(yt)] = yt with { Domains = [.. yt.Domains, "youtubekids.com"] };
        return new ServiceCatalog { Items = list.Where(x => x.Id.Length > 0).ToList() };
    }
}

/// <summary>AdGuard Home's API (Basic-login). Kun det Fleetdeck bruger: klienter, egne regler, tjenester og forespørgselsloggen.</summary>
public sealed class AdGuardClient(string baseUrl, string user, string pass)
{
    public const string Begin = "! >>> Fleetdeck iPads – styres automatisk, ret ikke her";
    public const string End = "! <<< Fleetdeck iPads slut";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private HttpRequestMessage Req(HttpMethod m, string path, JsonNode? body = null)
    {
        var r = new HttpRequestMessage(m, baseUrl.TrimEnd('/') + path);
        r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
        if (body is not null)
        {
            // AdGuard godtager kun præcis "application/json" (uden charset)
            r.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return r;
    }

    private async Task<JsonNode?> Send(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        using var res = await Http.SendAsync(Req(m, path, body), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) throw new InvalidOperationException("AdGuard afviste brugernavn/adgangskode");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"AdGuard svarede {(int)res.StatusCode}: {text.Trim()[..Math.Min(200, text.Trim().Length)]}");
        return text.Length > 0 && text.TrimStart() is ['{' or '[', ..] ? JsonNode.Parse(text) : null;
    }

    public Task<JsonNode?> Status(CancellationToken ct) => Send(HttpMethod.Get, "/control/status", null, ct);
    /// <summary>Et vilkårligt kald til AdGuards API (bruges af Reserve-DNS-synkroniseringen).</summary>
    public Task<JsonNode?> Call(HttpMethod m, string path, JsonNode? body, CancellationToken ct) => Send(m, path, body, ct);
    public Task<JsonNode?> Services(CancellationToken ct) => Send(HttpMethod.Get, "/control/blocked_services/all", null, ct);
    public Task<JsonNode?> Clients(CancellationToken ct) => Send(HttpMethod.Get, "/control/clients", null, ct);

    public async Task<List<string>> UserRules(CancellationToken ct) =>
        ((await Send(HttpMethod.Get, "/control/filtering/status", null, ct))?["user_rules"]?.AsArray() ?? []).Select(x => x?.GetValue<string>() ?? "").ToList();

    public Task SetUserRules(IEnumerable<string> rules, CancellationToken ct) =>
        Send(HttpMethod.Post, "/control/filtering/set_rules", new JsonObject { ["rules"] = new JsonArray([.. rules.Select(r => (JsonNode)r)]) }, ct);

    /// <summary>AdGuard gemmer et ID på 12 eller 16 hextegn som en MAC-adresse (fd:1e:…), og så matcher det aldrig ClientID'et i DoH-adressen.</summary>
    public static bool LooksLikeMac(string? id) =>
        id is { Length: 12 or 16 } && id.All(Uri.IsHexDigit);

    public static JsonObject ClientBody(IpadDesired d, string clientId)
    {
        var tags = new JsonArray("device_tablet");
        if (d.Child) tags.Add("user_child");
        return new JsonObject
        {
            ["name"] = d.Name, ["ids"] = d.LanIp is { Length: > 0 } ip ? new JsonArray(clientId, ip) : new JsonArray(clientId), ["tags"] = tags,
            ["use_global_settings"] = false, ["filtering_enabled"] = true, ["parental_enabled"] = d.Parental, ["safebrowsing_enabled"] = true,
            ["safe_search"] = new JsonObject
            {
                ["enabled"] = d.SafeSearch, ["bing"] = true, ["duckduckgo"] = true, ["ecosia"] = true, ["google"] = true, ["pixabay"] = true, ["yandex"] = true,
                ["youtube"] = false,   // YouTube styrer Fleetdeck selv (moderat/streng/tider)
            },
            ["use_global_blocked_services"] = false, ["blocked_services"] = new JsonArray([.. d.Services.Select(s => (JsonNode)s)]),
            ["blocked_services_schedule"] = new JsonObject { ["time_zone"] = "Local" },
            ["upstreams"] = new JsonArray(), ["ignore_querylog"] = false, ["ignore_statistics"] = false,
            ["upstreams_cache_enabled"] = false, ["upstreams_cache_size"] = 0,
        };
    }

    public async Task Upsert(IpadDesired d, string clientId, bool exists, CancellationToken ct)
    {
        var body = ClientBody(d, clientId);
        if (exists) await Send(HttpMethod.Post, "/control/clients/update", new JsonObject { ["name"] = d.Name, ["data"] = body }, ct);
        else await Send(HttpMethod.Post, "/control/clients/add", body, ct);
    }

    public Task Delete(string name, CancellationToken ct) => Send(HttpMethod.Post, "/control/clients/delete", new JsonObject { ["name"] = name }, ct);

    /// <summary>Forespørgsler fra én klient (nyeste først).</summary>
    public async Task<(List<(DateTime Ts, string Host, bool Blocked)> Rows, string? Oldest)> Log(string search, string? olderThan, int limit, CancellationToken ct, string? ip = null)
    {
        var q = $"/control/querylog?limit={limit}&search={Uri.EscapeDataString(search)}" + (olderThan is null ? "" : $"&older_than={Uri.EscapeDataString(olderThan)}");
        var n = await Send(HttpMethod.Get, q, null, ct);
        return (ParseLog(n, search, ip), n?["oldest"]?.GetValue<string>() is { Length: > 0 } o ? o : null);
    }

    /// <summary>Rækkerne fra forespørgselsloggen, der hører til klient-id'et <paramref name="search"/> eller IP-adressen <paramref name="ip"/>.</summary>
    public static List<(DateTime Ts, string Host, bool Blocked)> ParseLog(JsonNode? n, string search, string? ip)
    {
        var rows = new List<(DateTime, string, bool)>();
        foreach (var e in n?["data"]?.AsArray() ?? [])
        {
            if (e is null) continue;
            var byId = string.Equals(e["client_id"]?.GetValue<string>(), search, StringComparison.OrdinalIgnoreCase);
            var byIp = ip is { Length: > 0 } && e["client"]?.GetValue<string>() == ip;
            if (!byId && !byIp) continue;
            if (DateTime.TryParse(e["time"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts))
                rows.Add((ts, e["question"]?["name"]?.GetValue<string>() ?? "", IsBlocked(e["reason"]?.GetValue<string>())));
        }
        return rows;
    }

    /// <summary>Falsk kun, når AdGuard selv siger, at forespørgselsloggen er slået fra (ældre versioner uden dette kald regnes som slået til).</summary>
    public async Task<bool> QueryLogEnabled(CancellationToken ct)
    {
        try { return (await Send(HttpMethod.Get, "/control/querylog/config", null, ct))?["enabled"]?.GetValue<bool>() != false; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>Spærret af AdGuard (ikke omskrivninger som Begrænset tilstand/SafeSearch) – tæller ikke som brug af en tjeneste.</summary>
    public static bool IsBlocked(string? reason) => reason is "FilteredBlackList" or "FilteredBlockedService" or "FilteredParental" or "FilteredSafeBrowsing" or "FilteredInvalid";

    /// <summary>Hvad AdGuard ville gøre med et navn for en bestemt klient (til selvtjek).</summary>
    public Task<JsonNode?> CheckHost(string host, string client, CancellationToken ct) =>
        Send(HttpMethod.Get, $"/control/filtering/check_host?name={Uri.EscapeDataString(host)}&client={Uri.EscapeDataString(client)}", null, ct);

    public static List<string> ReplaceSection(List<string> current, IEnumerable<string> ours)
    {
        var outp = new List<string>(); var inside = false;
        foreach (var r in current)
        {
            if (r == Begin) { inside = true; continue; }
            if (r == End) { inside = false; continue; }
            if (!inside) outp.Add(r);
        }
        while (outp.Count > 0 && outp[^1].Length == 0) outp.RemoveAt(outp.Count - 1);
        var list = ours.ToList();
        if (list.Count > 0) { outp.Add(Begin); outp.AddRange(list); outp.Add(End); }
        return outp;
    }
}

/// <summary>Opsætningen af AdGuard-forbindelsen (adgangskoden gemmes krypteret).</summary>
public sealed class FamilySettings(SettingsService s, IDataProtectionProvider dp)
{
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.AdGuard.Password");
    public async Task<AdGuardClient?> Client()
    {
        var url = await s.Get("adguard_url"); var user = await s.Get("adguard_user"); var enc = await s.Get("adguard_pass");
        if (url.Length == 0 || user.Length == 0 || enc.Length == 0) return null;
        try { return new AdGuardClient(url, user, protector.Unprotect(enc)); }
        catch (CryptographicException) { return null; }
    }
    public Task SetPassword(string pw) => s.Set("adguard_pass", pw.Length == 0 ? "" : protector.Protect(pw));
    public async Task<string> DohHost() => (await s.Get("doh_host")).Trim().Replace("https://", "").TrimEnd('/');
    public async Task<string[]> SchoolGroups()
    {
        var v = await s.Get("school_groups");
        return v.Length == 0 ? IpadPolicy.DefaultSchoolGroups : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
/// Hjernen: hvert minut (og straks ved ændringer) regnes hver iPads tilstand ud og skrives til AdGuard, og forespørgselsloggen
/// læses for at måle aktivitet (bl.a. YouTube-minutter til dagsgrænsen).
/// </summary>
public sealed class IpadEngine(IServiceScopeFactory scopes, ILogger<IpadEngine> log) : BackgroundService
{
    private readonly Channel<bool> kick = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<int, DateTime> seen = [], seenReserve = [];
    private ServiceCatalog? catalog; private DateTime catalogAt;
    public string? LastError { get; private set; }
    public DateTime? LastRun { get; private set; }
    /// <summary>Alle overvågede enheder er stille på samme tid: det ligner en fejl på DNS-adressen, så der sendes ingen advarsler pr. enhed.</summary>
    public bool WatchHeld { get; private set; }
    private readonly WatchMonitor monitor = new();
    private DateTime lastRead = DateTime.MinValue;
    public static readonly TimeZoneInfo Tz = TimeZoneInfo.Local;
    private const long Five = TimeSpan.TicksPerMinute * 5;

    public void Kick() => kick.Writer.TryWrite(true);

    public async Task<ServiceCatalog?> Catalog(AdGuardClient ag, CancellationToken ct)
    {
        if (catalog is null || DateTime.UtcNow - catalogAt > TimeSpan.FromHours(12))
        {
            catalog = ServiceCatalog.Parse(await ag.Services(ct)); catalogAt = DateTime.UtcNow;
        }
        return catalog;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stop);
        // En uventet fejl her (fx SqliteException) må ikke slippe ud af ExecuteAsync – det stopper hele serveren
        try { await Import(); }
        catch (Exception e) when (!stop.IsCancellationRequested) { log.LogWarning(e, "iPad-styring: import fejlede"); }
        var lastActivity = DateTime.MinValue; var lastPrune = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var fs = scope.ServiceProvider.GetRequiredService<FamilySettings>();
                var live = scope.ServiceProvider.GetRequiredService<Live>();
                var ag = await fs.Client();
                var rd = await scope.ServiceProvider.GetRequiredService<ReserveDnsSettings>().Client(db, stop);
                if (rd is null && ReserveDnsStatus.Current is { } gone) { ReserveDnsStatus.Current = null; await Reevaluate(gone.MachineId); }
                if (ag is not null || rd is not null)
                {
                    var activity = DateTime.UtcNow - lastActivity > TimeSpan.FromSeconds(55);
                    if (activity) lastActivity = DateTime.UtcNow;
                    var readBefore = lastRead;
                    var (desired, rules) = await Desired(db, fs, ag, rd?.Client, stop);
                    if (ag is not null)
                    {
                        // hoved-AdGuard: fejl her stopper ikke reserven
                        try
                        {
                            if (activity) await Activity(db, ag, seen, stop);
                            if (await Apply(db, ag, desired, rules, stop)) await live.Changed("ipads");
                            LastError = null;
                        }
                        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException && !stop.IsCancellationRequested)
                        {
                            LastError = e.Message; log.LogWarning("iPad-styring: {Msg}", e.Message);
                        }
                    }
                    if (rd is { } r) await SyncReserve(db, ag, r.Client, r.MachineId, desired, rules, activity, stop);   // panelet henter selv status
                    if (activity) await Watch(db, scope.ServiceProvider, lastRead > readBefore, stop);
                }
                else monitor.Note(false, DateTime.UtcNow);
                if (DateTime.UtcNow - lastPrune > TimeSpan.FromHours(6))
                {
                    var cut = DateTime.UtcNow.AddDays(-35);
                    await db.IpadSlots.Where(s => s.Slot < cut).ExecuteDeleteAsync(stop);
                    lastPrune = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException or DbUpdateException)
            {
                LastError = e.Message; log.LogWarning("iPad-styring: {Msg}", e.Message);
                monitor.Note(false, DateTime.UtcNow);
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                LastError = e.Message; log.LogError(e, "iPad-styring: uventet fejl");
                monitor.Note(false, DateTime.UtcNow);
            }
            LastRun = DateTime.UtcNow;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            try { await kick.Reader.ReadAsync(wait.Token); await Task.Delay(500, stop); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        }
    }

    /// <summary>Advarer, når en enhed ikke længere bruger styringen – men kun når loggen faktisk har kunnet læses i et stykke tid.</summary>
    private async Task Watch(AppDb db, IServiceProvider sp, bool readOk, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        monitor.Note(readOk, now);
        if (!monitor.Ready(now)) return;
        var hours = IpadWatch.Hours(await sp.GetRequiredService<SettingsService>().GetNum(IpadWatch.HoursSetting, IpadWatch.DefaultHours));
        var (changed, held) = await IpadWatch.Run(db, sp.GetRequiredService<Notifier>().Send, hours, now, ct);
        WatchHeld = held;
        if (changed) await sp.GetRequiredService<Live>().Changed("ipads");
    }

    /// <summary>Engangs-import af AdGuard-login fra data/adguard.import (url, bruger, adgangskode på hver sin linje) – filen slettes bagefter.</summary>
    private async Task Import()
    {
        using var scope = scopes.CreateScope();
        var f = Path.Combine(scope.ServiceProvider.GetRequiredService<Paths>().Data, "adguard.import");
        if (!File.Exists(f)) return;
        try
        {
            var l = (await File.ReadAllLinesAsync(f)).Select(x => x.Trim()).ToArray();
            if (l.Length >= 3 && l[0].Length > 0 && l[1].Length > 0 && l[2].Length > 0)
            {
                var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
                await s.Set("adguard_url", l[0].TrimEnd('/')); await s.Set("adguard_user", l[1]);
                await scope.ServiceProvider.GetRequiredService<FamilySettings>().SetPassword(l[2]);
                if (l.Length >= 4 && l[3].Length > 0) await s.Set("doh_host", l[3]);
                log.LogInformation("AdGuard-forbindelsen er importeret");
            }
        }
        finally { File.Delete(f); }
    }

    public static async Task<Dictionary<int, YtUsage>> YtUsedToday(AppDb db, CancellationToken ct = default)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz).Date;
        var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Tz);
        var rows = await db.IpadSlots.AsNoTracking().Where(s => (s.Service == "youtube" || s.Service == IpadPolicy.AppYoutube || s.Service == IpadPolicy.AppKids) && s.Slot >= from)
            .GroupBy(s => new { s.IpadId, s.Service }).Select(g => new { g.Key.IpadId, g.Key.Service, N = g.Count() }).ToListAsync(ct);
        return rows.GroupBy(r => r.IpadId).ToDictionary(g => g.Key, g => new YtUsage(
            g.Where(x => x.Service == "youtube").Sum(x => x.N) * 5, g.Where(x => x.Service == IpadPolicy.AppYoutube).Sum(x => x.N) * 5, g.Where(x => x.Service == IpadPolicy.AppKids).Sum(x => x.N) * 5));
    }

    /// <summary>
    /// Hver iPads ønskede tilstand lige nu (og alle regler til Fleetdecks afsnit). Tjeneste-kataloget hentes fra hoved-AdGuard,
    /// eller fra reserven, hvis hoved-AdGuard ikke svarer.
    /// </summary>
    private async Task<(List<(Ipad P, IpadDesired D)> Desired, List<string> Rules)> Desired(AppDb db, FamilySettings fs, AdGuardClient? ag, AdGuardClient? reserve, CancellationToken ct)
    {
        ServiceCatalog? cat = catalog; Exception? failed = null;
        foreach (var src in new[] { ag, reserve })
        {
            if (src is null) continue;
            try { cat = await Catalog(src, ct); failed = null; break; }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { failed = e; }
        }
        // uden katalog kendes tjenesternes grupper ikke – så hellere ingenting end forkerte regler
        if (cat is null && failed is not null) throw failed;
        var groups = cat?.Groups ?? [];
        var school = await fs.SchoolGroups();
        var ipads = await db.Ipads.ToListAsync(ct);
        var used = await YtUsedToday(db, ct);
        var now = DateTime.UtcNow;
        var list = ipads.OrderBy(p => p.Id).Select(p => (p, IpadPolicy.Build(p, IpadPolicy.State(p, now, Tz, used.GetValueOrDefault(p.Id)), groups, school))).ToList();
        return (list, list.SelectMany(x => x.Item2.Rules).ToList());
    }

    /// <summary>Skriver ændringer til AdGuard. Returnerer true, hvis noget blev ændret.</summary>
    private static async Task<bool> Apply(AppDb db, AdGuardClient ag, List<(Ipad P, IpadDesired D)> desired, List<string> allRules, CancellationToken ct)
    {
        var ipads = desired.Select(x => x.P).ToList();
        var existing = ((await ag.Clients(ct))?["clients"]?.AsArray() ?? []).Select(c => c?["name"]?.GetValue<string>() ?? "").ToHashSet();
        var changed = false;
        foreach (var (p, d) in desired)
        {
            var hash = d.Hash(p.ClientId);
            if (hash == p.Applied && existing.Contains(d.Name)) continue;
            try
            {
                // uden LAN-adresse er ClientID det eneste, AdGuard kan genkende enheden på
                if (AdGuardClient.LooksLikeMac(p.ClientId) && string.IsNullOrEmpty(p.LanIp))
                    throw new InvalidOperationException("Enhedens ID ligner en netværksadresse, så AdGuard kan ikke genkende den, og reglerne virker ikke. Fjern profilen, og installér en ny.");
                await ag.Upsert(d, p.ClientId, existing.Contains(d.Name), ct);
                p.Applied = hash; p.ApplyError = null; changed = true;
            }
            catch (InvalidOperationException e) { p.ApplyError = e.Message; p.Applied = null; }
        }
        // klienter, Fleetdeck har lavet, men hvor iPad'en er slettet
        foreach (var n in existing.Where(n => n.StartsWith("fd-ipad-", StringComparison.Ordinal) && !ipads.Any(p => IpadPolicy.ClientName(p.Id) == n)))
        { await ag.Delete(n, ct); changed = true; }

        await db.SaveChangesAsync(ct);
        var cur = await ag.UserRules(ct);
        var next = AdGuardClient.ReplaceSection(cur, allRules);
        if (!cur.SequenceEqual(next)) { await ag.SetUserRules(next, ct); changed = true; }
        return changed;
    }

    /// <summary>
    /// Reserve-DNS: læg den samme ønskede tilstand på reservens AdGuard (iPad-klienter og regel-afsnittet – også når hoved-AdGuard er nede)
    /// og kopiér resten af indstillingerne fra hoved-AdGuard. Reservens forespørgselslog læses også, for Cloudflare fordeler iPad'ernes
    /// forespørgsler mellem de to connectorer.
    /// </summary>
    private async Task SyncReserve(AppDb db, AdGuardClient? ag, AdGuardClient rep, int machineId, List<(Ipad P, IpadDesired D)> desired, List<string> rules, bool activity, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var prev = ReserveDnsStatus.Current is { } c && c.MachineId == machineId ? c : null;
        var wasAlarm = ReserveDnsStatus.Alarm(prev, now) is not null;
        ReserveDnsHealth next;
        try
        {
            var version = (await rep.Status(ct))?["version"]?.GetValue<string>();
            AgSnapshot? primary = null;
            if (ag is not null)
            {
                try { primary = await AgSnapshot.Read(ag, ct); }
                catch (Exception e) when (e is InvalidOperationException or HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
                { log.LogInformation("Reserve-DNS: hoved-AdGuard kan ikke læses ({Msg}) – kun iPad-reglerne lægges på reserven", e.Message); }
            }
            var replica = await AgSnapshot.Read(rep, ct);
            var ops = ReserveDnsSync.Plan(primary, replica, [.. desired.Select(x => AdGuardClient.ClientBody(x.D, x.P.ClientId))], rules);
            var (done, error) = await ReserveDnsSync.Run(rep, ops, ct);
            if (done > 0) log.LogInformation("Reserve-DNS: {N} ændring(er) lagt på ({What})", done, string.Join(", ", ops.Take(5).Select(o => o.What)));
            if (activity) await Activity(db, rep, seenReserve, ct);
            next = new(machineId, true, now, now, null, null, now, error, done, done > 0 ? now : prev?.LastChange, primary is not null, version);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            next = new(machineId, false, now, prev?.LastOk, prev?.DownSince ?? now, e.Message, prev?.LastSync, prev?.SyncError, prev?.LastChanges ?? 0, prev?.LastChange, ag is not null, prev?.Version);
            if (prev?.Up != false) log.LogWarning("Reserve-DNS svarer ikke: {Msg}", e.Message);
        }
        ReserveDnsStatus.Current = next;
        // alarm (eller løst) med det samme – ikke først, når maskinen melder sig næste gang
        if (wasAlarm != (ReserveDnsStatus.Alarm(next, now) is not null)) await Reevaluate(machineId);
    }

    private async Task Reevaluate(int machineId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<HealthService>().Evaluate(machineId);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or DbUpdateException) { log.LogWarning("Reserve-DNS-alarm: {Msg}", e.Message); }
    }

    /// <summary>Læser nye forespørgsler pr. iPad og gemmer, hvilke tjenester der var i brug i hver 5-minutters-periode.</summary>
    private async Task Activity(AppDb db, AdGuardClient ag, Dictionary<int, DateTime> seen, CancellationToken ct)
    {
        var cat = await Catalog(ag, ct);
        var ipads = await db.Ipads.ToListAsync(ct);
        foreach (var p in ipads)
        {
            var since = seen.TryGetValue(p.Id, out var s) ? s : DateTime.UtcNow.AddMinutes(-15);
            var rows = new List<(DateTime Ts, string Host, bool Blocked)>(); string? older = null;
            DateTime? viaClient = null, viaAny = null;
            for (var page = 0; page < 5; page++)
            {
                var (r, oldest) = await ag.Log(p.ClientId, older, 300, ct);
                // nyeste side læses uanset "since", så en genstart af Fleetdeck ikke ligner, at enheden har været væk
                if (page == 0 && r.Count > 0) viaClient = r.Max(x => x.Ts);
                if (p.LanIp is { Length: > 0 } lan)
                {
                    var (r2, _) = await ag.Log(lan, older, 300, ct, lan);
                    r.AddRange(r2);
                }
                if (page == 0 && r.Count > 0) viaAny = r.Max(x => x.Ts);
                rows.AddRange(r.Where(x => x.Ts > since));
                if (r.Count == 0 || r.Min(x => x.Ts) <= since || oldest is null) break;
                older = oldest;
            }
            IpadWatch.Observe(p, viaClient, viaAny);
            if (rows.Count == 0) continue;
            seen[p.Id] = rows.Max(r => r.Ts);
            // spærrede opslag (fx den almindelige YouTube-app i "Kun YouTube Kids") tæller kun som aktivitet, ikke som brug af tjenesten
            var slots = rows.Select(r => (Svc: r.Blocked ? "*" : cat?.Classify(r.Host) ?? "*", Slot: new DateTime(r.Ts.Ticks / Five * Five, DateTimeKind.Utc)))
                .SelectMany(x => x.Svc == "*" ? [x] : new[] { x, ("*", x.Slot) })
                .Concat(rows.Where(r => !r.Blocked && IpadPolicy.YtAppOf(r.Host) is not null).Select(r => (Svc: IpadPolicy.YtAppOf(r.Host)!, Slot: new DateTime(r.Ts.Ticks / Five * Five, DateTimeKind.Utc))))
                .Distinct().ToList();
            var minSlot = slots.Min(x => x.Slot);
            var have = (await db.IpadSlots.AsNoTracking().Where(x => x.IpadId == p.Id && x.Slot >= minSlot).Select(x => new { x.Service, x.Slot }).ToListAsync(ct))
                .Select(x => (x.Service, x.Slot)).ToHashSet();
            foreach (var (svc, slot) in slots.Where(x => !have.Contains(x)))
                db.IpadSlots.Add(new IpadSlot { IpadId = p.Id, Service = svc, Slot = slot });
        }
        await db.SaveChangesAsync(ct);
        if (await ag.QueryLogEnabled(ct)) lastRead = DateTime.UtcNow;
    }
}

/// <summary>DNS-profilen (.mobileconfig), der får iPad'en til altid at bruge AdGuard via krypteret DNS med sit eget klient-id.</summary>
public static class IpadProfile
{
    public static string Build(Ipad p, string dohHost)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        var url = $"https://{dohHost}/dns-query/{p.ClientId}";
        var inner = Guid.NewGuid().ToString().ToUpperInvariant(); var outer = Guid.NewGuid().ToString().ToUpperInvariant();
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>PayloadContent</key>
              <array>
                <dict>
                  <key>DNSSettings</key>
                  <dict>
                    <key>DNSProtocol</key><string>HTTPS</string>
                    <key>ServerURL</key><string>{X(url)}</string>
                  </dict>
                  <key>PayloadDisplayName</key><string>Fleetdeck – sikker DNS</string>
                  <key>PayloadIdentifier</key><string>dk.fleetdeck.ipad.{p.ClientId}.dns</string>
                  <key>PayloadType</key><string>com.apple.dnsSettings.managed</string>
                  <key>PayloadUUID</key><string>{inner}</string>
                  <key>PayloadVersion</key><integer>1</integer>
                </dict>
              </array>
              <key>PayloadDescription</key><string>Sender iPad'ens DNS krypteret til hjemmets AdGuard, så Fleetdeck kan styre internet, sengetid og YouTube – også uden for hjemmet.</string>
              <key>PayloadDisplayName</key><string>Fleetdeck – {X(p.Name)}</string>
              <key>PayloadIdentifier</key><string>dk.fleetdeck.ipad.{p.ClientId}</string>
              <key>PayloadOrganization</key><string>Fleetdeck</string>
              <key>PayloadType</key><string>Configuration</string>
              <key>PayloadUUID</key><string>{outer}</string>
              <key>PayloadVersion</key><integer>1</integer>
            </dict>
            </plist>
            """;
    }
}
