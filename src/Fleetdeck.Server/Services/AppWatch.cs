using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Status for en app: kører den nyeste udgivelse på kanalen?</summary>
public static class AppStatus
{
    public const string New = "new", Ok = "ok", Ahead = "ahead", Unknown = "unknown", NoRelease = "norelease";
}

/// <summary>Sammenligningen: seneste udgivelse på kanalen, og de udgivelser, der er nyere end den kørende version.</summary>
public sealed record AppCompare(string Status, ReleaseInfo? Latest, List<ReleaseInfo> Newer);

/// <summary>GitHubs grænse for forespørgsler (fra svarets X-RateLimit-*-felter).</summary>
public sealed record AppRate(int? Limit, int? Remaining, DateTime? Reset, DateTime? BlockedUntil);

/// <summary>Ren logik for siden Apps: kontrol af felter, læsning af versionsnumre, sammenligning, beskeder og scripts.</summary>
public static partial class AppWatch
{
    public const string Source = "appwatch";
    public const string TokenKey = "appwatch_token", HoursKey = "appwatch_hours", AutoHourKey = "appwatch_auto_hour", RateKey = "appwatch_rate";
    public const string Begin = "FLEETDECK_APPVERSION_BEGIN", End = "FLEETDECK_APPVERSION_END";
    public static readonly string[] ReadKinds = ["http", "docker", "file", "command", "none"];
    public static readonly string[] PlaceKinds = ["container", "service", "program"];
    /// <summary>Udgivelser, der gemmes pr. app (og noternes længde).</summary>
    public const int KeepReleases = 15, NotesMax = 8000;

