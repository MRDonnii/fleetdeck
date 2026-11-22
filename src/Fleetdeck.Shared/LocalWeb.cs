using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Fleetdeck.Shared;

/// <summary>
/// ESP Flasher uden certifikat: USB fra browseren (Web Serial) kræver en sikker side, og Chrome og Edge regner
/// <c>http://localhost</c> som sikker. Agenten lytter derfor KUN på maskinens egen loopback (127.0.0.1 og ::1) og sender
/// forespørgslerne videre til sin Fleetdeck-server – brugerfladen, /api (med brugerens egen login-cookie), websockets og
/// firmwarefilerne. Så virker ESP Flasher fra enhver pc med agenten, uden at installere et certifikat.
/// <para>Sikkerhed:</para>
/// <list type="bullet">
/// <item>Kun loopback: der lyttes ikke på netværket, og forbindelser fra andre adresser lukkes.</item>
/// <item>Kun Host <c>localhost</c>, <c>127.0.0.1</c> eller <c>[::1]</c> med den rigtige port (værn mod DNS-rebinding).</item>
/// <item>En <c>Origin</c> fra en anden side afvises (også websockets, som CORS ikke dækker).</item>
/// <item>Én forespørgsel pr. forbindelse (<c>Connection: close</c>), så hver forespørgsel bliver tjekket.</item>
/// <item>Målet er altid agentens egen server; adresser i forespørgslen (absolut form, CONNECT) afvises.</item>
/// <item>Agentens token sendes aldrig med – kun det, browseren selv sender. Klientens egne X-Forwarded-* fjernes.</item>
/// </list>
/// </summary>
public static class LocalWeb
{
    /// <summary>Fast standardport. Kan ændres pr. pc (agentens konfiguration: LocalWebPort, 0 = slået fra) og for linket på
    /// ESP-siden (serverens miljøvariabel <see cref="EnvPort"/>).</summary>
    public const int DefaultPort = 8587;
    public const string EnvPort = "LOCAL_WEB_PORT";
    private const int MaxHead = 32 * 1024, MaxConnections = 64;

    /// <summary>Porten ud fra konfigurationen: null = standard, 0 = slået fra, ellers 1–65535.</summary>
    public static int ResolvePort(int? configured) => configured switch { null => DefaultPort, >= 0 and <= 65535 => configured.Value, _ => DefaultPort };

    /// <summary>Porten, serveren viser i linket "Åbn ESP Flasher på denne pc".</summary>
    public static int ServerPort() =>
        int.TryParse(Environment.GetEnvironmentVariable(EnvPort), out var p) && p is > 0 and <= 65535 ? p : DefaultPort;

    public static string Url(int port) => $"http://localhost:{port}/";

    /// <summary>"ESP Flasher" i bakkemenuen: gennem agenten på localhost, når den lytter – ellers Fleetdecks egen side (null = ingen adresse).</summary>
    public static string? EspUrl(string? localUrl, string? webUrl)
    {
        if (localUrl is { Length: > 0 } l && l.StartsWith("http://", StringComparison.Ordinal)) return l.TrimEnd('/') + "/esp";
        return webUrl is { Length: > 0 } w && w.StartsWith("http", StringComparison.Ordinal) ? w.TrimEnd('/') + "/esp" : null;
    }

