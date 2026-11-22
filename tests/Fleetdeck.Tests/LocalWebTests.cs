using System.Net;
using System.Net.Sockets;
using System.Text;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>ESP Flasher uden certifikat: agentens lille proxy på http://localhost (se LocalWeb).</summary>
public class LocalWebTests
{
    private static LocalWeb.Request Req(string head) => LocalWeb.Parse(Encoding.Latin1.GetBytes(head))!;

    [Fact]
    public void Parse_reads_request_line_and_headers()
    {
        var r = Req("GET /esp?x=1 HTTP/1.1\r\nHost: localhost:8587\r\nCookie: ws_session=abc\r\nAccept: */*");
        Assert.Equal("GET", r.Method);
        Assert.Equal("/esp?x=1", r.Target);
        Assert.Equal("localhost:8587", r.All("host").Single());
        Assert.Equal("ws_session=abc", r.All("Cookie").Single());
    }

    [Theory]
    [InlineData("GET /x HTTP/2.0\r\nHost: localhost:8587")]          // kun HTTP/1.x
    [InlineData("GET  /x HTTP/1.1\r\nHost: localhost:8587")]         // dobbelt mellemrum
    [InlineData("GET /x HTTP/1.1\r\nHost: localhost:8587\r\n X: y")] // foldet linje
    [InlineData("GET /x HTTP/1.1\r\nBad Name: y")]                   // mellemrum i navnet
    [InlineData("GET /x HTTP/1.1\r\nHost: a\nX: b")]                 // løst LF
    [InlineData("GET /x HTTP/1.1\r\nX: a\u0000b")]                   // kontroltegn
    public void Parse_rejects_malformed(string head) => Assert.Null(LocalWeb.Parse(Encoding.Latin1.GetBytes(head)));

    [Theory]
    [InlineData("localhost:8587", true)]
    [InlineData("LOCALHOST:8587", true)]
    [InlineData("127.0.0.1:8587", true)]
    [InlineData("[::1]:8587", true)]
    [InlineData("localhost", false)]            // uden port
    [InlineData("localhost:8080", false)]       // anden port
    [InlineData("evil.example:8587", false)]    // DNS-rebinding
    [InlineData("127.0.0.2:8587", false)]
    [InlineData("localhost.evil.example:8587", false)]
    [InlineData(null, false)]
    public void Host_must_be_local_name_with_our_port(string? host, bool ok) => Assert.Equal(ok, LocalWeb.HostAllowed(host, 8587));

    [Theory]
    [InlineData("http://localhost:8587", true)]
    [InlineData("http://127.0.0.1:8587", true)]
    [InlineData("https://localhost:8587", false)]
    [InlineData("http://localhost:3000", false)]   // anden lokal side (samme "site", men ikke samme oprindelse)
    [InlineData("http://evil.example:8587", false)]
    [InlineData("null", false)]
    public void Origin_must_be_our_own(string origin, bool ok) => Assert.Equal(ok, LocalWeb.OriginAllowed(origin, 8587));

    [Fact]
    public void Refuse_checks_host_origin_and_target()
    {
        Assert.Null(LocalWeb.Refuse(Req("GET /esp HTTP/1.1\r\nHost: localhost:8587"), 8587));
        Assert.Null(LocalWeb.Refuse(Req("POST /api/auth/login HTTP/1.1\r\nHost: localhost:8587\r\nOrigin: http://localhost:8587"), 8587));
        Assert.Equal(421, LocalWeb.Refuse(Req("GET /esp HTTP/1.1\r\nHost: evil.example:8587"), 8587)!.Status);
        Assert.Equal(421, LocalWeb.Refuse(Req("GET /esp HTTP/1.1"), 8587)!.Status);
        Assert.Equal(421, LocalWeb.Refuse(Req("GET /esp HTTP/1.1\r\nHost: localhost:8587\r\nHost: evil.example"), 8587)!.Status);
        Assert.Equal(403, LocalWeb.Refuse(Req("GET /hubs/ui HTTP/1.1\r\nHost: localhost:8587\r\nOrigin: http://evil.example"), 8587)!.Status);
        Assert.Equal(400, LocalWeb.Refuse(Req("GET http://other.example/ HTTP/1.1\r\nHost: localhost:8587"), 8587)!.Status);
        Assert.Equal(400, LocalWeb.Refuse(Req("CONNECT other.example:443 HTTP/1.1\r\nHost: localhost:8587"), 8587)!.Status);
    }