    // et versionsnummer, der står for sig selv (ikke midt i et ord eller et længere tal)
    [GeneratedRegex(@"(?<![0-9A-Za-z.])[vV]?\d{1,6}\.\d{1,6}(?:\.\d{1,6}){0,2}(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z.-]+)?(?![0-9A-Za-z])")]
    private static partial Regex VersionTokenRx();
    [GeneratedRegex(@"(?i)\bversion\b[^0-9A-Za-z\n]{0,4}(?=[vV]?\d)")] private static partial Regex VersionWordRx();
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}(?:\.[A-Za-z0-9_-]{1,64}){0,7}\z")] private static partial Regex FieldRx();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z")] private static partial Regex TagRx();

    // ---------------------------------------------------------------- kontrol af det, brugeren skriver

    public static string? Validate(WatchedApp a)
    {
        if (string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 100 || a.Name.Any(char.IsControl)) return "Skriv et navn (højst 100 tegn)";
        if (UpdateScript.ValidateRepo(a.Repo) is not null) return "GitHub-projektet skrives som ejer/navn, fx ejer/minapp";
        if (a.Channel is not (ReleaseFeed.Stable or ReleaseFeed.Beta)) return "Ukendt kanal";
        if (!PlaceKinds.Contains(a.PlaceKind)) return "Ukendt type (container, tjeneste eller program)";
        if (a.PlaceName.Length > 100 || a.PlaceName.Any(char.IsControl)) return "Navnet på containeren eller tjenesten er for langt";
        if (!ReadKinds.Contains(a.ReadKind)) return "Ukendt måde at læse versionen på";
        if (a.ReadField.Length > 0 && !FieldRx().IsMatch(a.ReadField)) return "JSON-feltet skrives som fx version eller data.app.version";
        if (a.ReadTarget.Length > 1000) return "Adressen, stien eller kommandoen er for lang";
        if (a.UpdateCommand.Length > 4000) return "Opdatér-kommandoen er for lang (højst 4000 tegn)";
        switch (a.ReadKind)
        {
            case "http":
                if (!Uri.TryCreate(a.ReadTarget.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.UserInfo.Length > 0)
                    return "Adressen skal starte med http:// eller https://, fx http://192.0.2.10:8080/api/version";
                break;
            case "docker":
                var c = DockerName(a);
                if (c.Length == 0) return "Skriv containerens navn";
                if (PublicAccess.ValidateContainer(c) is { } ce) return ce;
                if (a.MachineId is null) return "Vælg maskinen, containeren kører på";
                break;
            case "file":
                if (a.ReadTarget.Trim() is not { Length: > 0 } f || f.Contains('\'') || f.Contains('"') || f.Any(char.IsControl)) return "Skriv den fulde sti til filen (uden anførselstegn)";
                if (a.MachineId is null) return "Vælg maskinen, filen ligger på";
                break;
            case "command":
                if (a.ReadTarget.Trim().Length == 0) return "Skriv kommandoen, der udskriver versionen";
                if (a.MachineId is null) return "Vælg maskinen, kommandoen skal køre på";
                break;
        }
        if (a.UpdateCommand.Trim().Length > 0 && a.MachineId is null) return "Vælg maskinen, Opdatér-kommandoen skal køre på";
        if (a.AutoUpdate && a.NeverAuto) return "En app, der aldrig må opdateres automatisk, kan ikke have automatisk opdatering";
        if (a.AutoUpdate && a.UpdateCommand.Trim().Length == 0) return "Automatisk opdatering kræver en Opdatér-kommando";
        return null;
    }

    /// <summary>Retter det, brugeren har skrevet (mellemrum, "aldrig automatisk" slår automatisk fra).</summary>
    public static void Clean(WatchedApp a)
    {
        a.Name = a.Name.Trim(); a.Repo = a.Repo.Trim(); a.PlaceName = a.PlaceName.Trim();
        a.ReadTarget = a.ReadKind == "command" ? a.ReadTarget.Replace("\r\n", "\n").Trim() : a.ReadTarget.Trim();
        a.ReadField = a.ReadField.Trim(); a.UpdateCommand = a.UpdateCommand.Replace("\r\n", "\n").Trim();
        if (a.NeverAuto) a.AutoUpdate = false;
    }

    public static string DockerName(WatchedApp a) => (a.ReadTarget.Trim() is { Length: > 0 } t ? t : a.PlaceName).Trim();

    public static bool NeedsAgent(WatchedApp a) => a.ReadKind is "docker" or "file" or "command";

    // ---------------------------------------------------------------- versionen i et svar

    /// <summary>
    /// Versionen i et svar eller en fil: med et JSON-felt (fx data.version) læses feltet; ellers et "version"-felt i JSON,
    /// eller det første versionsnummer i teksten (1.2.3, v1.2.3, 2.0.0-beta.4).
    /// </summary>
    public static (string? Version, string? Error) Extract(string? text, string? field)
    {
        text = (text ?? "").Trim().TrimStart('﻿');
        if (text.Length == 0) return (null, "Svaret er tomt");
        if (field is { Length: > 0 })
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); } catch (JsonException) { return (null, $"Svaret er ikke JSON, så feltet {field} kan ikke læses"); }
            using (doc)
            {
                var e = doc.RootElement;
                foreach (var part in field.Split('.'))
                {
                    if (e.ValueKind == JsonValueKind.Object && TryProp(e, part, out var v)) e = v;
                    else if (e.ValueKind == JsonValueKind.Array && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < e.GetArrayLength()) e = e[i];
                    else return (null, $"Feltet {field} findes ikke i svaret");
                }
                var s = e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetRawText(), _ => null };
                return Token(s) is { } ver ? (ver, null) : (null, $"Feltet {field} er ikke et versionsnummer ({Short(s ?? e.GetRawText())})");
            }
        }
        if (text[0] is '{' or '[')
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && TryProp(doc.RootElement, "version", out var v) && Token(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) is { } jv)
                    return (jv, null);
            }
            catch (JsonException) { }
        }
        return Token(text) is { } tv ? (tv, null) : (null, $"Fandt intet versionsnummer i svaret ({Short(text)})");
    }

    private static bool TryProp(JsonElement e, string name, out JsonElement v)
    {
        if (e.TryGetProperty(name, out v)) return true;
        foreach (var p in e.EnumerateObject()) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { v = p.Value; return true; }
        return false;
    }

    /// <summary>Første versionsnummer i en tekst, skrevet som SemVer (v og byggedata fjernes).</summary>
    public static string? Token(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // "version 1.8.0" går forud for andre tal i teksten (fx en adresse eller et byggenummer)
        foreach (Match w in VersionWordRx().Matches(s))
            if (VersionTokenRx().Match(s, w.Index + w.Length) is { Success: true } t && t.Index == w.Index + w.Length && SemVer.Parse(t.Value) is { } wv) return wv.ToString();
        foreach (Match m in VersionTokenRx().Matches(s))
            if (SemVer.Parse(m.Value) is { } v) return v.ToString();
        return null;
    }

    private static string Short(string s) { s = Regex.Replace(s, @"\s+", " ").Trim(); return s.Length > 60 ? s[..60] + "…" : s; }

    /// <summary>Docker: versionen fra billedets mærke (org.opencontainers.image.version) eller tagget (ghcr.io/ejer/minapp:1.2.3).</summary>
    public static string? FromImage(string? image, string? label = null)
    {
        if (SemVer.Parse(label?.Trim()) is { } lv) return lv.ToString();
        if (string.IsNullOrWhiteSpace(image)) return null;
        var img = image.Trim();
        var at = img.IndexOf('@'); if (at >= 0) img = img[..at];
        var slash = img.LastIndexOf('/'); var colon = img.IndexOf(':', slash + 1);
        return colon < 0 ? null : SemVer.Parse(img[(colon + 1)..]) is { } v ? v.ToString() : null;
    }

    /// <summary>Det, agentens læse-job udskrev mellem markørerne (alt, hvis markørerne mangler).</summary>
    public static string Between(string output)
    {
        var b = output.IndexOf(Begin, StringComparison.Ordinal);
        if (b < 0) return output;
        var rest = output[(b + Begin.Length)..];
        var e = rest.IndexOf(End, StringComparison.Ordinal);
        return (e >= 0 ? rest[..e] : rest).Trim();
    }

    /// <summary>Versionen fra læse-jobbets output.</summary>
    public static (string? Version, string? Error) FromJob(WatchedApp a, string output)
    {
        var text = Between(output);
        if (a.ReadKind != "docker") return Extract(text, a.ReadField);
        var parts = text.Split('|', 2);
        var label = parts.Length == 2 ? parts[0].Trim() : null;
        var image = (parts.Length == 2 ? parts[1] : parts[0]).Trim();
        return FromImage(image, label) is { } v ? (v, null) : (null, $"Billedet {Short(image)} har intet versionsnummer i tagget eller mærket org.opencontainers.image.version");
    }

    /// <summary>Scriptet, agenten kører for at læse versionen (PowerShell på Windows, sh ellers). Null = læses ikke via agenten.</summary>
    public static string? ReadScript(WatchedApp a, string platform)
    {
        var win = platform == "windows";
        var body = a.ReadKind switch
        {
            "file" => win ? $"Get-Content -Raw -LiteralPath '{a.ReadTarget.Trim()}'" : $"cat -- '{a.ReadTarget.Trim()}'",
            "docker" => $"docker inspect --format '{{{{index .Config.Labels \"org.opencontainers.image.version\"}}}}|{{{{.Config.Image}}}}' {DockerName(a)}",
            "command" => a.ReadTarget.Trim(),
            _ => null,
        };
        return body is null ? null : $"echo {Begin}\n{body}\necho {End}\n";
    }

    // ---------------------------------------------------------------- udgivelser og sammenligning

    /// <summary>Gem kun de nyeste udgivelser og en kort udgave af noterne.</summary>
    public static List<ReleaseInfo> Slim(IEnumerable<ReleaseInfo> list) => list.OrderByDescending(r => r.Sem).Take(KeepReleases)
        .Select(r => r with { Notes = r.Notes.Length > NotesMax ? r.Notes[..NotesMax] + "\n…" : r.Notes, Image = null, ImageAssetUrl = null, LocalDir = null }).ToList();

    public static AppCompare Compare(string? running, IReadOnlyCollection<ReleaseInfo> releases, string channel)
    {
        var latest = ReleaseFeed.Latest(releases, channel);
        if (latest is null) return new(AppStatus.NoRelease, null, []);
        if (SemVer.Parse(running) is not { } cur) return new(AppStatus.Unknown, latest, []);
        var c = latest.Sem.CompareTo(cur);
        var newer = releases.Where(r => (channel == ReleaseFeed.Beta || !r.Prerelease) && r.Sem > cur).OrderByDescending(r => r.Sem).Take(5).ToList();
        return new(c > 0 ? AppStatus.New : c == 0 ? AppStatus.Ok : AppStatus.Ahead, latest, newer);
    }

    /// <summary>
    /// Skal der sendes besked? Én gang pr. version: kun når appen kører en ældre version end den nyeste på kanalen. Uden en
    /// kørende version (læses ikke) gælder den første udgivelse, Fleetdeck ser, som udgangspunkt (ingen besked), og nyere end den giver besked.
    /// Returnerer den version, der skal huskes som "sendt", og om der skal sendes.
    /// </summary>
    public static (bool Send, string? Remember) ShouldNotify(WatchedApp a, AppCompare cmp)
    {
        if (cmp.Latest is not { } latest) return (false, a.NotifiedVersion);
        var noted = SemVer.Parse(a.NotifiedVersion);
        if (cmp.Status == AppStatus.New)
            return noted is { } n && latest.Sem <= n ? (false, a.NotifiedVersion) : (a.Notify, latest.Version);
        if (cmp.Status == AppStatus.Unknown && a.ReadKind == "none")
            return noted is null ? (false, latest.Version) : latest.Sem > noted.Value ? (a.Notify, latest.Version) : (false, a.NotifiedVersion);
        return (false, a.NotifiedVersion);
    }

    public static string NotifyText(WatchedApp a, ReleaseInfo latest, string? running, string? machine)
    {
        var where = machine is null ? "" : $" på {machine}";
        var head = running is null ? $"📦 {a.Name}: version {latest.Version} er udgivet" : $"📦 {a.Name}: ny version {latest.Version} (kører {running}{where})";
        var notes = string.Join('\n', latest.Notes.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6));
        if (notes.Length > 400) notes = notes[..400] + "…";
        return $"{head}\n{(notes.Length > 0 ? notes : latest.Name)}{(a.NeverAuto ? "\nAldrig automatisk – opdatér selv i Fleetdeck under Apps." : "")}";
    }

    /// <summary>Opdatér-kommandoen med {version} og {tag} udfyldt (kun sikre tegn i begge).</summary>
    public static string? ExpandCommand(string command, ReleaseInfo r)
    {
        if (SemVer.Parse(r.Version) is null || !TagRx().IsMatch(r.Tag)) return null;
        return command.Replace("{version}", r.Version).Replace("{tag}", r.Tag);
    }

    // ---------------------------------------------------------------- GitHub-grænser

    /// <summary>
    /// GitHubs grænse ud fra svaret. Afvist (403/429) med 0 tilbage: vent til nulstillingen. Retry-After: vent så længe.
    /// Under 5 tilbage: vent også (andre dele af Fleetdeck bruger den samme grænse).
    /// </summary>
    public static AppRate Rate(HttpStatusCode status, IDictionary<string, string> headers, DateTime now)
    {
        int? I(string k) => headers.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        var limit = I("x-ratelimit-limit"); var remaining = I("x-ratelimit-remaining");
        DateTime? reset = I("x-ratelimit-reset") is { } r ? DateTimeOffset.FromUnixTimeSeconds(r).UtcDateTime : null;
        DateTime? blocked = null;
        var refused = status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;
        if (I("retry-after") is { } ra && refused) blocked = now.AddSeconds(Math.Clamp(ra, 1, 3600));
        else if (remaining is <= 0 && refused) blocked = reset ?? now.AddHours(1);
        else if (remaining is < 5) blocked = reset ?? now.AddMinutes(15);
        else if (status == HttpStatusCode.TooManyRequests) blocked = now.AddMinutes(15);
        return new(limit, remaining, reset, blocked);
    }

    /// <summary>Hvor ofte en app tjekkes (timer), og om den er klar til at blive tjekket.</summary>
    public static bool Due(DateTime? last, double hours, DateTime now) => last is null || now - last.Value >= TimeSpan.FromHours(Math.Clamp(hours, 0.25, 168));

    /// <summary>Hvor længe der går, før versionen læses igen: N timer, en halv time efter en fejl.</summary>
    public static double ReadHours(WatchedApp a, double hours) => a.ReadError is null ? hours : Math.Min(hours, 0.5);
}

