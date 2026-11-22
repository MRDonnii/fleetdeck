using System.Net;

namespace Fleetdeck.Server.Services.Bios;

/// <summary>
/// HTTP til leverandørerne: kun HTTPS, kun værter på listen, omdirigeringer følges selv (klienten "bios" følger dem ikke) og kun
/// inden for listen, og en størrelsesgrænse. Bruges af både katalogtjenesten og pakkecachen.
/// </summary>
public static class BiosHttp
{
    public const string ClientName = "bios";
    public const long MaxPackageBytes = 200L * 1024 * 1024;
    public const long MaxCatalogBytes = 20L * 1024 * 1024;
    public const long MaxSmallBytes = 2L * 1024 * 1024;

    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "download.lenovo.com", "downloads.dell.com", "dl.dell.com", "ftp.hp.com",
    };

    public static bool Allowed(Uri u) => u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && AllowedHosts.Contains(u.Host) && string.IsNullOrEmpty(u.UserInfo);

    public static Uri Check(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && Allowed(u)
        ? u : throw new BiosDownloadException("Adressen er ikke hos en kendt BIOS-leverandør (kun HTTPS til Lenovo, Dell og HP).");

    /// <summary>GET (eller HEAD) med omdirigeringer inden for listen (højst 5). Kalderen ejer svaret.</summary>
    public static async Task<HttpResponseMessage> Send(HttpClient c, string url, CancellationToken ct, HttpMethod? method = null,
        Action<HttpRequestMessage>? configure = null)
    {
        var u = Check(url);
        for (var hop = 0; hop < 6; hop++)
        {
            using var req = new HttpRequestMessage(method ?? HttpMethod.Get, u);
            configure?.Invoke(req);
            var res = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)res.StatusCode is >= 300 and < 400 && res.StatusCode != HttpStatusCode.NotModified)
            {
                var loc = res.Headers.Location;
                res.Dispose();
                if (loc is null) throw new BiosDownloadException("Leverandøren omdirigerede uden en adresse.");
                var next = loc.IsAbsoluteUri ? loc : new Uri(u, loc);
                if (!Allowed(next)) throw new BiosDownloadException($"Leverandøren omdirigerede til {next.Host}, som ikke er på listen over tilladte værter.");
                u = next;
                continue;
            }
            return res;
        }
        throw new BiosDownloadException("For mange omdirigeringer.");
    }

    /// <summary>
    /// Klientens Timeout gælder kun, til svarets headers er kommet (ResponseHeadersRead). Selve læsningen har derfor sin egen grænse:
    /// højst <see cref="IdleTimeout"/> uden nye data og højst <see cref="SmallTotal"/> for en lille fil.
    /// </summary>
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public static TimeSpan SmallTotal { get; set; } = TimeSpan.FromMinutes(5);
    public const string Stalled = "Forbindelsen til leverandøren gik i stå. Prøv igen.";

    /// <summary>Hent en lille fil (katalog, pakke-XML, readme) som bytes, med grænse.</summary>
    public static async Task<byte[]> Bytes(HttpClient c, string url, long limit, CancellationToken ct)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(SmallTotal);
        try
        {
            using var res = await Send(c, url, total.Token);
            if (!res.IsSuccessStatusCode) throw new BiosDownloadException($"{Path.GetFileName(new Uri(url).AbsolutePath)}: HTTP {(int)res.StatusCode}");
            return await Read(res, limit, total.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new BiosDownloadException(Stalled); }
    }

    public static async Task<byte[]> Read(HttpResponseMessage res, long limit, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength is { } len && len > limit) throw new BiosDownloadException("Filen er større end tilladt.");
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        await Copy(s, buf, ct, n =>
        {
            ms.Write(buf, 0, n);
            if (ms.Length > limit) throw new BiosDownloadException("Filen er større end tilladt.");
            return Task.CompletedTask;
        });
        return ms.ToArray();
    }

    /// <summary>Læs strømmen til ende; hver ReadAsync må højst vente <see cref="IdleTimeout"/>. En forbindelse, der går i stå, giver <see cref="Stalled"/>.</summary>
    public static async Task Copy(Stream s, byte[] buf, CancellationToken ct, Func<int, Task> chunk)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            idle.CancelAfter(IdleTimeout);
            int n;
            try { n = await s.ReadAsync(buf, idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new BiosDownloadException(Stalled); }
            if (n <= 0) return;
            await chunk(n);
        }
    }
}

/// <summary>En fejl med en dansk tekst, der kan vises til brugeren.</summary>
public sealed class BiosDownloadException(string message) : Exception(message);
