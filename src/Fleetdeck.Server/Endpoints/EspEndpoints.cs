using System.Security.Claims;
using Fleetdeck.Server.Data;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Server.Endpoints;

public sealed record EspWifiInput(string Ssid, string? Password);
/// <summary>Token: ny nøgle (tom/null = behold den gemte); ClearToken: fjern den.</summary>
public sealed record EspSettingsInput(string CatalogUrl, string? Token = null, bool ClearToken = false);
public sealed record EspNameInput(string Host);

/// <summary>
/// ESP-flash: firmwarekataloget og filerne til ESP Web Tools i browseren, Wi-Fi til nye enheder (krypteret) og https-hjælp.
/// Flashen sker i browseren over USB (Web Serial), så siden skal åbnes over https.
/// </summary>
public static class EspEndpoints
{
    public static void MapEspEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/esp").RequireAuthorization("user");
        var adm = app.MapGroup("/api/esp").RequireAuthorization("admin");

        api.MapGet("", async (HttpContext ctx, EspFirmwareService fw, EspWifi wifi, EspCatalogSecret secret, TlsInfo tls, ClaimsPrincipal u) =>
        {
            var admin = u.IsInRole(Roles.Admin);
            var (catalogUrl, hasToken) = await secret.Public(admin);   // aldrig selve nøglen
            return Results.Ok(new
            {
                firmware = fw.Mirror.Status,
                catalogUrl,
                hasToken,
                wifi = new { ssid = admin ? await wifi.Ssid() : null, has = (await wifi.Ssid()).Length > 0, hasPassword = await wifi.HasPassword() },
                https = Https(ctx, tls),
                // ESP Flasher gennem agenten på pc'en: http://localhost:porten er sikker for Chrome og Edge uden certifikat
                local = new { port = Fleetdeck.Shared.LocalWeb.ServerPort() },
            });
        });

        // ESP Web Tools henter manifest.json og delene relativt til manifestets adresse (med sessionens cookie)
        api.MapGet("/fw/{**path}", (string path, EspFirmwareService fw, HttpContext ctx) =>
        {
            if (fw.Mirror.Resolve(path) is not { } file) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.File(file, file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "application/octet-stream");
        });

        api.MapGet("/cert", (TlsInfo tls) => tls.Certificate is { } c
            ? Results.File(System.Text.Encoding.ASCII.GetBytes(c.ExportCertificatePem() + "\n"), "application/x-x509-ca-cert", "fleetdeck.crt")
            : Results.NotFound());

        adm.MapPost("/check", async (EspFirmwareService fw, CancellationToken ct) =>
        {
            await fw.Refresh();
            var updated = await fw.Mirror.Check(ct);
            return Results.Ok(new { updated, firmware = fw.Mirror.Status });
        });

        adm.MapPut("/settings", async (EspSettingsInput i, SettingsService s, EspCatalogSecret secret, EspFirmwareService fw, AuditService audit, ClaimsPrincipal u) =>
        {
            if (EspCatalog.NormalizeUrl(i.CatalogUrl) is not { } url)
                return Results.Problem("Adressen skal være en http(s)-adresse til mappen med catalog.json, fx https://github.com/EJER/REPO/tree/main/mappe", statusCode: 400);
            var tokenNote = "";
            if (i.ClearToken) { await secret.Clear(); tokenNote = " · nøglen er fjernet"; }
            else if (i.Token is { Length: > 0 } t)
            {
                if (await secret.Save(t) is { } err) return Results.Problem(err, statusCode: 400);
                tokenNote = " · ny nøgle gemt";   // aldrig selve nøglen i loggen
            }
            await s.Set(EspCatalog.UrlSetting, url);
            await fw.Refresh();
            await audit.Log(u.UserName(), "settings.esp_catalog", (url.Length > 0 ? url : "intet katalog") + tokenNote);
            return Results.Ok(new { catalogUrl = url, hasToken = await secret.HasToken() });
        });

        adm.MapPut("/wifi", async (EspWifiInput i, EspWifi wifi, AuditService audit, ClaimsPrincipal u) =>
        {
            // tom kode = behold den gemte; et åbent net gemmes ved at slette Wi-Fi og skrive navnet uden kode
            var keep = string.IsNullOrEmpty(i.Password) && await wifi.HasPassword();
            if (EspWifi.Validate(i.Ssid, keep ? null : i.Password) is { } err) return Results.Problem(err, statusCode: 400);
            await wifi.Save(i.Ssid, keep ? null : i.Password ?? "");
            await audit.Log(u.UserName(), "settings.esp_wifi", $"Wi-Fi til ESP-enheder: {i.Ssid.Trim()}{(keep ? " (koden uændret)" : "")}");
            return Results.Ok(new { ssid = i.Ssid.Trim(), hasPassword = await wifi.HasPassword() });
        });

        adm.MapDelete("/wifi", async (EspWifi wifi, AuditService audit, ClaimsPrincipal u) =>
        {
            await wifi.Clear();
            await audit.Log(u.UserName(), "settings.esp_wifi", "Wi-Fi til ESP-enheder er slettet");
            return Results.Ok();
        });

        // Koden går kun til browseren, når den skal sendes til en enhed over USB (Improv) – og det logges
        adm.MapPost("/wifi/use", async (EspWifi wifi, AuditService audit, ClaimsPrincipal u) =>
        {
            var ssid = await wifi.Ssid();
            if (ssid.Length == 0) return Results.Problem("Der er ikke gemt noget Wi-Fi", statusCode: 400);
            if (await wifi.Password() is not { } pw) return Results.Problem("Wi-Fi-koden kan ikke læses (nøglerne er skiftet) – gem den igen", statusCode: 400);
            await audit.Log(u.UserName(), "esp.wifi_used", $"Wi-Fi {ssid} er sendt til en ESP-enhed over USB");
            return Results.Ok(new { ssid, password = pw });
        });

        adm.MapPost("/https/name", async (EspNameInput i, TlsInfo tls, AuditService audit, ClaimsPrincipal u) =>
        {
            var host = (i.Host ?? "").Trim().TrimStart('[').TrimEnd(']');
            if (tls.AddName(host) is { } err) return Results.Problem(err, statusCode: 400);
            await audit.Log(u.UserName(), "settings.tls_name", $"https-certifikatet dækker nu også {host} (samme nøgle)");
            return Results.Ok();
        });
    }

    /// <summary>Hvordan siden kommer på https: port, om certifikatet dækker adressen, og fingeraftrykket til at tjekke filen.</summary>
    public static object Https(HttpContext ctx, TlsInfo tls)
    {
        var host = ctx.Request.Host.Host;
        if (tls.Certificate is not { } cert)
            return new { enabled = false, secure = ctx.Request.IsHttps, error = tls.Error, host };
        var names = TlsInfo.Names(cert);
        var h = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return new
        {
            enabled = true, secure = ctx.Request.IsHttps, host, port = tls.HttpsPort,
            url = $"https://{h}:{tls.HttpsPort}/esp",
            names, covers = TlsInfo.Covers(names, host), selfSigned = tls.SelfSigned, canAddName = tls.OwnDir is not null,
            fingerprint = string.Join(':', Convert.ToHexString(SHA256.HashData(cert.RawData)).Chunk(2).Select(c => new string(c))),
            validTo = cert.NotAfter.ToUniversalTime(),
        };
    }
}
