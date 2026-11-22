using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Fleetdeck.Shared;

/// <summary>Et overvågningstjek, som en agent skal køre fra sin egen maskine (fx noget, serveren ikke selv kan nå).</summary>
public sealed class AgentCheck
{
    public int Id { get; set; }
    public string Type { get; set; } = "http";       // http | tcp | ping | dns
    public string Target { get; set; } = "";
    public string? Keyword { get; set; }
    public bool KeywordInvert { get; set; }
    public string? ExpectStatus { get; set; }
    public bool IgnoreTls { get; set; }
    public string? Expect { get; set; }
    public int TimeoutSec { get; set; } = 10;
}

public sealed class AgentCheckResult
{
    public int Id { get; set; }
    public bool Up { get; set; }
    public int? Ms { get; set; }
    public string Message { get; set; } = "";
    public DateTime? CertExpires { get; set; }
}

/// <summary>Tjekkene, som både serveren og agenterne bruger: HTTP(S) med statuskode/nøgleord/certifikat, TCP-port, ping og DNS.</summary>
public static class Probe
{
    public static async Task<AgentCheckResult> Run(AgentCheck c, CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(c.TimeoutSec, 1, 120));
        try
        {
            return c.Type switch
            {
                "http" => await Http(c, timeout, ct),
                "tcp" => await Tcp(c, timeout, ct),
                "ping" => await PingHost(c, timeout),
                "dns" => await Dns(c, timeout, ct),
                _ => Fail(c, $"Typen {c.Type} kan ikke køres herfra"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fail(c, $"Intet svar inden for {timeout.TotalSeconds:0} sek."); }
        catch (Exception e) when (e is HttpRequestException or SocketException or IOException or PingException or UriFormatException or ArgumentException
                                   or InvalidOperationException or System.Security.Authentication.AuthenticationException)
        {
            var inner = e.InnerException?.Message;
            var msg = inner is { Length: > 0 } && !e.Message.Contains(inner) ? $"{e.Message} ({inner})" : e.Message;
            return Fail(c, msg.Length > 280 ? msg[..280] : msg);
        }
    }

    private static AgentCheckResult Fail(AgentCheck c, string msg, int? ms = null, DateTime? cert = null) =>
        new() { Id = c.Id, Up = false, Ms = ms, Message = msg, CertExpires = cert };
    private static AgentCheckResult Ok(AgentCheck c, string msg, int? ms, DateTime? cert = null) =>
        new() { Id = c.Id, Up = true, Ms = ms, Message = msg, CertExpires = cert };

    private static async Task<AgentCheckResult> Http(AgentCheck c, TimeSpan timeout, CancellationToken ct)
    {
        DateTime? certEnd = null; string? certErr = null;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 5, ConnectTimeout = timeout,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert is X509Certificate2 x) certEnd = x.NotAfter.ToUniversalTime();
                    if (errors != SslPolicyErrors.None) certErr = errors.ToString();
                    return c.IgnoreTls || errors == SslPolicyErrors.None;
                },
            },
        };
        using var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Fleetdeck-Overvaagning/1.0");
        var sw = Stopwatch.StartNew();
        using var res = await http.GetAsync(c.Target, HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)res.StatusCode;
        var body = "";
        if (!string.IsNullOrEmpty(c.Keyword))
        {
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            var buf = new byte[1 << 20]; var n = 0; int r;
            while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) n += r;
            body = System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        var ms = (int)sw.ElapsedMilliseconds;
        if (!StatusOk(code, c.ExpectStatus)) return Fail(c, $"HTTP {code} {res.ReasonPhrase}", ms, certEnd);
        if (!string.IsNullOrEmpty(c.Keyword) && body.Contains(c.Keyword, StringComparison.OrdinalIgnoreCase) == c.KeywordInvert)
            return Fail(c, c.KeywordInvert ? $"Siden indeholder \"{c.Keyword}\"" : $"\"{c.Keyword}\" står ikke på siden", ms, certEnd);
        var msg = $"HTTP {code}";
        if (certEnd is { } end) msg += $" · certifikat udløber om {(int)Math.Floor((end - DateTime.UtcNow).TotalDays)} dage";
        if (certErr is not null && c.IgnoreTls) msg += " · certifikatfejl ignoreret";
        return Ok(c, msg, ms, certEnd);
    }

    /// <summary>"200-399" (standard), "200,204" eller "2xx".</summary>
    public static bool StatusOk(int code, string? expect)
    {
        if (string.IsNullOrWhiteSpace(expect)) return code is >= 200 and < 400;
        foreach (var part in expect.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 3 && part.EndsWith("xx", StringComparison.OrdinalIgnoreCase) && char.IsDigit(part[0]) && code / 100 == part[0] - '0') return true;
            var d = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (d.Length == 2 && int.TryParse(d[0], out var lo) && int.TryParse(d[1], out var hi) && code >= lo && code <= hi) return true;
            if (d.Length == 1 && int.TryParse(d[0], out var one) && code == one) return true;
        }
        return false;
    }

    private static async Task<AgentCheckResult> Tcp(AgentCheck c, TimeSpan timeout, CancellationToken ct)
    {
        var t = c.Target.Trim();
        string host; int port;
        if (t.Contains("://")) { var u = new Uri(t); host = u.Host; port = u.Port; }
        else
        {
            var i = t.LastIndexOf(':');
            if (i <= 0 || !int.TryParse(t[(i + 1)..], out port)) return Fail(c, "Skriv vært:port, fx 192.168.1.25:8123");
            host = t[..i].Trim('[', ']');
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        using var tcp = new TcpClient();
        var sw = Stopwatch.StartNew();
        await tcp.ConnectAsync(host, port, cts.Token);
        return Ok(c, $"Port {port} er åben", (int)sw.ElapsedMilliseconds);
    }

    private static async Task<AgentCheckResult> PingHost(AgentCheck c, TimeSpan timeout)
    {
        using var p = new Ping();
        var r = await p.SendPingAsync(c.Target.Trim(), timeout);
        return r.Status == IPStatus.Success ? Ok(c, $"Svarer på ping ({r.Address})", (int)r.RoundtripTime)
            : Fail(c, r.Status == IPStatus.TimedOut ? "Svarer ikke på ping" : $"Ping: {r.Status}");
    }

    private static async Task<AgentCheckResult> Dns(AgentCheck c, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        var ips = await System.Net.Dns.GetHostAddressesAsync(c.Target.Trim(), cts.Token);
        var ms = (int)sw.ElapsedMilliseconds;
        var list = string.Join(", ", ips.Select(i => i.ToString()));
        if (ips.Length == 0) return Fail(c, "Navnet giver ingen adresser", ms);
        if (!string.IsNullOrWhiteSpace(c.Expect) && !list.Contains(c.Expect.Trim(), StringComparison.OrdinalIgnoreCase))
            return Fail(c, $"Svarer {list} – forventede {c.Expect}", ms);
        return Ok(c, list.Length > 200 ? list[..200] : list, ms);
    }
}
