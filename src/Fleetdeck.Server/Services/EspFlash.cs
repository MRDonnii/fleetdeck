using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Fleetdeck.Server.Services;

/// <summary>
/// ESP-flash: et firmwarekatalog, brugeren selv angiver (samme format og regler som ESP Flasher-projektets docker/fwsync.py).
/// Fleetdeck har ingen firmware eller standardkatalog med. Hver firmware har id, manifest "&lt;id&gt;/manifest.json" og sha256 = de første 12 hextegn af SHA-256 over
/// alle dele i manifestets første build, lagt efter hinanden. Kataloget uden "generated" er dets indhold.
/// </summary>
public static partial class EspCatalog
{
    /// <summary>Intet standardkatalog: uden en adresse viser siden, hvordan man sætter et op.</summary>
    public const string DefaultUrl = "";
    /// <summary>Den tidligere standard (findes ikke længere). Står den gemt, regnes den som "intet katalog" – den gemte værdi røres ikke.</summary>
    public const string RetiredDefaultUrl = "https://raw.githubusercontent.com/MRDonnii/esp-flasher/main/esp-flasher/firmware/";
    public const string UrlSetting = "esp_catalog_url", TokenSetting = "esp_catalog_token";
    public const string GitHubRaw = "raw.githubusercontent.com";
    public const int MaxCatalog = 1 << 20, MaxPart = 16 << 20;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")] private static partial Regex SafeIdRx();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")] private static partial Regex SafeFileRx();
    [GeneratedRegex(@"^[0-9a-f]{12}$")] private static partial Regex ShaRx();

    public static bool SafeId(string? s) => s is not null && SafeIdRx().IsMatch(s) && !s.Contains("..");
    public static bool SafeFile(string? s) => s is not null && SafeFileRx().IsMatch(s) && !s.Contains("..");

    /// <summary>Kataloget, hvis det har en liste "firmwares" – ellers null.</summary>
    public static JsonObject? Parse(string json)
    {
        try { return JsonNode.Parse(json) is JsonObject o && o["firmwares"] is JsonArray ? o : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Fejlen i en post (null = i orden).</summary>
    public static string? Validate(JsonNode? fw)
    {
        var id = Str(fw, "id");
        if (!SafeId(id) || Str(fw, "manifest") != $"{id}/manifest.json" || !ShaRx().IsMatch(Str(fw, "sha256")))
            return $"Ugyldig post i kataloget: {(id.Length > 0 ? id : "?")}";
        return null;
    }

    /// <summary>Delenes filnavne i manifestets første build (fejl ved et usikkert navn).</summary>
    public static List<string> Parts(string manifestJson, string id)
    {
        var parts = (JsonNode.Parse(manifestJson)?["builds"] as JsonArray)?.FirstOrDefault()?["parts"] as JsonArray
                    ?? throw new InvalidDataException($"{id}: manifestet har ingen dele");
        var names = new List<string>();
        foreach (var p in parts)
        {
            var name = Str(p, "path");
            if (!SafeFile(name)) throw new InvalidDataException($"{id}: ugyldigt filnavn \"{name}\"");
            names.Add(name);
        }
        return names.Count > 0 ? names : throw new InvalidDataException($"{id}: manifestet har ingen dele");
    }

    /// <summary>De første 12 hextegn af SHA-256 over delene i rækkefølge.</summary>
    public static string PartsHash(IEnumerable<byte[]> parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in parts) sha.AppendData(p);
        return Convert.ToHexStringLower(sha.GetHashAndReset())[..12];
    }

    /// <summary>Indholdet uden tidsstemplet (felter sorteret): ens betyder, at der intet er at hente.</summary>
    public static string Content(JsonObject? cat)
    {
        if (cat is null) return "";
        return Canon(new JsonObject(cat.Where(kv => kv.Key != "generated").Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))));
    }

    private static string Canon(JsonNode? n) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => JsonSerializer.Serialize(kv.Key) + ":" + Canon(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        null => "null",
        _ => n.ToJsonString(),
    };

    public static string Generated(JsonObject? cat) => Str(cat, "generated");

    /// <summary>Skal kataloget fra nettet bruges? Kun når indholdet er ændret, og det ikke er ældre end det, vi har.</summary>
    public static bool ShouldTake(JsonObject remote, JsonObject? current) =>
        Content(remote) != Content(current) && string.CompareOrdinal(Generated(remote), Generated(current)) >= 0;

    /// <summary>
    /// Katalogets mappe som http(s)-adresse med / til sidst; tom = slået fra; null = ugyldig. Tager mappen, dens catalog.json eller en
    /// GitHub-adresse (https://github.com/EJER/REPO[/tree/GREN[/mappe]]), som bliver til raw.githubusercontent.com-formen (som fwsync.normalize_url).
    /// </summary>
    public static string? NormalizeUrl(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http") || u.Host.Length == 0
            || u.Query.Length > 0 || u.Fragment.Length > 0 || u.UserInfo.Length > 0) return null;
        var path = u.AbsolutePath;
        var origin = u.GetLeftPart(UriPartial.Authority);
        if (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var bits = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (bits.Count == 2) bits.Add("main");
            else if (bits.Count >= 4 && bits[2] is "tree" or "blob") bits.RemoveAt(2);
            else return null;
            if (bits[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)) bits[1] = bits[1][..^4];
            if (bits[0].Length == 0 || bits[1].Length == 0) return null;
            origin = "https://" + GitHubRaw;
            path = "/" + string.Join('/', bits);
        }
        if (path.EndsWith("/catalog.json", StringComparison.Ordinal)) path = path[..^"catalog.json".Length];
        return origin + path.TrimEnd('/') + "/";
    }

    /// <summary>Adressen, der faktisk bruges: den gemte, men den tidligere standard (som ikke findes mere) = intet katalog.</summary>
    public static string Effective(string? stored) => NormalizeUrl(stored) is { } u && u != RetiredDefaultUrl ? u : "";

    private static string Str(JsonNode? n, string key) =>
        n is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>Status for firmwaren, som siden viser.</summary>