    /// <summary>
    /// Lyt på loopback, indtil <paramref name="ct"/> stopper. <paramref name="server"/> læses for hver forespørgsel (så "Skift
    /// server" slår igennem). <paramref name="listening"/> får adressen, mens der lyttes, og null, når porten ikke kan bruges.
    /// </summary>
    public static async Task Run(int port, Func<string?> server, Action<string> log, Action<string?> listening, CancellationToken ct, LocalHandler? local = null)
    {
        if (port <= 0) { listening(null); return; }
        var warned = false;
        var gate = new SemaphoreSlim(MaxConnections);
        while (!ct.IsCancellationRequested)
        {
            var sockets = new List<Socket>();
            try
            {
                sockets.Add(Listen(IPAddress.Loopback, port));
                if (Socket.OSSupportsIPv6)
                    try { sockets.Add(Listen(IPAddress.IPv6Loopback, port)); } catch (SocketException) { /* kun IPv4 */ }
            }
            catch (SocketException e)
            {
                foreach (var s in sockets) s.Dispose();
                if (!warned) { log($"ESP Flasher på denne pc: port {port} kan ikke bruges ({e.SocketErrorCode}). Prøver igen hvert minut."); warned = true; }
                listening(null);
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            warned = false;
            log($"ESP Flasher på denne pc: {Url(port)}esp (kun fra maskinen selv)");
            listening(Url(port));
            try { await Task.WhenAll(sockets.Select(s => Accept(s, port, server, gate, ct, local))); }
            finally { foreach (var s in sockets) s.Dispose(); listening(null); }
        }
    }

    private static Socket Listen(IPAddress ip, int port)
    {
        var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Windows: ingen anden proces må lægge sig på samme port og snuppe forbindelserne
            if (OperatingSystem.IsWindows()) s.ExclusiveAddressUse = true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) s.DualMode = false;
            s.Bind(new IPEndPoint(ip, port));
            s.Listen(128);
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    private static async Task Accept(Socket listener, int port, Func<string?> server, SemaphoreSlim gate, CancellationToken ct, LocalHandler? local)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket c;
            try { c = await listener.AcceptAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (c.RemoteEndPoint is not IPEndPoint rep || !IPAddress.IsLoopback(rep.Address) || !gate.Wait(0)) { c.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await Serve(c, port, server, ct, local); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException) { }
                finally { gate.Release(); c.Dispose(); }
            }, CancellationToken.None);
        }
    }

    private static async Task Serve(Socket c, int port, Func<string?> server, CancellationToken ct, LocalHandler? local)
    {
        c.NoDelay = true;
        await using var client = new NetworkStream(c, ownsSocket: false);
        var buf = new byte[MaxHead];
        int len = 0, end;
        using (var head = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            head.CancelAfter(TimeSpan.FromSeconds(15));
            while ((end = HeadEnd(buf.AsSpan(0, len))) < 0)
            {
                if (len == buf.Length) { await Reply(client, 431, "For store headere.", ct); return; }
                var n = await client.ReadAsync(buf.AsMemory(len), head.Token);
                if (n == 0) return;
                len += n;
            }
        }
        if (Parse(buf.AsSpan(0, end)) is not { } req) { await Reply(client, 400, "Ugyldig forespørgsel.", ct); return; }
        if (Refuse(req, port) is { } no) { await Reply(client, no.Status, no.Text, ct); return; }
        if (IsLocal(req.Target))
        {
            // /_fd/…: håndteres af agenten selv og sendes aldrig videre til serveren
            var reply = LocalRefuse(req, port) is { } lr ? LocalReply.Error(lr.Status, lr.Text)
                : local is null ? LocalReply.Error(404, "Denne pc kan ikke det endnu.")
                : await SafeLocal(local, req, ct);
            await ReplyJson(client, reply, ct);
            return;
        }
        if (Target(server()) is not { } target) { await Reply(client, 503, "Agenten har ingen Fleetdeck-server at sende videre til.", ct); return; }
        Stream up;
        try { up = await Connect(target, ct); }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            if (ct.IsCancellationRequested) return;
            await Reply(client, 502, $"Fleetdeck-serveren ({target.Authority}) kan ikke nås fra denne pc lige nu.", ct);
            return;
        }
        await using (up)
        {
            await up.WriteAsync(UpstreamHead(req, target, port), ct);
            if (len > end + 4) await up.WriteAsync(buf.AsMemory(end + 4, len - end - 4), ct);
            await up.FlushAsync(ct);
            await Pipe(client, up, ct);
        }
    }

    /// <summary>Svar én vej ad gangen, til serveren lukker (eller browseren går). Websockets og SSE løber bare igennem.</summary>
    private static async Task Pipe(Stream client, Stream up, CancellationToken ct)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var down = Copy(up, client, both.Token);
        var upward = Copy(client, up, both.Token);
        if (await Task.WhenAny(down, upward) == upward)
            await Task.WhenAny(down, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));   // browseren er gået: lidt tid til et sidste svar
        both.Cancel();
        await Task.WhenAll(down, upward);
    }

    private static async Task Copy(Stream from, Stream to, CancellationToken ct)
    {
        try { await from.CopyToAsync(to, 16 * 1024, ct); await to.FlushAsync(ct); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task<Stream> Connect(Uri u, CancellationToken ct)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        to.CancelAfter(TimeSpan.FromSeconds(10));
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await s.ConnectAsync(new DnsEndPoint(u.IdnHost, u.Port), to.Token); }
        catch { s.Dispose(); throw; }
        var ns = new NetworkStream(s, ownsSocket: true);
        if (u.Scheme != Uri.UriSchemeHttps) return ns;
        // samme tillid som agenten selv (systemets certifikater eller det fastlåste fingeraftryk) – men ingen klientnøgle eller token
        var tls = new SslStream(ns, leaveInnerStreamOpen: false, TlsPinning.Validate);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = u.IdnHost, ApplicationProtocols = [SslApplicationProtocol.Http11],
            }, to.Token);
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    /// <summary>Et lokalt endpoint (/_fd/…): sti, forespørgselsparametre → svar. Kun agenten selv svarer; intet sendes videre.</summary>
    public delegate Task<LocalReply> LocalHandler(string path, IReadOnlyDictionary<string, string> query, CancellationToken ct);

    public sealed record LocalReply(int Status, string Body)
    {
        public static LocalReply Json(object o) => new(200, System.Text.Json.JsonSerializer.Serialize(o, JsonWeb));
        public static LocalReply Error(int status, string text) => new(status, System.Text.Json.JsonSerializer.Serialize(new { error = text }, JsonWeb));
    }
    private static readonly System.Text.Json.JsonSerializerOptions JsonWeb = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static async Task<LocalReply> SafeLocal(LocalHandler local, Request req, CancellationToken ct)
    {
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(30));
            var (path, q) = SplitTarget(req.Target);
            return await local(path, q, to.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        { return LocalReply.Error(500, "Agenten kunne ikke svare: " + e.Message); }
    }

    private static async Task ReplyJson(Stream s, LocalReply r, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(r.Body);
        var reason = r.Status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", _ => "Error" };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {r.Status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        try { await s.WriteAsync(head, ct); await s.WriteAsync(body, ct); await s.FlushAsync(ct); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
    }

    // ---------- rene funktioner (testes direkte) ----------

    /// <summary>Stier, agenten selv svarer på (USB-stik til BIOS: /_fd/claim og /_fd/confirm).</summary>
    public static bool IsLocal(string target) => target.StartsWith("/_fd/", StringComparison.Ordinal) || target == "/_fd";

    /// <summary>
    /// /_fd/… kræver mere end resten: POST, en Origin fra netop denne side (ikke bare "ingen Origin") og headeren X-Fleetdeck: 1, som en
    /// anden side ikke kan sætte uden at spørge først. Så kan en fremmed side eller en almindelig navigation aldrig bekræfte et stik.
    /// </summary>
    public static Refusal? LocalRefuse(Request r, int port)
    {
        if (r.Method != "POST") return new(405, "Kun POST.");
        var origins = r.All("Origin").ToList();
        if (origins.Count != 1 || !OriginAllowed(origins[0], port)) return new(403, "Kun Fleetdeck på denne pc.");
        if (!r.All("X-Fleetdeck").Any(v => v.Trim() == "1")) return new(403, "Mangler X-Fleetdeck.");
        if (r.All("Content-Length").Any(v => v.Trim() != "0") || r.All("Transfer-Encoding").Any()) return new(400, "Ingen indhold – kun parametre i adressen.");
        return null;
    }

    /// <summary>Sti og parametre (afkodet). Ukendte eller dobbelte parametre: den første tæller.</summary>
    public static (string Path, IReadOnlyDictionary<string, string> Query) SplitTarget(string target)
    {
        var i = target.IndexOf('?');
        var path = i < 0 ? target : target[..i];
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        if (i >= 0)
            foreach (var part in target[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var k = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
                var v = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
                q.TryAdd(k, v);
            }
        return (path, q);
    }

    public sealed record Request(string Method, string Target, string Version, IReadOnlyList<KeyValuePair<string, string>> Headers)
    {
        public IEnumerable<string> All(string name) =>
            Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
    }

    public sealed record Refusal(int Status, string Text);

    /// <summary>Agentens serveradresse som mål – kun http(s). En unix-socket (agenten på selve værten) bruges ikke.</summary>
    public static Uri? Target(string? server) =>
        Uri.TryCreate(server?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
        && u.Host.Length > 0 && string.IsNullOrEmpty(u.UserInfo) ? u : null;

    public static int HeadEnd(ReadOnlySpan<byte> b) => b.IndexOf("\r\n\r\n"u8);

    private static bool IsToken(string s) => s.Length > 0 && s.All(ch => char.IsAsciiLetterOrDigit(ch) || "!#$%&'*+-.^_`|~".Contains(ch));

    /// <summary>Forespørgselslinje og headere (uden den tomme linje). Null ved alt, der ikke er pæn HTTP/1.x.</summary>
    public static Request? Parse(ReadOnlySpan<byte> head)
    {
        var text = Encoding.Latin1.GetString(head);
        var lines = text.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !IsToken(first[0]) || first[2] is not ("HTTP/1.1" or "HTTP/1.0")) return null;
        var target = first[1];
        if (target.Length == 0 || target.Any(ch => ch <= ' ' || ch >= 0x7f)) return null;
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0 || line[0] is ' ' or '\t' || line.Contains('\r') || line.Contains('\n')) return null;   // ingen foldede linjer
            var i = line.IndexOf(':');
            if (i <= 0 || !IsToken(line[..i])) return null;
            var value = line[(i + 1)..].Trim(' ', '\t');
            if (value.Any(ch => ch < ' ' && ch != '\t' || ch == 0x7f)) return null;
            headers.Add(new(line[..i], value));
        }
        return new Request(first[0], target, first[2], headers);
    }

    private static string[] Hosts(int port) => [$"localhost:{port}", $"127.0.0.1:{port}", $"[::1]:{port}"];

    /// <summary>Kun de lokale navne med vores port. En anden Host betyder en side, der har fået sit navn til at pege på 127.0.0.1.</summary>
    public static bool HostAllowed(string? host, int port) =>
        host is not null && Hosts(port).Contains(host.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Origin fra en anden side (også en anden port på localhost) afvises; ingen Origin er en almindelig navigation.</summary>
    public static bool OriginAllowed(string origin, int port) =>
        Hosts(port).Any(h => string.Equals(origin.Trim(), "http://" + h, StringComparison.OrdinalIgnoreCase));

    public static Refusal? Refuse(Request r, int port)
    {
        if (r.Method == "CONNECT" || r.Target[0] != '/') return new(400, "Kun stier på Fleetdeck.");
        var hosts = r.All("Host").ToList();
        if (hosts.Count != 1 || !HostAllowed(hosts[0], port))
            return new(421, $"Åbn ESP Flasher på http://localhost:{port}/esp – andre adresser afvises.");
        if (r.All("Origin").Any(o => !OriginAllowed(o, port)))
            return new(403, "En anden side må ikke bruge Fleetdeck gennem agenten.");
        return null;
    }

    /// <summary>Headere, der aldrig sendes videre: forbindelsen selv og klientens egne påstande om proxyer.</summary>
    private static readonly HashSet<string> Drop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "TE", "Trailer", "Upgrade",
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Prefix", "X-Forwarded-Port", "X-Real-IP",
    };

    /// <summary>Forespørgslen til serveren: Host bliver serverens, browserens adresse står i X-Forwarded-Host (så serverens
    /// Origin-tjek og cookies passer til http://localhost), og forbindelsen lukkes efter svaret – undtagen en websocket.</summary>
    public static byte[] UpstreamHead(Request r, Uri target, int port)
    {
        var connection = r.All("Connection").SelectMany(v => v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
        var websocket = connection.Contains("upgrade", StringComparer.OrdinalIgnoreCase)
            && r.All("Upgrade").Any(v => v.Equals("websocket", StringComparison.OrdinalIgnoreCase));
        var hop = new HashSet<string>(connection, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.Append(r.Method).Append(' ').Append(target.AbsolutePath.TrimEnd('/')).Append(r.Target).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(target.Authority).Append("\r\n");
        foreach (var (k, v) in r.Headers)
            if (!Drop.Contains(k) && !hop.Contains(k)) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("X-Forwarded-Host: ").Append(r.All("Host").First().Trim()).Append("\r\n");
        sb.Append("X-Forwarded-Proto: http\r\n");
        sb.Append(websocket ? "Upgrade: websocket\r\nConnection: Upgrade\r\n" : "Connection: close\r\n");
        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static async Task Reply(Stream s, int status, string text, CancellationToken ct)
    {
        var reason = status switch { 400 => "Bad Request", 403 => "Forbidden", 421 => "Misdirected Request", 431 => "Request Header Fields Too Large", 502 => "Bad Gateway", _ => "Service Unavailable" };
        var body = Encoding.UTF8.GetBytes($"<!doctype html><html lang=\"da\"><meta charset=\"utf-8\"><title>Fleetdeck</title><body style=\"font-family:system-ui;margin:3em\"><h2>Fleetdeck på denne pc</h2><p>{text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")}</p></body></html>");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        try { await s.WriteAsync(head, ct); await s.WriteAsync(body, ct); await s.FlushAsync(ct); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
    }
}
