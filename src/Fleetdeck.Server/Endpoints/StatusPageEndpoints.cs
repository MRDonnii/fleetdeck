using System.Net;
using System.Security.Claims;
using System.Text;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed class StatusPageConf
{
    public bool Enabled { get; set; }
    public string Title { get; set; } = "Status";
    public string Token { get; set; } = "";
    public List<int> Checks { get; set; } = [];
    public bool ShowMessages { get; set; }
}

/// <summary>Statusside (à la Uptime Kuma): en skrivebeskyttet side med udvalgte tjek, som kan deles med et hemmeligt link.</summary>
public static class StatusPageEndpoints
{
    private const string Key = "status_page";

    private static async Task<StatusPageConf> Load(SettingsService s) => Json.Des<StatusPageConf>(await s.Get(Key)) ?? new StatusPageConf();

    public static void MapStatusPageEndpoints(this WebApplication app)
    {
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        adm.MapGet("/status-page", async (SettingsService s) => Results.Ok(await Load(s)));

        adm.MapPut("/status-page", async (StatusPageConf i, SettingsService s, AuditService audit, ClaimsPrincipal u) =>
        {
            var cur = await Load(s);
            cur.Enabled = i.Enabled; cur.ShowMessages = i.ShowMessages;
            cur.Title = string.IsNullOrWhiteSpace(i.Title) ? "Status" : i.Title.Trim()[..Math.Min(80, i.Title.Trim().Length)];
            cur.Checks = i.Checks.Distinct().Take(100).ToList();
            if (cur.Token.Length == 0) cur.Token = Crypto.Token(15);
            await s.Set(Key, Json.Ser(cur));
            await audit.Log(u.UserName(), "status_page.update", cur.Enabled ? "slået til" : "slået fra");
            return Results.Ok(cur);
        });

        adm.MapPost("/status-page/new-link", async (SettingsService s, AuditService audit, ClaimsPrincipal u) =>
        {
            var cur = await Load(s);
            cur.Token = Crypto.Token(15);
            await s.Set(Key, Json.Ser(cur));
            await audit.Log(u.UserName(), "status_page.new_link");
            return Results.Ok(cur);
        });

        app.MapGet("/status/{token}", async (string token, SettingsService s, AppDb db) =>
        {
            var conf = await Load(s);
            if (!conf.Enabled || conf.Token.Length < 10 || !CryptographicEquals(token, conf.Token)) return Results.NotFound();
            return Results.Content(await Render(conf, db), "text/html; charset=utf-8");
        }).AllowAnonymous();
    }

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task<string> Render(StatusPageConf conf, AppDb db)
    {
        var checks = await db.Checks.AsNoTracking().Where(c => conf.Checks.Contains(c.Id)).OrderBy(c => c.Group).ThenBy(c => c.Name).ToListAsync();
        var ids = checks.Select(c => c.Id).ToList();
        var since = DateTime.UtcNow.AddDays(-30); var since24 = DateTime.UtcNow.AddHours(-24);
        var stats = await db.CheckBeats.AsNoTracking().Where(b => ids.Contains(b.CheckId) && b.Ts >= since).GroupBy(b => b.CheckId)
            .Select(g => new { g.Key, N = g.Count(), Up = g.Count(b => b.Up), N24 = g.Count(b => b.Ts >= since24), Up24 = g.Count(b => b.Ts >= since24 && b.Up) })
            .ToDictionaryAsync(x => x.Key);
        var beats = await db.CheckBeats.AsNoTracking().Where(b => ids.Contains(b.CheckId) && b.Ts >= DateTime.UtcNow.AddDays(-2))
            .OrderByDescending(b => b.Id).Select(b => new { b.CheckId, b.Up, b.Ts }).Take(ids.Count * 60 + 100).ToListAsync();
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        static string Pct(int up, int n) => n == 0 ? "–" : $"{100.0 * up / n:0.##} %";
        bool Down(ServiceCheck c) => c.Enabled && c.State == CheckStates.Down;
        var anyDown = checks.Any(Down);
        var sb = new StringBuilder();
        sb.Append($$$"""
            <!doctype html><html lang="da"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="refresh" content="60"><title>{{{H(conf.Title)}}}</title><meta name="robots" content="noindex">
            <style>
            :root{color-scheme:light dark;--bg:#0b0f17;--card:#121827;--line:#1f2738;--fg:#e6e9ef;--dim:#8a93a6;--up:#22c55e;--down:#ef4444;--pause:#64748b}
            @media (prefers-color-scheme:light){:root{--bg:#f5f7fb;--card:#fff;--line:#e3e8f0;--fg:#111827;--dim:#5b6475}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.45 Inter,"Segoe UI",system-ui,sans-serif}
            main{max-width:860px;margin:0 auto;padding:28px 18px 40px}h1{font-size:24px;margin:0 0 14px}
            .banner{padding:16px 18px;border-radius:14px;font-weight:700;margin-bottom:20px;color:#fff;background:{(anyDown ? "var(--down)" : "var(--up)")}}
            h2{font-size:13px;text-transform:uppercase;letter-spacing:.05em;color:var(--dim);margin:22px 0 8px}
            .card{background:var(--card);border:1px solid var(--line);border-radius:14px;overflow:hidden}
            .row{padding:12px 16px;border-top:1px solid var(--line)}.row:first-child{border-top:0}
            .top{display:flex;justify-content:space-between;gap:12px;align-items:center}.name{font-weight:600}
            .dot{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:8px}.pct{color:var(--dim);font-size:13px;white-space:nowrap}
            .bar{display:flex;gap:2px;margin-top:8px}.bar i{flex:1;height:22px;border-radius:3px;background:var(--line)}
            .msg{color:var(--dim);font-size:13px;margin-top:4px}footer{color:var(--dim);font-size:12px;margin-top:22px;text-align:center}
            </style></head><body><main><h1>{{{H(conf.Title)}}}</h1>
            <div class="banner">{(anyDown ? $"{{{checks.Count(Down)}}} tjeneste(r) har problemer" : "Alle tjenester kører")}</div>
            """);
        foreach (var g in checks.GroupBy(c => c.Group ?? ""))
        {
            if (g.Key.Length > 0) sb.Append($"<h2>{H(g.Key)}</h2>"); else sb.Append("<h2>Tjenester</h2>");
            sb.Append("<div class=\"card\">");
            foreach (var c in g)
            {
                var st = stats.GetValueOrDefault(c.Id);
                var color = !c.Enabled ? "var(--pause)" : c.State == CheckStates.Down ? "var(--down)" : c.State == CheckStates.Up ? "var(--up)" : "var(--pause)";
                var bars = beats.Where(b => b.CheckId == c.Id).Take(60).Reverse().ToList();
                sb.Append($"<div class=\"row\"><div class=\"top\"><span class=\"name\"><span class=\"dot\" style=\"background:{color}\"></span>{H(c.Name)}</span>");
                sb.Append($"<span class=\"pct\">24 t: {(st is null ? "–" : Pct(st.Up24, st.N24))} · 30 d: {(st is null ? "–" : Pct(st.Up, st.N))}</span></div>");
                sb.Append("<div class=\"bar\">");
                for (var k = bars.Count; k < 60; k++) sb.Append("<i></i>");
                foreach (var b in bars) sb.Append($"<i style=\"background:{(b.Up ? "var(--up)" : "var(--down)")}\" title=\"{b.Ts.ToLocalTime():dd/MM HH:mm}\"></i>");
                sb.Append("</div>");
                if (conf.ShowMessages && c.State == CheckStates.Down && c.LastMessage is { Length: > 0 } lm) sb.Append($"<div class=\"msg\">{H(lm)}</div>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }
        if (checks.Count == 0) sb.Append("<p>Ingen tjenester er valgt til siden.</p>");
        sb.Append($"<footer>Opdateret {DateTime.Now:dd/MM HH:mm} · opdaterer sig selv hvert minut</footer></main></body></html>");
        return sb.ToString();
    }
}