public sealed record EspFirmwareStatus(string Source, string Generated, bool Live, DateTime? Checked, DateTime? Changed, string Error, int Count);

/// <summary>
/// Firmwaren, Fleetdeck serverer: den medfølgende kopi (reserve uden net) eller det nyeste spejl i data/esp-firmware/&lt;generation&gt;.
/// Et nyt spejl tages kun i brug, når hver firmware passer med katalogets sha256; uændrede firmwarer kopieres fra det, vi har.
/// </summary>
public sealed class EspFirmwareMirror(string bundled, string live, Func<string, int, CancellationToken, Task<byte[]>> get)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string bundledDir = bundled;
    private volatile string current = bundled;
    private string url = "";
    private string source = "app", error = "";
    private DateTime? checkedAt, changedAt;

    public string Current => current;

    public EspFirmwareStatus Status
    {
        get
        {
            var cat = Load(current);
            return new(source, EspCatalog.Generated(cat), url.Length > 0, checkedAt, changedAt, error, (cat?["firmwares"] as JsonArray)?.Count ?? 0);
        }
    }

    /// <summary>Vælger det nyeste katalog, vi har (medfølgende eller et tidligere spejl), og husker adressen til tjek.</summary>
    public void Start(string catalogUrl)
    {
        url = catalogUrl;
        string best = bundledDir;
        var bestCat = Load(bundledDir);
        if (Directory.Exists(live))
            foreach (var dir in Directory.GetDirectories(live).Order(StringComparer.Ordinal))
                if (Load(dir) is { } cat && (bestCat is null || string.CompareOrdinal(EspCatalog.Generated(cat), EspCatalog.Generated(bestCat)) > 0))
                    (best, bestCat) = (dir, cat);
        current = best;
        source = best == bundledDir ? "app" : "github";
    }

    public void SetUrl(string catalogUrl) => url = catalogUrl;

    /// <summary>Ét tjek mod nettet. True, når et nyt katalog er taget i brug.</summary>
    public async Task<bool> Check(CancellationToken ct)
    {
        if (url.Length == 0) return false;
        await gate.WaitAsync(ct);
        string? fresh = null;
        try
        {
            checkedAt = DateTime.UtcNow;
            var remote = EspCatalog.Parse(Encoding.UTF8.GetString(await get(url + "catalog.json", EspCatalog.MaxCatalog, ct)))
                         ?? throw new InvalidDataException("Kataloget på nettet er ugyldigt");
            var curDir = current;
            var cur = Load(curDir);
            if (!EspCatalog.ShouldTake(remote, cur)) { error = ""; return false; }
            Directory.CreateDirectory(live);
            fresh = Path.Combine(live, "g" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(fresh);
            var have = new Dictionary<string, JsonObject>();
            foreach (var f in (cur?["firmwares"] as JsonArray ?? []).OfType<JsonObject>())
                if (f["id"] is JsonValue v && v.TryGetValue<string>(out var fid)) have.TryAdd(fid, f);
            foreach (var fw in (JsonArray)remote["firmwares"]!)
            {
                if (EspCatalog.Validate(fw) is { } bad) throw new InvalidDataException(bad);
                var id = (string)fw!["id"]!;
                var sha = (string)fw["sha256"]!;
                var dest = Path.Combine(fresh, id);
                if (have.TryGetValue(id, out var old) && old["sha256"] is JsonValue ov && ov.TryGetValue<string>(out var osha) && osha == sha && File.Exists(Path.Combine(curDir, id, "manifest.json")))
                {
                    CopyDir(Path.Combine(curDir, id), dest);   // uændret: genbrug det, vi har
                    continue;
                }
                Directory.CreateDirectory(dest);
                var raw = await get($"{url}{id}/manifest.json", EspCatalog.MaxCatalog, ct);
                var parts = new List<byte[]>();
                foreach (var name in EspCatalog.Parts(Encoding.UTF8.GetString(raw), id))
                {
                    var data = await get($"{url}{id}/{name}", EspCatalog.MaxPart, ct);
                    parts.Add(data);
                    await File.WriteAllBytesAsync(Path.Combine(dest, name), data, ct);
                }
                if (EspCatalog.PartsHash(parts) != sha) throw new InvalidDataException($"{id}: kontrolsummen passer ikke – opdateringen springes over");
                await File.WriteAllBytesAsync(Path.Combine(dest, "manifest.json"), raw, ct);
            }
            await File.WriteAllTextAsync(Path.Combine(fresh, "catalog.json"), remote.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            current = fresh;
            source = "github";
            changedAt = DateTime.UtcNow;
            error = "";
            // det forrige spejl beholdes (en browser kan være i gang med at hente fra det); ældre slettes
            foreach (var dir in Directory.GetDirectories(live))
                if (dir != fresh && dir != curDir) TryDelete(dir);
            fresh = null;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e.Message.Length > 200 ? e.Message[..200] : e.Message;   // fejl: vi beholder den firmware, vi har
            return false;
        }
        finally
        {
            if (fresh is not null) TryDelete(fresh);
            gate.Release();
        }
    }

    /// <summary>Stien til en fil i den firmware, der serveres nu: "catalog.json" eller "&lt;id&gt;/&lt;fil&gt;". Null, hvis den ikke findes.</summary>
    public string? Resolve(string? rel)
    {
        var parts = (rel ?? "").Split('/');
        var ok = parts switch
        {
            ["catalog.json"] => true,
            [var id, var file] => EspCatalog.SafeId(id) && EspCatalog.SafeFile(file),
            _ => false,
        };
        if (!ok) return null;
        var path = Path.Combine([current, .. parts]);
        return File.Exists(path) ? path : null;
    }

    private static JsonObject? Load(string dir)
    {
        try { return EspCatalog.Parse(File.ReadAllText(Path.Combine(dir, "catalog.json"))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Henter katalogets filer. Uden nøgle hentes adressen, som den er. Med en nøgle (privat repo):
/// <list type="bullet">
/// <item>raw.githubusercontent.com læses gennem GitHub-API'et: repoets træ (git/trees, én gang pr. tjek – når catalog.json hentes) og hver
/// fil som blob (git/blobs med Accept: application/vnd.github.raw). Ikke contents-API'et: det omkoder binærfiler, GitHub tror er tekst
/// (fx en ota_data_initial.bin med lutter 0xFF), så kontrolsummen ikke passer.</item>
/// <item>andre værter får nøglen som "Authorization: Bearer".</item>
/// </list>
/// Omdirigeringer følges selv (højst <see cref="MaxRedirects"/>), og nøglen sendes kun til samme skema, vært og port som den første
/// adresse – aldrig videre til en anden vært. Nøglen står aldrig i en adresse, en fejltekst eller loggen.
/// </summary>
public sealed class EspFetch(Func<HttpClient> client)
{
    public const int MaxRedirects = 5, MaxTree = 16 << 20;
    public const string GitHubApi = "https://api.github.com";
    private readonly Lock gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> trees = [];

    /// <summary>Er nøglen til at sende i en header? (samme tegn som ESP Flasher tillader: GitHub-nøgler og lignende)</summary>
    public static bool ValidToken(string? t) => t is { Length: >= 10 and <= 255 } && t.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    public async Task<byte[]> Get(string url, int limit, string? token, CancellationToken ct)
    {
        var u = new Uri(url);
        token = ValidToken(token) ? token : null;
        if (token is not null && u.Host.Equals(EspCatalog.GitHubRaw, StringComparison.OrdinalIgnoreCase)) return await GitHub(u, limit, token, ct);
        return await Download(u, limit, token, null, Name(u), ct);
    }

    private async Task<byte[]> GitHub(Uri raw, int limit, string token, CancellationToken ct)
    {
        var seg = raw.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (seg.Length < 4) throw new InvalidDataException("GitHub-adressen skal være https://github.com/EJER/REPO/tree/GREN/mappe");
        var (owner, repo, gitRef) = (seg[0], seg[1], seg[2]);
        var path = string.Join('/', seg[3..]);
        var api = $"{GitHubApi}/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
        var key = $"{owner}/{repo}/{gitRef}";
        Dictionary<string, string>? tree;
        lock (gate) trees.TryGetValue(key, out tree);
        if (tree is null || seg[^1] == "catalog.json")   // catalog.json hentes først i hvert tjek: så hentes træet igen
        {
            var json = await Download(new Uri($"{api}/git/trees/{Uri.EscapeDataString(gitRef)}?recursive=1"), MaxTree, token, "application/vnd.github+json", path, ct);
            tree = ParseTree(json);
            lock (gate) trees[key] = tree;
        }
        if (!tree.TryGetValue(path, out var sha)) throw new InvalidDataException($"{path}: 404 {Why(404)}");
        return await Download(new Uri($"{api}/git/blobs/{Uri.EscapeDataString(sha)}"), limit, token, "application/vnd.github.raw", path, ct);
    }

    /// <summary>Sti i repoet → blob-sha (kun filer).</summary>
    public static Dictionary<string, string> ParseTree(byte[] json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonNode.Parse(json)?["tree"] is JsonArray items)
            foreach (var t in items.OfType<JsonObject>())
                if (t["type"]?.GetValue<string>() == "blob" && t["path"]?.GetValue<string>() is { } p && t["sha"]?.GetValue<string>() is { } sha)
                    map[p] = sha;
        return map;
    }

    private async Task<byte[]> Download(Uri start, int limit, string? token, string? accept, string name, CancellationToken ct)
    {
        var http = client();
        var url = start;
        for (var hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (accept is not null) req.Headers.Accept.ParseAdd(accept);
            if (url.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)) req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            else req.Headers.CacheControl = new() { NoCache = true };
            if (token is not null && SameOrigin(url, start)) req.Headers.Authorization = new("Bearer", token);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)res.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is { } loc)
            {
                if (hop >= MaxRedirects) throw new InvalidDataException($"{name}: for mange omdirigeringer");
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                if (url.Scheme is not ("https" or "http")) throw new InvalidDataException($"{name}: omdirigeret til en adresse, der ikke er http(s)");
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new InvalidDataException($"{name}: {code} {Why(code) ?? res.ReasonPhrase}");
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > limit) throw new InvalidDataException($"{name} er for stor");
            }
            return ms.ToArray();
        }
    }

    /// <summary>Samme skema, vært og port: kun dér må nøglen sendes med.</summary>
    public static bool SameOrigin(Uri a, Uri b) => Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private static string? Why(int code) => code switch
    {
        401 => "nøglen blev afvist",
        403 => "ingen adgang (nøglen eller GitHubs grænse for kald)",
        404 => "findes ikke – eller repoet er privat, og nøglen mangler",
        _ => null,
    };

    private static string Name(Uri u) => Uri.UnescapeDataString(u.AbsolutePath[(u.AbsolutePath.LastIndexOf('/') + 1)..]);
}

