using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Bios;

/// <summary>Katalogets tilstand pr. leverandør, til Indstillinger → BIOS og maskinsiden.</summary>
public sealed record BiosVendorStatus(string Vendor, string Name, bool Supported, DateTime? FetchedAt, DateTime? LastAttempt, string? Error,
    DateTime? ErrorSince, int Models, int Machines, string Text);

/// <summary>
/// Leverandørernes BIOS-kataloger (først Lenovo). Henter kun for leverandører, der findes i flåden, sender ETag/If-Modified-Since, beholder
/// sidste gode svar på disken (data/bios/catalog) og beregner MachineBiosStatus for alle maskiner bagefter. Pakkerne selv hentes ikke her –
/// kun på et tryk (BiosPackages).
/// </summary>
public sealed class BiosCatalog
{
    private readonly Paths paths;
    private readonly IHttpClientFactory http;
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<BiosCatalog> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>Rematch læser og indsætter statusrækker; to samtidige kørsler (katalogtjek + inventar) må ikke begge indsætte samme maskine.</summary>
    private readonly SemaphoreSlim matchGate = new(1, 1);
    /// <summary>Stammer, hvis detaljer ikke kunne hentes: næste forsøg tidligst (6 timer, derefter 24 timer). Kun i hukommelsen.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Next, int Failures)> detailsBackoff = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Skal detaljerne til stammen hentes nu (ikke i pause efter et mislykket forsøg)?</summary>
    public bool DetailsDue(string stem, DateTime now) => !detailsBackoff.TryGetValue(stem, out var b) || b.Next <= now;

    private void DetailsFailed(string stem, DateTime now) =>
        detailsBackoff.AddOrUpdate(stem, _ => (now.AddHours(6), 1), (_, b) => (now.AddHours(b.Failures >= 1 ? 24 : 6), b.Failures + 1));
    private readonly SemaphoreSlim wake = new(0, 1);
    private DateTime lastWake = DateTime.MinValue;
    private Meta meta = new();
    private volatile LenovoCatalogView lenovo = new();

    public sealed class Meta
    {
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public DateTime? FetchedAt { get; set; }
        public DateTime? LastAttempt { get; set; }
        public string? Error { get; set; }
        public DateTime? ErrorSince { get; set; }
    }

    public BiosCatalog(Paths paths, IHttpClientFactory http, IServiceScopeFactory scopes, ILogger<BiosCatalog> log)
    {
        this.paths = paths; this.http = http; this.scopes = scopes; this.log = log;
        LoadFromDisk();
    }

    public string Dir => Path.Combine(paths.Data, "bios", "catalog", "lenovo");
    private string CatalogFile => Path.Combine(Dir, "catalogv2.xml");
    private string MetaFile => Path.Combine(Dir, "meta.json");
    private string PkgDir => Path.Combine(Dir, "pkg");

    public LenovoCatalogView Lenovo => lenovo;
    public Meta LenovoMeta => meta;

    /// <summary>Vågn baggrundstjenesten (fx "Tjek nu" eller en ny Lenovo-model). Højst hvert 10. minut, medmindre <paramref name="force"/>.</summary>
    public bool Wake(bool force = false)
    {
        lock (wake)
        {
            if (!force && DateTime.UtcNow - lastWake < TimeSpan.FromMinutes(10)) return false;
            lastWake = DateTime.UtcNow;
        }
        try { wake.Release(); } catch (SemaphoreFullException) { }
        return true;
    }

