using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services.Bios;

/// <summary>Det, der skal hentes: adresse, filnavn og den kontrolsum, leverandøren oplyser (null = ingen; så låses den ved første hentning).</summary>
public sealed record BiosPackageSource(string Vendor, string Key, string Version, string Kind, string Url, string FileName, string? Sha256, long? Size, string Model);

/// <summary>
/// BIOS-pakker i serverens private cache (data/bios/&lt;vendor&gt;/&lt;key&gt;/&lt;version&gt;/&lt;fil&gt;). Hentes kun, når en administrator trykker
/// (aldrig automatisk), tjekkes mod leverandørens SHA-256 ved hentning og igen før hver brug, og skrives i revisionsloggen.
/// Oprydning: de 2 nyeste pr. model beholdes, og pakker der ikke er brugt i 90 dage slettes.
/// </summary>
public sealed partial class BiosPackages(Paths paths, IHttpClientFactory http, IServiceScopeFactory scopes, BiosCatalog catalog, ILogger<BiosPackages> log)
{
    public const string UsbZip = "usb-zip", WinExe = "win-exe";
    public const string Downloading = "downloading", Ready = "ready", Failed = "failed";
    public const string ChecksumError = "Pakken svarede ikke til leverandørens kontrolsum og er slettet.";
    public static readonly TimeSpan Unused = TimeSpan.FromDays(90);
    public const int KeepPerModel = 2;

    private sealed class Progress { public long Bytes, Total; }
    private readonly ConcurrentDictionary<int, Progress> progress = new();

    public string Root => Path.Combine(paths.Data, "bios");
    public string FullPath(BiosPackage p) => Path.Combine(paths.Data, p.Path);

    /// <summary>Hvad der skal hentes for maskinen (null + fejltekst, hvis der ingen sikker pakke er).</summary>
    public (BiosPackageSource? Source, string? Error) SourceFor(SystemFacts f, string kind)
    {
        if (catalog.LenovoFor(f) is not { } l)
            return (null, "Der er ingen sikker BIOS-pakke til denne maskine. Kun Lenovo understøttes endnu, og modellen skal genkendes sikkert.");
        var version = l.Bios.Parsed.Code ?? l.Bios.Version;
        switch (kind)
        {
            case WinExe:
                return (new(BiosMatcher.Lenovo, l.Key, version, kind, l.Bios.Url, FileOf(l.Bios.Url), l.Bios.Sha256 ?? l.Details?.Package.Installer?.Sha256,
                    l.Details?.Package.Installer?.Size, l.Model.Name), null);
            case UsbZip:
                if (LenovoCatalog.UsbZipUrl(l.Bios.Url) is not { } zip) return (null, "Lenovo har ingen USB-pakke til denne model.");
                return (new(BiosMatcher.Lenovo, l.Key, version, kind, zip, FileOf(zip), null, null, l.Model.Name), null);
            default:
                return (null, "Ukendt pakketype.");
        }
    }

