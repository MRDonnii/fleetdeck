using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Intel AMT pr. maskine: adgangskoden gemmes krypteret (DataProtection), og strømmen styres direkte via AMT.</summary>
public sealed class AmtService(IDataProtectionProvider dp)
{
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.Amt.Password");

    /// <summary>
    /// AMT-adgangskoden står i jobbets parametre, til agenten har hentet den. Er jobbet afsluttet på anden måde end med et
    /// resultat (annulleret, udløbet, opgivet), fjernes den her – den ligger allerede krypteret på maskinen.
    /// </summary>
    public static Task<int> ScrubFinishedJobs(AppDb db) =>
        db.Jobs.Where(j => (j.Type == JobTypes.AmtActivate || j.Type == JobTypes.AmtDeactivate) && !JobStatus.Active.Contains(j.Status) && j.ParamsJson != "{}")
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ParamsJson, "{}"));

    public void SetPassword(Machine m, string? pw) => m.AmtPassword = pw is null ? null : protector.Protect(pw);

    public string? Password(Machine m)
    {
        if (string.IsNullOrEmpty(m.AmtPassword)) return null;
        try { return protector.Unprotect(m.AmtPassword); } catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public static AmtInfo? Info(Machine m) => Json.Des<HealthReport>(m.HealthJson).Amt;

    /// <summary>AMT's egen adresse, ellers pc'ens (AMT deler normalt IP med Windows via DHCP).</summary>
    public static string? Ip(Machine m) => Info(m)?.AmtIp is { Length: > 0 } ip && ip != "0.0.0.0" ? ip : m.Ip;

    public async Task<(bool Ok, string Message)> Power(Machine m, int state, CancellationToken ct = default)
    {
        if (Password(m) is not { } pw) return (false, $"Intel AMT er ikke sat op på {m.Label} fra Fleetdeck");
        if (Ip(m) is not { } ip) return (false, $"{m.Label} har ingen kendt IP-adresse");
        try
        {
            var rv = await AmtClient.SetPower(ip, pw, state, ct);
            return rv == 0 ? (true, "OK") : (false, rv == 2 ? "AMT afviste: maskinen er allerede i den tilstand" : $"AMT svarede med kode {rv}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or IOException)
        {
            return (false, $"Kunne ikke nå Intel AMT på {ip}: {e.Message}");
        }
    }

    public async Task<object> Status(Machine m, CancellationToken ct = default)
    {
        var info = Info(m);
        int? power = null; string? error = null;
        if (Password(m) is { } pw && Ip(m) is { } ip)
        {
            try { power = await AmtClient.PowerState(ip, pw, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or IOException) { error = e.Message; }
        }
        return new
        {
            present = info?.Present ?? false, mode = info?.Mode, version = info?.Version, activated = info?.Activated ?? false,
            managed = !string.IsNullOrEmpty(m.AmtPassword), ip = Ip(m), linkUp = info?.LinkUp, infoError = info?.Error, @checked = info?.Checked,
            power, powerName = AmtClient.StateName(power), error,
        };
    }
}