    internal async Task WaitForWake(TimeSpan delay, CancellationToken ct)
    {
        try { await wake.WaitAsync(delay, ct); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (File.Exists(MetaFile)) meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaFile)) ?? new();
            if (!File.Exists(CatalogFile)) { lenovo = new() { Error = meta.Error }; return; }
            using var f = File.OpenRead(CatalogFile);
            var models = LenovoCatalog.ParseCatalog(f);
            var pk = new Dictionary<string, LenovoPackageDetails>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(PkgDir))
                foreach (var x in Directory.GetFiles(PkgDir, "*.xml"))
                    if (LoadDetails(Path.GetFileNameWithoutExtension(x)) is { } d) pk[Path.GetFileNameWithoutExtension(x)] = d;
            lenovo = new() { Models = models, Packages = pk, FetchedAt = meta.FetchedAt ?? File.GetLastWriteTimeUtc(CatalogFile), Error = meta.Error };
        }
        catch (Exception e) when (e is IOException or JsonException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "BIOS: det gemte Lenovo-katalog kunne ikke læses");
            lenovo = new() { Error = "Det gemte katalog kunne ikke læses" };
        }
    }

    private LenovoPackageDetails? LoadDetails(string stem)
    {
        try
        {
            var xml = Path.Combine(PkgDir, stem + ".xml");
            if (!File.Exists(xml)) return null;
            var pkg = LenovoCatalog.ParsePackage(File.ReadAllText(xml));
            var txt = Path.Combine(PkgDir, stem + ".txt");
            var changes = File.Exists(txt) ? LenovoCatalog.ParseReadme(File.ReadAllText(txt)) : [];
            return new(pkg, changes);
        }
        catch (Exception e) when (e is IOException or FormatException or System.Xml.XmlException) { return null; }
    }

    private void SaveMeta()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta));
    }

    /// <summary>Hent katalogerne (kun for leverandører i flåden) og beregn status for alle maskiner. False = hentningen fejlede.</summary>
    public async Task<bool> Refresh(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var fleet = await Fleet(db, ct);
            var ok = true;
            var lenovoFacts = fleet.Where(m => BiosMatcher.VendorOf(m.Facts) == BiosMatcher.Lenovo).Select(m => m.Facts).ToList();
            if (lenovoFacts.Count > 0)
            {
                ok = await RefreshLenovo(ct);
                await FetchDetails(lenovoFacts, ct);
            }
            await Rematch(db, null, ct);
            return ok;
        }
        finally { gate.Release(); }
    }

    private sealed record FleetRow(int Id, string Platform, SystemFacts Facts);

    private static async Task<List<FleetRow>> Fleet(AppDb db, CancellationToken ct) =>
        (await db.Machines.AsNoTracking().Select(m => new { m.Id, m.Platform, m.FactsJson }).ToListAsync(ct))
            .Select(m => new FleetRow(m.Id, m.Platform, Json.Des<SystemFacts>(m.FactsJson))).ToList();

    private async Task<bool> RefreshLenovo(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        meta.LastAttempt = now;
        try
        {
            var c = http.CreateClient(BiosHttp.ClientName);
            var haveFile = File.Exists(CatalogFile);
            using var res = await BiosHttp.Send(c, LenovoCatalog.CatalogUrl, ct, configure: req =>
            {
                if (!haveFile) return;
                if (meta.ETag is { } et && System.Net.Http.Headers.EntityTagHeaderValue.TryParse(et, out var tag)) req.Headers.IfNoneMatch.Add(tag);
                if (meta.LastModified is { } lm && DateTimeOffset.TryParse(lm, out var d)) req.Headers.IfModifiedSince = d;
            });
            if (res.StatusCode != HttpStatusCode.NotModified || !haveFile)
            {
                if (!res.IsSuccessStatusCode) throw new BiosDownloadException($"HTTP {(int)res.StatusCode}");
                using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
                total.CancelAfter(TimeSpan.FromMinutes(10));   // klientens Timeout gælder ikke selve læsningen
                var bytes = await BiosHttp.Read(res, BiosHttp.MaxCatalogBytes, total.Token);
                var models = LenovoCatalog.ParseCatalog(new MemoryStream(bytes));
                if (models.Count(m => m.Bios is not null) < 10) throw new BiosDownloadException("Kataloget så forkert ud (for få modeller).");
                Directory.CreateDirectory(Dir);
                var tmp = CatalogFile + ".partial";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, CatalogFile, true);
                meta.ETag = res.Headers.ETag?.ToString();
                meta.LastModified = res.Content.Headers.LastModified?.ToString("R");
                lenovo = new() { Models = models, Packages = lenovo.Packages, FetchedAt = now };
            }
            else if (!lenovo.Loaded) LoadFromDisk();
            meta.FetchedAt = now; meta.Error = null; meta.ErrorSince = null;
            lenovo = new() { Models = lenovo.Models, Packages = lenovo.Packages, FetchedAt = now };
            SaveMeta();
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or BiosDownloadException or OperationCanceledException or System.Xml.XmlException or IOException
                                   && !ct.IsCancellationRequested)
        {
            log.LogInformation("BIOS: Lenovos katalog kunne ikke hentes: {Err}", e.Message);
            meta.Error = e is OperationCanceledException ? "Tidsgrænsen blev overskredet" : e.Message;
            meta.ErrorSince ??= now;
            lenovo = new() { Models = lenovo.Models, Packages = lenovo.Packages, FetchedAt = lenovo.FetchedAt, Error = meta.Error };
            try { SaveMeta(); } catch (IOException) { }
            return false;
        }
    }

    /// <summary>Pakke-XML (opgraderingsregler, alvor) og readme (ændringer) for de BIOS'er, flåden matcher. Filerne er uforanderlige
    /// (versionen står i navnet), så de hentes kun én gang. Fejl her er ikke fatale: status vises bare uden ændringslinjer.</summary>
    private async Task FetchDetails(List<SystemFacts> facts, CancellationToken ct)
    {
        var view = lenovo;
        if (!view.Loaded) return;
        var wanted = new Dictionary<string, LenovoModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in facts)
        {
            if (BiosMatcher.CurrentVersion(f) is not { } cv) continue;
            var m = BiosMatcher.MatchLenovo(f, LenovoBiosVersion.FromMachine(cv, f.BiosRelease), view.Models);
            if (m.Model?.Bios is { } b && !view.Packages.ContainsKey(b.Stem) && DetailsDue(b.Stem, DateTime.UtcNow)) wanted.TryAdd(b.Stem, m.Model);
        }
        if (wanted.Count == 0) return;
        var c = http.CreateClient(BiosHttp.ClientName);
        var added = new Dictionary<string, LenovoPackageDetails>(view.Packages, StringComparer.OrdinalIgnoreCase);
        foreach (var (stem, model) in wanted.Take(20))
        {
            try
            {
                if (LoadDetails(stem) is { } cached) { added[stem] = cached; continue; }
                (string Url, string? Sha256)? loc = null;
                foreach (var mtm in model.Types.Take(3))
                {
                    foreach (var os in new[] { "Win10", "Win11" })
                    {
                        string xml;
                        try { xml = Encoding.UTF8.GetString(await BiosHttp.Bytes(c, LenovoCatalog.ModelCatalogUrl(mtm, os), BiosHttp.MaxSmallBytes, ct)); }
                        catch (BiosDownloadException) { continue; }
                        loc = LenovoCatalog.FindPackage(xml, stem);
                        if (loc is not null) break;
                    }
                    if (loc is not null) break;
                }
                if (loc is not { } l) { log.LogInformation("BIOS: pakke-XML til {Stem} blev ikke fundet", stem); DetailsFailed(stem, DateTime.UtcNow); continue; }
                var pkgBytes = await BiosHttp.Bytes(c, l.Url, BiosHttp.MaxSmallBytes, ct);
                if (l.Sha256 is { } want && Convert.ToHexStringLower(SHA256.HashData(pkgBytes)) != want)
                    throw new BiosDownloadException("Pakke-XML'en svarede ikke til kontrolsummen.");
                var pkgXml = Encoding.UTF8.GetString(pkgBytes);
                var pkg = LenovoCatalog.ParsePackage(pkgXml);
                Directory.CreateDirectory(PkgDir);
                List<BiosChange> changes = [];
                if (pkg.Readme is { } rf)
                {
                    // readme'en er kun ændringslinjer: fejler den, gemmes pakke-XML'en alligevel (detaljerne findes så uden ændringslinjer)
                    try
                    {
                        var readme = await BiosHttp.Bytes(c, new Uri(new Uri(l.Url), rf.Name).ToString(), BiosHttp.MaxSmallBytes, ct);
                        if (rf.Sha256 is null || Convert.ToHexStringLower(SHA256.HashData(readme)) == rf.Sha256)
                        {
                            var text = Encoding.UTF8.GetString(readme);
                            changes = LenovoCatalog.ParseReadme(text);
                            await File.WriteAllTextAsync(Path.Combine(PkgDir, stem + ".txt"), text, ct);
                        }
                    }
                    catch (Exception e) when (e is HttpRequestException or BiosDownloadException or UriFormatException && !ct.IsCancellationRequested)
                    {
                        log.LogInformation("BIOS: readme til {Stem} kunne ikke hentes: {Err}", stem, e.Message);
                    }
                }
                await File.WriteAllTextAsync(Path.Combine(PkgDir, stem + ".xml"), pkgXml, ct);
                added[stem] = new(pkg, changes);
            }
            catch (Exception e) when (e is HttpRequestException or BiosDownloadException or FormatException or System.Xml.XmlException or IOException
                                       or OperationCanceledException && !ct.IsCancellationRequested)
            {
                log.LogInformation("BIOS: detaljer til {Stem} kunne ikke hentes: {Err}", stem, e.Message);
                DetailsFailed(stem, DateTime.UtcNow);
            }
        }
        var cur = lenovo;
        lenovo = new() { Models = cur.Models, Packages = added, FetchedAt = cur.FetchedAt, Error = cur.Error };
    }

    /// <summary>Beregn status for maskinerne (alle, når <paramref name="machineIds"/> er null) ud fra det katalog, der er i hukommelsen.</summary>
    public async Task<int> Rematch(AppDb db, IReadOnlyCollection<int>? machineIds, CancellationToken ct = default)
    {
        await matchGate.WaitAsync(ct);
        try { return await RematchLocked(db, machineIds, ct); }
        finally { matchGate.Release(); }
    }

    private async Task<int> RematchLocked(AppDb db, IReadOnlyCollection<int>? machineIds, CancellationToken ct)
    {
        var q = db.Machines.AsNoTracking();
        if (machineIds is not null) q = q.Where(m => machineIds.Contains(m.Id));
        var rows = await q.Select(m => new { m.Id, m.Platform, m.FactsJson }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        // fwupd's eget svar for "System Firmware" (den eksisterende firmware-pipeline: UpgradeItem "fwupd:…")
        var fw = (await db.Upgrades.AsNoTracking().Where(u => ids.Contains(u.MachineId) && u.WingetId.StartsWith(FwupdPrefix) && u.Name.StartsWith("System Firmware"))
                .Select(u => new { u.MachineId, u.Available }).ToListAsync(ct))
            .GroupBy(x => x.MachineId).ToDictionary(g => g.Key, g => g.First().Available);
        var existing = await db.BiosStatus.Where(s => ids.Contains(s.MachineId)).ToDictionaryAsync(s => s.MachineId, ct);
        var now = DateTime.UtcNow;
        var view = lenovo;
        var needDetails = false;
        foreach (var r in rows)
        {
            var facts = Json.Des<SystemFacts>(r.FactsJson);
            var s = BiosMatcher.Evaluate(r.Id, r.Platform, facts, view, fw.GetValueOrDefault(r.Id) ?? (fw.ContainsKey(r.Id) ? "?" : null), now);
            // ny model: hent pakke-XML og readme (men ikke igen og igen, når et forsøg lige er fejlet)
            if (s.Vendor == BiosMatcher.Lenovo && LenovoFor(facts) is { Details: null } lf && DetailsDue(lf.Bios.Stem, now)) needDetails = true;
            if (existing.TryGetValue(r.Id, out var e)) Copy(s, e);
            else db.BiosStatus.Add(s);
        }
        await db.SaveChangesAsync(ct);
        if (needDetails && machineIds is not null) Wake();
        return rows.Count;
    }

    public const string FwupdPrefix = "fwupd:";

    private static void Copy(MachineBiosStatus from, MachineBiosStatus to)
    {
        to.Vendor = from.Vendor; to.CatalogKey = from.CatalogKey; to.MatchQuality = from.MatchQuality;
        to.Current = from.Current; to.CurrentNumeric = from.CurrentNumeric; to.CurrentDate = from.CurrentDate;
        to.Latest = from.Latest; to.LatestNumeric = from.LatestNumeric; to.LatestDate = from.LatestDate;
        to.State = from.State; to.Critical = from.Critical; to.ChangeSummaryJson = from.ChangeSummaryJson; to.Detail = from.Detail;
        to.FwupdVersion = from.FwupdVersion; to.CheckedAt = from.CheckedAt; to.Source = from.Source;
    }

    /// <summary>Til Indstillinger → BIOS: én række pr. leverandør.</summary>
    public async Task<List<BiosVendorStatus>> Vendors(AppDb db, CancellationToken ct = default)
    {
        var counts = await db.BiosStatus.AsNoTracking().GroupBy(s => s.Vendor).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var v = lenovo;
        var lenovoText = v.Error is { } err
            ? $"Lenovos katalog kunne ikke hentes{(meta.ErrorSince is { } since ? " siden " + since.ToString("yyyy-MM-dd HH:mm") + " UTC" : "")}. Sidste kendte svar vises. ({err})"
            : v.Loaded ? "Hentet." : counts.GetValueOrDefault(BiosMatcher.Lenovo) > 0 ? "Ikke hentet endnu." : "Ingen Lenovo-maskiner i flåden – kataloget hentes ikke.";
        return
        [
            new(BiosMatcher.Lenovo, "Lenovo", true, v.FetchedAt, meta.LastAttempt, v.Error, meta.ErrorSince, v.Models.Count, counts.GetValueOrDefault(BiosMatcher.Lenovo), lenovoText),
            new(BiosMatcher.Dell, "Dell", false, null, null, null, null, 0, counts.GetValueOrDefault(BiosMatcher.Dell), "Understøttes ikke endnu."),
            new(BiosMatcher.Hp, "HP", false, null, null, null, null, 0, counts.GetValueOrDefault(BiosMatcher.Hp), "Understøttes ikke endnu."),
        ];
    }

    /// <summary>Lenovo-BIOS'en, en maskine matcher (til pakkehentning). Null, hvis matchet ikke er sikkert nok.</summary>
    public (LenovoModel Model, LenovoCatalogBios Bios, LenovoPackageDetails? Details, string Key)? LenovoFor(SystemFacts f)
    {
        var view = lenovo;
        if (!view.Loaded || BiosMatcher.VendorOf(f) != BiosMatcher.Lenovo || BiosMatcher.CurrentVersion(f) is not { } cv) return null;
        var m = BiosMatcher.MatchLenovo(f, LenovoBiosVersion.FromMachine(cv, f.BiosRelease), view.Models);
        if (m.Quality == BiosMatcher.Uncertain || m.Model?.Bios is not { } b) return null;
        return (m.Model, b, view.Packages.GetValueOrDefault(b.Stem), $"{b.Image}/{BiosMatcher.Mtm(f) ?? m.Model.Types.FirstOrDefault() ?? "x"}");
    }
}