    /// <summary>Bed om en pakke til maskinen. Findes den allerede (klar eller under hentning), gives den; ellers startes hentningen.</summary>
    public async Task<(BiosPackage? Package, string? Error)> Request(AppDb db, int machineId, string kind, string user, bool wait = false, CancellationToken ct = default)
    {
        var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == machineId, ct);
        if (m is null) return (null, "Maskinen findes ikke.");
        var (src, err) = SourceFor(Json.Des<SystemFacts>(m.FactsJson), kind);
        if (src is null) return (null, err);
        Task run;
        BiosPackage p;
        var added = -1;
        // tjek og start under én lås: to samtidige anmodninger (dobbeltklik, USB-guiden + "Hent pakke") starter kun én hentning
        await requestGate.WaitAsync(ct);
        try
        {
            p = await Upsert(db, src, user, ct);
            if (p.State == Ready && File.Exists(FullPath(p))) return (p, null);
            if (!progress.TryAdd(p.Id, new Progress { Total = src.Size ?? 0 })) return (p, null);   // hentes allerede
            added = p.Id;
            p.State = Downloading; p.Error = null; p.RequestedBy = user;
            await db.SaveChangesAsync(ct);
            await new AuditService(db).Log(user, "bios.package.download", $"BIOS-pakke hentes: {src.Model} {src.Version} ({src.Kind}, {src.FileName})", machineId);
            var id = p.Id;
            run = Task.Run(() => Run(id, src, CancellationToken.None), CancellationToken.None);
            added = -1;
        }
        finally
        {
            if (added >= 0) progress.TryRemove(added, out _);   // fejl før hentningen kom i gang
            requestGate.Release();
        }
        if (wait) await run;
        return (p, null);
    }

    private readonly SemaphoreSlim requestGate = new(1, 1);
    /// <summary>En hentning må højst tage så lang tid i alt (oven i grænsen for, hvor længe den må stå stille).</summary>
    public static TimeSpan DownloadTotal { get; set; } = TimeSpan.FromMinutes(20);

    private async Task<BiosPackage> Upsert(AppDb db, BiosPackageSource src, string user, CancellationToken ct)
    {
        var p = await db.BiosPackages.FirstOrDefaultAsync(x => x.Vendor == src.Vendor && x.CatalogKey == src.Key && x.Version == src.Version && x.Kind == src.Kind, ct);
        if (p is null)
        {
            p = new BiosPackage { Vendor = src.Vendor, CatalogKey = src.Key, Version = src.Version, Kind = src.Kind, RequestedBy = user };
            db.BiosPackages.Add(p);
        }
        p.SourceUrl = src.Url; p.FileName = src.FileName;
        if (src.Size is { } sz) p.Size = sz;
        if (src.Sha256 is { } sha) { p.Sha256 = sha; p.ShaSource = "catalog"; }
        else if (p.Sha256 is null) p.ShaSource = "tofu";
        p.Path = Path.Combine("bios", Safe(src.Vendor), Safe(src.Key), Safe(src.Version), Safe(src.FileName));
        await db.SaveChangesAsync(ct);
        return p;
    }

    /// <summary>Selve hentningen (i baggrunden). Fejl gemmes på rækken som en dansk tekst – aldrig en evig "henter".</summary>
    public async Task Run(int id, BiosPackageSource src, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var p = await db.BiosPackages.FirstAsync(x => x.Id == id, ct);
        var final = FullPath(p);
        var part = $"{final}.{Guid.NewGuid():N}.partial";   // hver hentning sin egen fil
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(DownloadTotal);
        ct = total.Token;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            var c = http.CreateClient(BiosHttp.ClientName);
            using var res = await BiosHttp.Send(c, src.Url, ct);
            if (!res.IsSuccessStatusCode)
                throw new BiosDownloadException(res.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "Leverandøren har ikke filen (ikke fundet)." : $"Leverandøren svarede HTTP {(int)res.StatusCode}.");
            var len = res.Content.Headers.ContentLength;
            if (len > BiosHttp.MaxPackageBytes) throw new BiosDownloadException("Pakken er større end 200 MB og hentes ikke.");
            if (src.Size is { } want && len is { } cl && want != cl) throw new BiosDownloadException("Pakkens størrelse passer ikke til kataloget.");
            var pr = progress.GetOrAdd(id, _ => new Progress());
            pr.Total = len ?? src.Size ?? 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long got = 0;
            var head = new byte[4];
            var headLen = 0;
            await using (var s = await res.Content.ReadAsStreamAsync(ct))
            await using (var o = File.Create(part))
            {
                var buf = new byte[1 << 20];
                await BiosHttp.Copy(s, buf, ct, async n =>
                {
                    got += n;
                    if (got > BiosHttp.MaxPackageBytes) throw new BiosDownloadException("Pakken er større end 200 MB og hentes ikke.");
                    for (var i = 0; i < n && headLen < head.Length; i++) head[headLen++] = buf[i];
                    hash.AppendData(buf, 0, n);
                    await o.WriteAsync(buf.AsMemory(0, n), ct);
                    pr.Bytes = got;
                });
            }
            if (src.Size is { } size && size != got) throw new BiosDownloadException("Pakkens størrelse passer ikke til kataloget.");
            if (!LooksRight(src.Kind, head.AsSpan(0, headLen))) throw new BiosDownloadException("Filen ligner ikke en BIOS-pakke.");
            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (p.Sha256 is { } expected && !string.Equals(expected, sha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(part);
                throw new BiosDownloadException(ChecksumError);
            }
            if (p.Sha256 is null) { p.Sha256 = sha; p.ShaSource = "tofu"; }   // kataloget har ingen kontrolsum: lås den nu og vis den i UI'en
            File.Move(part, final, true);
            p.Size = got; p.State = Ready; p.Error = null;
            p.Capsule = p.Kind == UsbZip ? BiosRom.ZipHasCapsule(final) : null;   // kapsel-vejen tilbydes kun, når pakken har en rigtig kapsel
            p.DownloadedAt = p.LastVerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await new AuditService(db).Log("system", "bios.package.ready", $"BIOS-pakke hentet: {src.FileName} · SHA-256 {sha} ({p.ShaSource})");
            await Prune(db, DateTime.UtcNow, ct);
        }
        catch (Exception e) when (e is BiosDownloadException or HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            TryDelete(part);
            log.LogInformation("BIOS: hentning af {File} fejlede: {Err}", src.FileName, e.Message);
            p.State = Failed;
            p.Error = e switch
            {
                BiosDownloadException => e.Message,
                OperationCanceledException => "Hentningen tog for lang tid. Prøv igen.",
                HttpRequestException => "Leverandøren kunne ikke nås. Prøv igen senere.",
                _ => "Pakken kunne ikke gemmes på serveren.",
            };
            if (p.Error.Length > 300) p.Error = p.Error[..300];
            await db.SaveChangesAsync(CancellationToken.None);
            await new AuditService(db).Log("system", "bios.package.failed", $"BIOS-pakke {src.FileName}: {p.Error}");
        }
        finally { progress.TryRemove(id, out _); }
    }

    /// <summary>Zip starter med "PK", Windows-programmer med "MZ".</summary>
    public static bool LooksRight(string kind, ReadOnlySpan<byte> head) => kind switch
    {
        UsbZip => head.Length >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K',
        WinExe => head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z',
        _ => true,
    };

    /// <summary>Tjek filen mod SHA-256 lige før brug. Passer den ikke, slettes den, og rækken markeres som fejlet. Null = i orden.</summary>
    public async Task<string?> Verify(AppDb db, BiosPackage p, CancellationToken ct = default)
    {
        var f = FullPath(p);
        if (p.State != Ready || !File.Exists(f)) return "Pakken er ikke hentet.";
        string got;
        await using (var s = File.OpenRead(f)) got = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
        if (p.Sha256 is null || !string.Equals(got, p.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(f);
            p.State = Failed; p.Error = ChecksumError;
            await db.SaveChangesAsync(ct);
            return ChecksumError;
        }
        p.LastVerifiedAt = p.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Behold de <see cref="KeepPerModel"/> nyeste pr. model og pakketype, og slet pakker der ikke er brugt i 90 dage.</summary>
    public async Task<int> Prune(AppDb db, DateTime now, CancellationToken ct = default)
    {
        var all = await db.BiosPackages.ToListAsync(ct);
        var drop = new List<BiosPackage>();
        foreach (var g in all.GroupBy(p => (p.Vendor, p.CatalogKey, p.Kind)))
        {
            var newest = g.Where(p => p.State != Downloading)
                .OrderByDescending(p => LenovoBiosVersion.FromCatalog(p.Version).Level ?? -1).ThenByDescending(p => p.DownloadedAt ?? p.Created).ToList();
            drop.AddRange(newest.Skip(KeepPerModel));
        }
        drop.AddRange(all.Where(p => p.State != Downloading && (p.LastUsedAt ?? p.DownloadedAt ?? p.Created) < now - Unused));
        foreach (var p in drop.Distinct())
        {
            TryDelete(FullPath(p));
            db.BiosPackages.Remove(p);
        }
        if (drop.Count > 0) await db.SaveChangesAsync(ct);
        return drop.Distinct().Count();
    }

    public async Task<bool> Delete(AppDb db, int id, string user)
    {
        var p = await db.BiosPackages.FindAsync(id);
        if (p is null) return false;
        TryDelete(FullPath(p));
        db.BiosPackages.Remove(p);
        await db.SaveChangesAsync();
        await new AuditService(db).Log(user, "bios.package.delete", $"BIOS-pakke slettet: {p.FileName} ({p.Version})");
        return true;
    }

    /// <summary>Henter serveren pakken lige nu?</summary>
    public bool InProgress(int id) => progress.ContainsKey(id);

    public object View(BiosPackage p) => new
    {
        p.Id, p.Vendor, p.CatalogKey, p.Version, p.Kind, p.FileName, p.Size, p.Sha256, p.ShaSource, p.State, p.Error,
        p.Created, p.DownloadedAt, p.LastVerifiedAt, p.LastUsedAt, p.RequestedBy,
        bytes = progress.TryGetValue(p.Id, out var pr) ? pr.Bytes : p.State == Ready ? p.Size : 0,
        total = progress.TryGetValue(p.Id, out var pt) ? pt.Total : p.Size,
        // en hentning, serveren ikke længere arbejder på (fx efter genstart), er reelt fejlet
        stale = p.State == Downloading && !progress.ContainsKey(p.Id),
    };

    /// <summary>"Hent pakke": Lenovos USB-pakke med LÆS-MIG.txt lagt i roden. Indholdet kopieres uændret.</summary>
    public static async Task WriteUsbZip(string source, Stream output, string readme, CancellationToken ct)
    {
        // ZipArchive skriver synkront (headere, central directory) – det afviser Kestrel på svarets stream. Derfor bygges
        // zip'en i en midlertidig fil, som så sendes asynkront og slettes af sig selv.
        var tmp = Path.Combine(Path.GetTempPath(), $"fd-bios-usb-{Guid.NewGuid():N}.zip");
        await using var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await BuildUsbZip(source, file, readme, ct);
        file.Position = 0;
        await file.CopyToAsync(output, ct);
    }

    /// <summary>Selve zip'en til USB-stikket (Lenovos filer + LÆS-MIG.txt) skrevet til en stream, der må skrives synkront.</summary>
    public static async Task BuildUsbZip(string source, Stream output, string readme, CancellationToken ct)
    {
        await using var src = File.OpenRead(source);
        using var zin = new ZipArchive(src, ZipArchiveMode.Read);
        await using var zout = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var e in zin.Entries)
        {
            if (e.FullName.Equals("LÆS-MIG.txt", StringComparison.OrdinalIgnoreCase)) continue;
            var ne = zout.CreateEntry(e.FullName, CompressionLevel.Optimal);
            ne.LastWriteTime = e.LastWriteTime;
            if (e.FullName.EndsWith('/')) continue;
            await using var i = e.Open();
            await using var o = ne.Open();
            await i.CopyToAsync(o, ct);
        }
        var r = zout.CreateEntry("LÆS-MIG.txt", CompressionLevel.Optimal);
        await using (var w = new StreamWriter(r.Open(), new UTF8Encoding(true))) await w.WriteAsync(readme.Replace("\n", "\r\n"));
    }

    /// <summary>LÆS-MIG.txt til Lenovos USB-pakke: trinene, versionen og kontrolsummen.</summary>
    public static string UsbReadme(string model, string version, string fileName, string? sha256, string shaSource) => $"""
        BIOS-opdatering med USB-stik
        ============================

        Model:     {model}
        Version:   {version}
        Pakke:     {fileName}
        SHA-256:   {sha256 ?? "ukendt"} ({(shaSource == "tofu" ? "låst ved første hentning – Lenovo oplyser ingen kontrolsum for USB-pakken" : "fra Lenovos katalog")})

        Før du starter
        - Strømmen må ikke gå under en BIOS-opdatering. Bærbare skal sidde i laderen.
        - Tag en backup af BIOS-indstillingerne i Fleetdeck først.
        - Har maskinen BitLocker, så sæt den på pause, før du opdaterer, ellers beder Windows om genoprettelsesnøglen.
          Har den Linux med LUKS og TPM, så hav genoprettelsesnøglen klar.

        Sådan laver du stikket
        1. Formatér et USB-stik som FAT32. Alt på stikket bliver slettet.
        2. Pak indholdet af denne zip ud i roden af stikket (mappen efi skal ligge øverst).

        Sådan opdaterer du
        1. Stikket starter et program, som Secure Boot ikke kender. Tryk F1 ved opstart og slå Secure Boot fra.
        2. Sæt stikket i, genstart, tryk F12 og vælg USB-stikket.
        3. Vent. Opdateringen kan tage op til 2 minutter, og maskinen kan genstarte op til 2 gange. Sluk ikke.
        4. Tag stikket ud, tryk F1 og slå Secure Boot til igen.
        5. Åbn maskinen i Fleetdeck og sammenlign BIOS-indstillingerne med backuppen.

        """;

    private static string FileOf(string url) => Safe(Path.GetFileName(new Uri(url).AbsolutePath));

    /// <summary>Kun bogstaver, tal, punktum, bindestreg og understreg i stierne – aldrig ".." eller skråstreger.</summary>
    public static string Safe(string s)
    {
        var t = SafeRx().Replace(s.Replace('/', '-').Replace('\\', '-'), "_").Replace("..", "_").Trim('.', '_', '-');
        return t.Length == 0 ? "_" : t.Length > 80 ? t[..80] : t;
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex SafeRx();
}