/// <summary>Nøglen til et privat katalog (fx et fine-grained GitHub-token med Contents: Read-only). Gemmes krypteret og vises aldrig igen.</summary>
public sealed class EspCatalogSecret(SettingsService s, IDataProtectionProvider dp)
{
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.Esp.CatalogToken");

    public async Task<bool> HasToken() => (await s.Get(EspCatalog.TokenSetting)).Length > 0;

    /// <summary>Gem en ny nøgle. Fejltekst, hvis den ikke ligner en nøgle (null = gemt).</summary>
    public async Task<string?> Save(string token)
    {
        token = token.Trim();
        if (!EspFetch.ValidToken(token)) return "Nøglen ser forkert ud – kopiér hele tokenet fra GitHub (kun bogstaver, tal, _ . og -)";
        await s.Set(EspCatalog.TokenSetting, protector.Protect(token));
        return null;
    }

    public Task Clear() => s.Set(EspCatalog.TokenSetting, "");

    /// <summary>Nøglen i klartekst til hentningen ("" = ingen eller ulæselig, fx efter skiftede nøgler).</summary>
    public async Task<string> Token()
    {
        var enc = await s.Get(EspCatalog.TokenSetting);
        if (enc.Length == 0) return "";
        try { return protector.Unprotect(enc); } catch (CryptographicException) { return ""; }
    }