/// <summary>Skemaet: første hentning 2 min efter start, derefter hver 24. time + 0-60 min tilfældigt; efter fejl 1 time, derefter 6 timer.</summary>
public sealed class BiosCatalogService(BiosCatalog catalog, BiosPackages packages, IServiceScopeFactory scopes, ILogger<BiosCatalogService> log) : BackgroundService
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2), Interval = TimeSpan.FromHours(24);

    public static TimeSpan NextDelay(bool ok, int failures, Random rnd) =>
        ok ? Interval + TimeSpan.FromMinutes(rnd.Next(0, 61)) : failures <= 1 ? TimeSpan.FromHours(1) : TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var failures = 0;
        try
        {
            await catalog.WaitForWake(FirstDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                bool ok;
                try { ok = await catalog.Refresh(ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "BIOS-katalog: tjekket fejlede"); ok = false; }
                failures = ok ? 0 : failures + 1;
                // pakker, der ikke er brugt i 90 dage, slettes også, selv om der aldrig hentes en ny
                try
                {
                    using var scope = scopes.CreateScope();
                    await packages.Prune(scope.ServiceProvider.GetRequiredService<AppDb>(), DateTime.UtcNow, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "BIOS-pakker: oprydningen fejlede"); }
                await catalog.WaitForWake(NextDelay(ok, failures, Random.Shared), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
