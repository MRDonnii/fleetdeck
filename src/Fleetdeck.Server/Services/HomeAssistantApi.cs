using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Home Assistants REST-API til strømkontakter med måler ("Sluk og tænd strømmen", når en genstart hænger).
/// Adressen står i indstillingen ha_url; adgangsnøglen (long-lived access token) gemmes krypteret og vises aldrig igen.
/// </summary>
public sealed class HomeAssistantApi(SettingsService s, IDataProtectionProvider dp, IHttpClientFactory http)
{
    public const string UrlSetting = "ha_url", TokenSetting = "ha_token", HangSetting = "power_hang_minutes";
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.HomeAssistant.Token");

    public sealed record State(string Entity, string Value, string? Unit, string? Name);

    public async Task<string> Url() => Normalize(await s.Get(UrlSetting)) ?? "";
    public async Task<bool> HasToken() => (await s.Get(TokenSetting)).Length > 0;
    public async Task<bool> Configured() => (await Url()).Length > 0 && await Token() is not null;
    public async Task<int> HangMinutes() => (int)Math.Clamp(await s.GetNum(HangSetting, PowerWatch.DefaultHangMinutes), 1, 120);

    /// <summary>http(s)://vært[:port] uden afsluttende / – ellers null.</summary>
    public static string? Normalize(string? url)
    {
        var u = (url ?? "").Trim().TrimEnd('/');
        if (u.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        return Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme is "http" or "https" && x.Host.Length > 0 && string.IsNullOrEmpty(x.Query) ? u : null;
    }

    /// <summary>Gemmer adressen; en tom nøgle lader den gamle stå, "-" sletter den.</summary>
    public async Task Save(string? url, string? token, int? hangMinutes)
    {
        if (url is not null)
        {
            var n = url.Trim().Length == 0 ? "" : Normalize(url) ?? throw new ArgumentException("Adressen skal være fx http://homeassistant.local:8123");
            await s.Set(UrlSetting, n);
        }
        if (token is "-") await s.Set(TokenSetting, "");
        else if (token is { Length: > 0 } t) await s.Set(TokenSetting, protector.Protect(t.Trim()));
        if (hangMinutes is int h) await s.Set(HangSetting, Math.Clamp(h, 1, 120).ToString());
    }

    private async Task<string?> Token()
    {
        var enc = await s.Get(TokenSetting);
        if (enc.Length == 0) return null;
        try { return protector.Unprotect(enc); } catch (CryptographicException) { return null; }
    }

    private async Task<HttpClient> Client()
    {
        var url = await Url();
        var token = await Token();
        if (url.Length == 0 || token is null) throw new InvalidOperationException("Home Assistant er ikke sat op (Indstillinger → Home Assistant-API).");
        var c = http.CreateClient("homeassistant");
        c.BaseAddress = new Uri(url + "/");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public async Task<State> Get(string entity, CancellationToken ct = default)
    {
        if (!HaEntity.Valid(entity)) throw new ArgumentException("Ugyldigt entitets-id");
        using var c = await Client();
        using var r = await c.GetAsync($"api/states/{entity}", ct);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException($"{entity} findes ikke i Home Assistant.");
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("Home Assistant afviste adgangsnøglen.");
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var attrs = root.TryGetProperty("attributes", out var a) ? a : default;
        string? Attr(string k) => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new(entity, root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "", Attr("unit_of_measurement"), Attr("friendly_name"));
    }

    /// <summary>Strøm i watt fra måleren – null, hvis den ikke kan læses.</summary>
    public async Task<double?> Watts(string? sensor, CancellationToken ct = default)
    {
        if (!HaEntity.IsSensor(sensor) || !await Configured()) return null;
        try { var st = await Get(sensor!, ct); return HaEntity.Watts(st.Value, st.Unit); }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException) { return null; }
    }

    /// <summary>switch.turn_on / switch.turn_off.</summary>
    public async Task Switch(string entity, bool on, CancellationToken ct = default)
    {
        if (!HaEntity.IsSwitch(entity)) throw new ArgumentException("Kun switch.…-entiteter kan slås til og fra.");
        using var c = await Client();
        using var body = new StringContent(JsonSerializer.Serialize(new { entity_id = entity }), Encoding.UTF8, "application/json");
        using var r = await c.PostAsync($"api/services/switch/{(on ? "turn_on" : "turn_off")}", body, ct);
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new InvalidOperationException("Home Assistant afviste adgangsnøglen.");
        r.EnsureSuccessStatusCode();
    }

    /// <summary>Forbindelsestest: "API running." fra /api/.</summary>
    public async Task<string> Test(CancellationToken ct = default)
    {
        using var c = await Client();
        using var r = await c.GetAsync("api/", ct);
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) return "Home Assistant afviste adgangsnøglen.";
        return r.IsSuccessStatusCode ? "Forbundet" : $"Fejl: HTTP {(int)r.StatusCode}";
    }
}
