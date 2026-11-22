using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Fleetdeck.Server.Services;

/// <summary>
/// CSRF-værn for cookie-godkendte kald: usikre metoder (POST/PUT/PATCH/DELETE) til /api skal have headeren
/// <c>X-Fleetdeck: 1</c>, som brugerfladen altid sender. En anden side (også en anden app på samme vært, som SameSite=Lax
/// ikke beskytter mod) kan ikke sætte en egen header uden CORS, og Fleetdeck svarer ikke på CORS. Virker uændret i
/// Kontrolrums iframe, fordi siden dér kalder sin egen adresse. Kald uden session-cookie (agenter, scripts med nøgle)
/// berøres ikke – undtagen login og kontoen, hvor headeren altid kræves.
/// </summary>
public static class Csrf
{
    public const string Header = "X-Fleetdeck", SessionCookie = "ws_session";

    public static bool Rejects(HttpRequest r)
    {
        if (HttpMethods.IsGet(r.Method) || HttpMethods.IsHead(r.Method) || HttpMethods.IsOptions(r.Method) || HttpMethods.IsTrace(r.Method)) return false;
        if (!r.Path.StartsWithSegments("/api")) return false;
        if (r.Headers[Header] == "1") return false;
        // Selve password-login fra en server (fx Kontrolrums auto-login) har ingen Origin. En browser sender altid Origin på en
        // POST fra en anden side, så login uden header tillades kun uden Origin eller fra Fleetdecks egen adresse.
        if (r.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) && !r.Cookies.ContainsKey(SessionCookie)
            && (r.Headers.Origin.Count == 0 || SameHost(r.Headers.Origin.ToString(), r.Host.Host))) return false;
        return r.Cookies.ContainsKey(SessionCookie) || r.Path.StartsWithSegments("/api/auth") || r.Path.StartsWithSegments("/api/account");
    }

    private static bool SameHost(string origin, string host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && string.Equals(u.Host, host, StringComparison.OrdinalIgnoreCase);

    public static async Task Middleware(HttpContext ctx, RequestDelegate next)
    {
        if (!Rejects(ctx.Request)) { await next(ctx); return; }
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { title = "Afvist", status = 403, detail = "Forespørgslen mangler Fleetdecks sikkerheds-header (CSRF-værn)." });
    }
}

/// <summary>
/// Betroede proxyer: kun fra dem læses klientens IP i <c>X-Forwarded-For</c> (bruges til rate limits og audit). Standard er
/// loopback og Dockers interne net (172.16.0.0/12); fx Kontrolrum eller cloudflared på en anden maskine tilføjes i Indstillinger.
/// </summary>
public static class TrustedProxies
{
    public const string SettingKey = "trusted_proxies";
    public static readonly string Defaults = Environment.GetEnvironmentVariable("TRUSTED_PROXIES") is { Length: > 0 } env ? env : "127.0.0.0/8, ::1, 172.16.0.0/12";

    public sealed record Snapshot(string Text, IReadOnlyList<System.Net.IPNetwork> Networks);
    private static volatile Snapshot current = new(Defaults, Parse(Defaults, out _)!);
    public static Snapshot Current => current;

    /// <summary>Sæt listen (tom = standard). Returnerer en fejltekst ved ugyldige adresser – så ændres intet.</summary>
    public static string? Set(string? text)
    {
        var t = string.IsNullOrWhiteSpace(text) ? Defaults : text.Trim();
        var nets = Parse(t, out var err);
        if (nets is null) return err;
        current = new Snapshot(t, nets);
        return null;
    }

    /// <summary>IP-adresser og net (CIDR) adskilt af komma, semikolon, mellemrum eller linjeskift.</summary>
    public static List<System.Net.IPNetwork>? Parse(string text, out string? error)
    {
        error = null;
        var res = new List<System.Net.IPNetwork>();
        foreach (var part in text.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (System.Net.IPNetwork.TryParse(part, out var net)) res.Add(net);
            else if (IPAddress.TryParse(part, out var ip)) res.Add(new System.Net.IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else { error = $"Ugyldig adresse eller net: {part}"; return null; }
        }
        return res;
    }

    public static bool IsTrusted(IPAddress? ip, Snapshot? s = null)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return (s ?? current).Networks.Any(n => n.Contains(ip));
    }
}

/// <summary>
/// Forwarded headers i to trin:
/// 1) <c>X-Forwarded-Proto/-Host/-Prefix</c> som hidtil fra alle (understien bag Kontrolrum; filteret for den offentlige
///    MDM-adresse kører før og afviser dem dér).
/// 2) <c>X-Forwarded-For</c> kun fra betroede proxyer – og der gås kun tilbage gennem kæden, så længe hvert led er betroet.
///    Listen kan ændres mens serveren kører.
/// </summary>
public static class ProxyHeaders
{
    private sealed record Stage(TrustedProxies.Snapshot Snapshot, ForwardedHeadersMiddleware? Middleware);

    public static IApplicationBuilder UseProxyHeaders(this IApplicationBuilder app)
    {
        var lf = app.ApplicationServices.GetRequiredService<ILoggerFactory>();
        return app.Use(next => Create(next, lf));
    }

    public static RequestDelegate Create(RequestDelegate next, ILoggerFactory lf, Func<TrustedProxies.Snapshot>? source = null)
    {
        source ??= () => TrustedProxies.Current;
        Stage? stage = null;
        RequestDelegate forwardedFor = ctx =>
        {
            var s = source();
            var st = stage;
            if (st is null || !ReferenceEquals(st.Snapshot, s))
            {
                // Tom liste ville betyde "stol på alle" i ASP.NET – derfor springes trinnet helt over
                st = new Stage(s, s.Networks.Count == 0 ? null : new ForwardedHeadersMiddleware(next, lf, Options.Create(ForOptions(s))));
                stage = st;
            }
            return st.Middleware is null ? next(ctx) : st.Middleware.Invoke(ctx);
        };
        var first = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedPrefix };
        first.KnownIPNetworks.Clear(); first.KnownProxies.Clear();   // tomme lister = alle (som før)
        return new ForwardedHeadersMiddleware(forwardedFor, lf, Options.Create(first)).Invoke;
    }

    private static ForwardedHeadersOptions ForOptions(TrustedProxies.Snapshot s)
    {
        var o = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = null };
        o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
        foreach (var n in s.Networks) o.KnownIPNetworks.Add(n);
        return o;
    }
}