/// <summary>Siden Apps: spørger GitHub om udgivelser, læser den kørende version og kører brugerens Opdatér-kommando.</summary>
public sealed class AppWatchService(AppDb db, SettingsService settings, AuditService audit, JobService jobs, IHttpClientFactory http,
    Notifier notifier, IDataProtectionProvider dp, ILogger<AppWatchService> log)
{
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.AppWatch.GitHubToken");

    public sealed record CheckResult(bool Asked, bool NotModified, string? Error, bool Notified);

    // ---------------------------------------------------------------- GitHub-nøgle (krypteret)

    public async Task<bool> HasToken() => (await settings.Get(AppWatch.TokenKey)).Length > 0;

    public async Task SetToken(string? token, string by)
    {
        if (token is null) return;
        token = token.Trim();
        await settings.Set(AppWatch.TokenKey, token.Length == 0 ? "" : protector.Protect(token));
        await audit.Log(by, "apps.token", token.Length == 0 ? "GitHub-nøgle fjernet" : "ny GitHub-nøgle");
    }

    private async Task<string?> Token()
    {
        var enc = await settings.Get(AppWatch.TokenKey);
        if (enc.Length == 0) return null;
        try { return protector.Unprotect(enc); } catch (CryptographicException) { return null; }
    }

    public async Task<AppRate?> Rate() => await settings.Get(AppWatch.RateKey) is { Length: > 0 } s ? JsonSerializer.Deserialize<AppRate>(s, Json.Web) : null;

    // ---------------------------------------------------------------- udgivelser

    public static List<ReleaseInfo> Releases(WatchedApp a)
    {
        try { return JsonSerializer.Deserialize<List<ReleaseInfo>>(a.ReleasesJson, Json.Web) ?? []; } catch (JsonException) { return []; }
    }

    /// <summary>Spørg GitHub om appens udgivelser (med ETag: et uændret svar tæller ikke med i grænsen).</summary>
    public async Task<CheckResult> Check(WatchedApp a, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (await Rate() is { BlockedUntil: { } until } && until > now)
        {
            a.CheckError = $"GitHub-grænsen er nået – prøver igen kl. {until.ToLocalTime():HH.mm}";
            await db.SaveChangesAsync(ct);
            return new(false, false, a.CheckError, false);
        }
        // samme projekt som en anden app, der lige er tjekket (fx en app og dens desktop-udgave): brug svaret igen
        if (await db.WatchedApps.AsNoTracking().Where(o => o.Id != a.Id && o.Repo == a.Repo && o.CheckError == null && o.Checked > now.AddMinutes(-10))
                .OrderByDescending(o => o.Checked).FirstOrDefaultAsync(ct) is { } twin)
        {
            a.ReleasesJson = twin.ReleasesJson; a.ETag = twin.ETag; a.Checked = twin.Checked; a.CheckError = null;
            var n = await NotifyIfNew(a);
            await db.SaveChangesAsync(ct);
            return new(false, false, null, n);
        }
        string? error = null; var notModified = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{a.Repo}/releases?per_page=30");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (await Token() is { } tok) req.Headers.Authorization = new("Bearer", tok);
            if (a.ETag is { Length: > 0 } et && a.ReleasesJson.Length > 2) req.Headers.TryAddWithoutValidation("If-None-Match", et);
            using var res = await http.CreateClient("appwatch").SendAsync(req, ct);
            var headers = res.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.FirstOrDefault() ?? "");
            await settings.Set(AppWatch.RateKey, Json.Ser(AppWatch.Rate(res.StatusCode, headers, now)));
            if (res.StatusCode == HttpStatusCode.NotModified) notModified = true;
            else if (!res.IsSuccessStatusCode)
                error = res.StatusCode switch
                {
                    HttpStatusCode.NotFound => $"GitHub-projektet {a.Repo} findes ikke (eller er privat – angiv en GitHub-nøgle)",
                    HttpStatusCode.Unauthorized => "GitHub afviste nøglen – ret den under Apps → Indstillinger",
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub afviser flere forespørgsler lige nu – prøver igen senere",
                    _ => $"GitHub svarede {(int)res.StatusCode}",
                };
            else
            {
                a.ReleasesJson = Json.Ser(AppWatch.Slim(ReleaseFeed.ParseGitHub(await res.Content.ReadAsStringAsync(ct))));
                a.ETag = res.Headers.ETag?.ToString() is { Length: > 0 and <= 200 } e ? e : null;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            error = e is TaskCanceledException ? "GitHub svarede ikke i tide" : e is JsonException ? "Svaret fra GitHub kunne ikke læses" : e.Message;
        }
        a.Checked = now; a.CheckError = error;
        var notified = error is null && await NotifyIfNew(a);
        await db.SaveChangesAsync(ct);
        return new(true, notModified, error, notified);
    }

    /// <summary>Besked om en ny version (én gang pr. version).</summary>
    private async Task<bool> NotifyIfNew(WatchedApp a)
    {
        var cmp = AppWatch.Compare(a.Running, Releases(a), a.Channel);
        var (send, remember) = AppWatch.ShouldNotify(a, cmp);
        a.NotifiedVersion = remember;
        if (!send || cmp.Latest is null) return false;
        var machine = a.MachineId is { } mid ? await db.Machines.AsNoTracking().Where(m => m.Id == mid).Select(m => m.DisplayName ?? m.Name).FirstOrDefaultAsync() : null;
        notifier.Send(AppWatch.NotifyText(a, cmp.Latest, a.Running, machine), "update");
        return true;
    }

    // ---------------------------------------------------------------- den kørende version

    /// <summary>Læs den kørende version: HTTP direkte fra serveren, Docker fra agentens rapport, ellers et lille job på maskinen.</summary>
    public async Task Read(WatchedApp a, CancellationToken ct = default)
    {
        if (a.ReadKind == "none") return;
        if (a.ReadKind == "http") { await ReadHttp(a, ct); await NotifyIfNew(a); await db.SaveChangesAsync(ct); return; }
        if (a.ReadJobId is { } jid && await db.Jobs.AsNoTracking().AnyAsync(j => j.Id == jid && JobStatus.Active.Contains(j.Status), ct)) return;
        var m = a.MachineId is { } mid ? await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mid, ct) : null;
        if (m is null) { Fail(a, "Maskinen findes ikke længere"); await db.SaveChangesAsync(ct); return; }
        if (a.ReadKind == "docker" && Json.Des<HealthReport>(m.HealthJson).Unraid?.Containers.FirstOrDefault(c => c.Name == AppWatch.DockerName(a)) is { } dc
            && AppWatch.FromImage(dc.Image) is { } dv)
        {
            Got(a, dv); await NotifyIfNew(a); await db.SaveChangesAsync(ct); return;
        }
        if (!MachineViews.Online(m, await settings.GetNum("poll_seconds", 30))) { Fail(a, $"{m.Label} er offline"); await db.SaveChangesAsync(ct); return; }
        if (AppWatch.ReadScript(a, m.Platform) is not { } body) return;
        var job = await jobs.Create(m.Id, JobTypes.Script, $"Læs versionen af {a.Name}", new JobParams { Name = a.Name, Body = body, TimeoutMin = 5 },
            RunAs.System, null, AppWatch.Source, "system", save: false);
        await db.SaveChangesAsync(ct);
        a.ReadJobId = job.Id;
        await jobs.Commit(m.Id);
    }

    private async Task ReadHttp(WatchedApp a, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, a.ReadTarget.Trim());
            req.Headers.Accept.ParseAdd("application/json, text/plain;q=0.9, */*;q=0.5");
            using var res = await http.CreateClient("appwatch").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) { Fail(a, $"Adressen svarede {(int)res.StatusCode}"); return; }
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            var buf = new byte[256 * 1024]; var n = 0;
            while (n < buf.Length && await s.ReadAsync(buf.AsMemory(n), ct) is var r and > 0) n += r;
            var (v, err) = AppWatch.Extract(Encoding.UTF8.GetString(buf, 0, n), a.ReadField);
            if (v is null) Fail(a, err!); else Got(a, v);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            Fail(a, e is TaskCanceledException ? "Adressen svarede ikke i tide" : $"Adressen svarede ikke ({e.Message})");
        }
    }

    private static void Got(WatchedApp a, string v) { a.Running = v; a.RunningAt = DateTime.UtcNow; a.ReadError = null; }
    private static void Fail(WatchedApp a, string e) { a.RunningAt = DateTime.UtcNow; a.ReadError = e.Length > 300 ? e[..300] : e; }

    /// <summary>Når læse- og opdateringsjobs er færdige: gem versionen, skriv resultatet i loggen og læs igen efter en opdatering.</summary>
    public async Task Settle(WatchedApp a, CancellationToken ct = default)
    {
        if (a.ReadJobId is { } rid && await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == rid, ct) is var rj && (rj is null || !JobStatus.Active.Contains(rj.Status)))
        {
            a.ReadJobId = null;
            if (rj is null) Fail(a, "Læse-jobbet blev slettet");
            else if (rj.Status != JobStatus.Success) Fail(a, $"Læse-jobbet {(rj.Status == JobStatus.Expired ? "udløb" : "fejlede")}{(rj.ExitCode is { } x ? $" (kode {x})" : "")}");
            else { var (v, err) = AppWatch.FromJob(a, rj.Output); if (v is null) Fail(a, err!); else Got(a, v); }
            if (a.ReadError is null) await NotifyIfNew(a);
        }
        if (a.UpdateJobId is { } uid && await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == uid, ct) is var uj && (uj is null || !JobStatus.Active.Contains(uj.Status)))
        {
            a.UpdateJobId = null;
            var ok = uj?.Status == JobStatus.Success;
            await audit.Log(uj?.CreatedBy ?? "system", ok ? "apps.update_done" : "apps.update_failed",
                ok ? $"{a.Name}: Opdatér-kommandoen til {a.UpdateTo} er kørt" : $"{a.Name}: Opdatér-kommandoen til {a.UpdateTo} fejlede (job {uid})", a.MachineId);
            // læs versionen igen om et minut (appen skal lige nå at starte)
            a.RunningAt = DateTime.UtcNow.AddHours(-await settings.GetNum(AppWatch.HoursKey, 6)).AddMinutes(1);
        }
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- Opdatér

    public async Task<(int? JobId, string? Error)> Update(WatchedApp a, string? version, bool confirmed, string by, bool auto = false)
    {
        if (!confirmed && !auto) return (null, "Bekræft opdateringen først");
        if (auto && (a.NeverAuto || !a.AutoUpdate)) return (null, $"{a.Name} må ikke opdateres automatisk");
        if (a.UpdateCommand.Trim().Length == 0) return (null, $"{a.Name} har ingen Opdatér-kommando");
        var rels = Releases(a);
        var rel = version is { Length: > 0 } ? rels.FirstOrDefault(r => r.Version == version) : AppWatch.Compare(a.Running, rels, a.Channel).Latest;
        if (rel is null) return (null, version is { Length: > 0 } ? $"Version {version} findes ikke i listen – tjek igen" : "Der er ingen udgivelse at opdatere til");
        if (a.MachineId is not { } mid || await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid) is not { } m) return (null, "Vælg maskinen, Opdatér-kommandoen skal køre på");
        if (!MachineViews.Online(m, await settings.GetNum("poll_seconds", 30))) return (null, $"{m.Label} er offline");
        if (a.UpdateJobId is { } uid && await db.Jobs.AnyAsync(j => j.Id == uid && JobStatus.Active.Contains(j.Status))) return (null, $"En opdatering af {a.Name} er allerede i gang");
        if (AppWatch.ExpandCommand(a.UpdateCommand, rel) is not { } body) return (null, "Udgivelsens tag kan ikke bruges i kommandoen");
        var job = await jobs.Create(m.Id, JobTypes.Script, $"Opdatér {a.Name} til {rel.Version}", new JobParams { Name = a.Name, Body = body, TimeoutMin = 60 },
            RunAs.System, null, AppWatch.Source, auto ? "automatisk" : by, save: false);
        await db.SaveChangesAsync();
        a.UpdateJobId = job.Id; a.UpdateTo = rel.Version;
        if (auto) a.AutoLastVersion = rel.Version;
        await jobs.Commit(m.Id);
        await audit.Log(auto ? "automatisk" : by, auto ? "apps.auto_update" : "apps.update", $"{a.Name}: {a.Running ?? "ukendt"} → {rel.Version} på {m.Label} (job {job.Id})", m.Id);
        return (job.Id, null);
    }

    // ---------------------------------------------------------------- baggrund

    /// <summary>Hvert minut: færdige jobs, GitHub hver N. time (højst 5 apps ad gangen), versionen og automatisk opdatering om natten.</summary>
    public async Task Tick(CancellationToken ct = default)
    {
        var apps = await db.WatchedApps.OrderBy(a => a.Checked ?? DateTime.MinValue).ToListAsync(ct);
        if (apps.Count == 0) return;
        var hours = await settings.GetNum(AppWatch.HoursKey, 6);
        var autoHour = (int)await settings.GetNum(AppWatch.AutoHourKey, 3);
        var now = DateTime.UtcNow; var asked = 0;
        foreach (var a in apps)
        {
            try
            {
                await Settle(a, ct);
                if (asked < 5 && AppWatch.Due(a.Checked, hours, now)) { asked++; await Check(a, ct); }
                if (a.ReadJobId is null && a.UpdateJobId is null && AppWatch.Due(a.RunningAt, AppWatch.ReadHours(a, hours), now)) await Read(a, ct);
                if (a.AutoUpdate && !a.NeverAuto && DateTime.Now.Hour == autoHour && a.UpdateJobId is null
                    && AppWatch.Compare(a.Running, Releases(a), a.Channel) is { Status: AppStatus.New, Latest: { } rel }
                    && rel.Version != a.AutoLastVersion && (rel.Published is not { } pub || now - pub >= TimeSpan.FromHours(2)))
                {
                    var (_, err) = await Update(a, rel.Version, false, "automatisk", auto: true);
                    if (err is not null)
                    {
                        a.AutoLastVersion = rel.Version; await db.SaveChangesAsync(ct);
                        await audit.Log("automatisk", "apps.auto_skipped", $"{a.Name} {rel.Version}: {err}", a.MachineId);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Apps: {App} kunne ikke tjekkes", a.Name); }
        }
    }
}