    /// <summary>Det, siden får at se: adressen (kun administratorer) og om der er en nøgle – aldrig selve nøglen.</summary>
    public async Task<(string? Url, bool HasToken)> Public(bool admin) =>
        (admin ? EspCatalog.Effective(await s.Get(EspCatalog.UrlSetting)) : null, await HasToken());
}

/// <summary>Holder firmwaren ajour: første tjek 15 s efter start, så hver 30. min (5 min efter en fejl).</summary>
public sealed class EspFirmwareService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30), Retry = TimeSpan.FromMinutes(5);
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<EspFirmwareService> log;
    private readonly IDataProtectionProvider dp;
    private volatile string token = "";
    public EspFirmwareMirror Mirror { get; }

    public EspFirmwareService(Paths paths, IHttpClientFactory http, IServiceScopeFactory scopes, IDataProtectionProvider dp, ILogger<EspFirmwareService> log)
    {
        this.scopes = scopes;
        this.log = log;
        this.dp = dp;
        var fetch = new EspFetch(() => http.CreateClient("esp"));
        Mirror = new(Path.Combine(AppContext.BaseDirectory, "EspFirmware"), Path.Combine(paths.Data, "esp-firmware"),
            (url, limit, ct) => fetch.Get(url, limit, token, ct));
    }

    /// <summary>Adressen og nøglen fra indstillingerne.</summary>
    public async Task<(string Url, string Token)> Source()
    {
        using var scope = scopes.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<SettingsService>();
        return (EspCatalog.Effective(await s.Get(EspCatalog.UrlSetting)), await new EspCatalogSecret(s, dp).Token());
    }

    public async Task<string> Url() => (await Source()).Url;

    /// <summary>Læser adressen og nøglen igen (efter et gem og før hvert tjek).</summary>
    public async Task Refresh()
    {
        var (url, t) = await Source();
        token = t;
        Mirror.SetUrl(url);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { var (url, t) = await Source(); token = t; Mirror.Start(url); }
        catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("ESP-firmware: indstillingen kunne ikke læses ({Type})", e.GetType().Name); Mirror.Start(""); }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            while (!ct.IsCancellationRequested)
            {
                await Refresh();
                if (await Mirror.Check(ct)) log.LogInformation("Ny ESP-firmware fra nettet ({Gen})", Mirror.Status.Generated);
                else if (Mirror.Status.Error is { Length: > 0 } err) log.LogInformation("ESP-firmware-tjek: {Err}", err);
                await Task.Delay(Mirror.Status.Error.Length > 0 ? Retry : Interval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>Wi-Fi til nye ESP-enheder (sendes over USB med Improv). Navnet i indstillingerne, koden krypteret.</summary>
public sealed class EspWifi(SettingsService s, IDataProtectionProvider dp)
{
    public const string SsidKey = "esp_wifi_ssid", PassKey = "esp_wifi_pass";
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.Esp.WifiPassword");

    /// <summary>Improv: navn 1–32 byte, kode højst 64 byte (WPA: 8–63 tegn, eller tom for et åbent net).</summary>
    public static string? Validate(string? ssid, string? password)
    {
        var sl = Encoding.UTF8.GetByteCount(ssid ?? "");
        if (string.IsNullOrWhiteSpace(ssid) || sl > 32) return "Wi-Fi-navnet skal være 1–32 tegn";
        if (password is { Length: > 0 } p && (p.Length < 8 || Encoding.UTF8.GetByteCount(p) > 63)) return "Wi-Fi-koden skal være 8–63 tegn (eller tom for et åbent net)";
        if ((ssid + password).Any(char.IsControl)) return "Wi-Fi-navn og -kode må ikke indeholde styretegn";
        return null;
    }

    public Task<string> Ssid() => s.Get(SsidKey);
    public async Task<bool> HasPassword() => (await s.Get(PassKey)).Length > 0;

    public async Task Save(string ssid, string? password)
    {
        await s.Set(SsidKey, ssid.Trim());
        if (password is not null) await s.Set(PassKey, password.Length == 0 ? "" : protector.Protect(password));
    }

    public async Task Clear() { await s.Set(SsidKey, ""); await s.Set(PassKey, ""); }

    public async Task<string?> Password()
    {
        var enc = await s.Get(PassKey);
        if (enc.Length == 0) return "";
        try { return protector.Unprotect(enc); } catch (CryptographicException) { return null; }
    }
}