    [Fact]
    public void Upstream_head_rewrites_host_and_strips_client_proxy_claims()
    {
        var r = Req("POST /api/esp/check HTTP/1.1\r\nHost: localhost:8587\r\nConnection: keep-alive, X-Secret\r\nX-Secret: 1\r\n"
            + "X-Forwarded-For: 1.2.3.4\r\nX-Forwarded-Host: evil\r\nX-Forwarded-Prefix: /enroll\r\nCookie: ws_session=abc\r\nX-Fleetdeck: 1\r\nContent-Length: 2");
        var head = Encoding.Latin1.GetString(LocalWeb.UpstreamHead(r, new Uri("https://fleet.example:8443"), 8587));
        Assert.StartsWith("POST /api/esp/check HTTP/1.1\r\nHost: fleet.example:8443\r\n", head);
        Assert.Contains("\r\nCookie: ws_session=abc\r\n", head);
        Assert.Contains("\r\nX-Fleetdeck: 1\r\n", head);
        Assert.Contains("\r\nContent-Length: 2\r\n", head);
        Assert.Contains("\r\nX-Forwarded-Host: localhost:8587\r\n", head);
        Assert.Contains("\r\nX-Forwarded-Proto: http\r\n", head);
        Assert.Contains("\r\nConnection: close\r\n", head);
        Assert.DoesNotContain("1.2.3.4", head);
        Assert.DoesNotContain("evil", head);
        Assert.DoesNotContain("/enroll", head);
        Assert.DoesNotContain("X-Secret", head);                  // nævnt i Connection = hop-by-hop
        Assert.DoesNotContain("Authorization", head);             // agentens token kommer aldrig med
        Assert.EndsWith("\r\n\r\n", head);
    }

    [Fact]
    public void Upstream_head_keeps_websocket_upgrade_and_server_path()
    {
        var r = Req("GET /hubs/ui?id=1 HTTP/1.1\r\nHost: 127.0.0.1:8587\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: k\r\nOrigin: http://127.0.0.1:8587");
        var head = Encoding.Latin1.GetString(LocalWeb.UpstreamHead(r, new Uri("http://10.9.8.7:8080/fleet/"), 8587));
        Assert.StartsWith("GET /fleet/hubs/ui?id=1 HTTP/1.1\r\nHost: 10.9.8.7:8080\r\n", head);
        Assert.Contains("\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n", head);
        Assert.Contains("\r\nSec-WebSocket-Key: k\r\n", head);
        Assert.DoesNotContain("Connection: close", head);
    }

    [Theory]
    [InlineData("http://fleet.example:8080", true)]
    [InlineData("https://fleet.example", true)]
    [InlineData("unix:/var/run/fleetdeck.sock", false)]
    [InlineData("ftp://fleet.example", false)]
    [InlineData("http://user:pw@fleet.example", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Target_is_only_the_agents_http_server(string? server, bool ok) => Assert.Equal(ok, LocalWeb.Target(server) is not null);

    [Fact]
    public void Ports_and_tray_url()
    {
        Assert.Equal(LocalWeb.DefaultPort, LocalWeb.ResolvePort(null));
        Assert.Equal(0, LocalWeb.ResolvePort(0));
        Assert.Equal(9000, LocalWeb.ResolvePort(9000));
        Assert.Equal(LocalWeb.DefaultPort, LocalWeb.ResolvePort(70000));
        Assert.Equal("http://localhost:8587/esp", LocalWeb.EspUrl("http://localhost:8587/", "http://fleet:8080"));
        Assert.Equal("http://fleet:8080/esp", LocalWeb.EspUrl(null, "http://fleet:8080/"));
        Assert.Null(LocalWeb.EspUrl(null, "unix:/x.sock"));
    }

    // ---------- hele vejen: browser → proxy på loopback → en lille "server" ----------

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    /// <summary>En server, der svarer med den forespørgsel, den fik (og lukker, som Kestrel gør ved Connection: close).</summary>
    private static (TcpListener, Task) Echo(CancellationToken ct)
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var t = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                using var c = await l.AcceptTcpClientAsync(ct);
                var s = c.GetStream();
                var buf = new byte[8192]; var len = 0;
                while (LocalWeb.HeadEnd(buf.AsSpan(0, len)) < 0) { var n = await s.ReadAsync(buf.AsMemory(len), ct); if (n == 0) break; len += n; }
                var end = LocalWeb.HeadEnd(buf.AsSpan(0, len));
                var head = Encoding.Latin1.GetString(buf, 0, end);
                var cl = LocalWeb.Parse(buf.AsSpan(0, end))!.All("Content-Length").Select(int.Parse).FirstOrDefault();
                while (len - end - 4 < cl) len += await s.ReadAsync(buf.AsMemory(len), ct);
                var body = Encoding.UTF8.GetBytes(head + "\n--\n" + Encoding.UTF8.GetString(buf, end + 4, cl));
                await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
                await s.WriteAsync(body, ct);
            }
        }, ct);
        return (l, t);
    }

    private static async Task<string> Raw(int port, string request)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.Latin1.GetBytes(request));
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms).WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task Proxy_forwards_allowed_requests_and_refuses_others()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (up, _) = Echo(cts.Token);
        var upstream = $"http://127.0.0.1:{((IPEndPoint)up.LocalEndpoint).Port}";
        var port = FreePort();
        var ready = new TaskCompletionSource<string?>();
        var run = LocalWeb.Run(port, () => upstream, _ => { }, u => { if (u is not null) ready.TrySetResult(u); }, cts.Token);
        Assert.Equal($"http://localhost:{port}/", await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var ok = await Raw(port, $"POST /api/esp/check HTTP/1.1\r\nHost: localhost:{port}\r\nOrigin: http://localhost:{port}\r\nX-Fleetdeck: 1\r\nX-Forwarded-For: 6.6.6.6\r\nContent-Length: 5\r\n\r\nhello");
        Assert.StartsWith("HTTP/1.1 200 OK", ok);
        Assert.Contains($"Host: 127.0.0.1:{((IPEndPoint)up.LocalEndpoint).Port}", ok);
        Assert.Contains($"X-Forwarded-Host: localhost:{port}", ok);
        Assert.Contains("Connection: close", ok);
        Assert.DoesNotContain("6.6.6.6", ok);
        Assert.EndsWith("\n--\nhello", ok);

        Assert.StartsWith("HTTP/1.1 421", await Raw(port, $"GET /esp HTTP/1.1\r\nHost: rebound.example:{port}\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 403", await Raw(port, $"GET /hubs/ui HTTP/1.1\r\nHost: localhost:{port}\r\nOrigin: http://evil.example\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 400", await Raw(port, "garbage\r\n\r\n"));

        // HttpClient som en browser: almindelig GET gennem proxyen
        using var http = new HttpClient();
        var page = await http.GetStringAsync($"http://localhost:{port}/esp");
        Assert.Contains("GET /esp HTTP/1.1", page);

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        up.Stop();
    }

    [Fact]
    public async Task Unreachable_server_gives_a_danish_502()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var port = FreePort();
        var dead = $"http://127.0.0.1:{FreePort()}";
        var ready = new TaskCompletionSource();
        var run = LocalWeb.Run(port, () => dead, _ => { }, u => { if (u is not null) ready.TrySetResult(); }, cts.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var res = await Raw(port, $"GET /esp HTTP/1.1\r\nHost: localhost:{port}\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 502", res);
        Assert.Contains("kan ikke nås", res);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Busy_port_is_reported_and_port_zero_is_off()
    {
        var off = new List<string?>();
        await LocalWeb.Run(0, () => "http://x", _ => { }, off.Add, CancellationToken.None);
        Assert.Equal([null], off);

        var busy = new TcpListener(IPAddress.Loopback, 0); busy.Start();
        var port = ((IPEndPoint)busy.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        var logged = new TaskCompletionSource<string>();
        var run = LocalWeb.Run(port, () => "http://x", m => logged.TrySetResult(m), _ => { }, cts.Token);
        Assert.Contains("kan ikke bruges", await logged.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        busy.Stop();
    }
}
