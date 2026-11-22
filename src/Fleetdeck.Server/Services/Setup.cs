using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

/// <summary>Alt fra opsætningsguiden, sendt samlet til sidst (intet gemmes undervejs).</summary>
public sealed record SetupInput(
    string? Username, string? Password, string? Household, string? ServerUrl,
    string? Language, string? Keyboard, string? TimeZone,
    string? Notify = null, string? MqttHost = null, int? MqttPort = null, string? MqttUser = null, string? MqttPass = null, string? MqttPrefix = null);

public enum SetupStatus { Done, Closed, Invalid }
public sealed record SetupOutcome(SetupStatus Status, User? User = null, string? Error = null);

/// <summary>
/// Første opstart: opsætningsguiden opretter den første administrator og de grundlæggende indstillinger i ét hug.
/// Guiden er kun åben, så længe der ikke findes nogen brugere – derefter svarer den 404 (<see cref="SetupStatus.Closed"/>).
/// </summary>
public sealed class SetupService(AppDb db)
{
    // Én ad gangen: to samtidige forsøg må ikke begge nå at se "ingen brugere"
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<bool> IsOpen() => !await db.Users.AnyAsync();

    /// <summary>Ret adressen til (fjern / til sidst, kræv http/https). Null = ugyldig.</summary>
    public static string? NormalizeUrl(string? url)
    {
        var t = (url ?? "").Trim().TrimEnd('/');
        if (t.Length > 0 && !t.Contains("://")) t = "http://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Host.Length > 0
            && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment) ? t : null;
    }

    /// <summary>Fejl i det indsendte (dansk tekst til brugeren), ellers null.</summary>
    public static string? Validate(SetupInput i)
    {
        if ((i.Username ?? "").Trim().Length < 2) return "Brugernavnet skal være mindst 2 tegn";
        if ((i.Username ?? "").Trim().Length > 64) return "Brugernavnet må højst være 64 tegn";
        if ((i.Password ?? "").Length < 8) return "Adgangskoden skal være mindst 8 tegn";
        if ((i.Household ?? "").Trim().Length > 64) return "Husstandens navn må højst være 64 tegn";
        if (NormalizeUrl(i.ServerUrl) is null) return "Skriv serverens adresse, fx http://192.168.1.10:8585";
        if (InstallLocale.FindLanguage(i.Language) is null) return "Vælg et sprog";
        if (InstallLocale.FindKeyboard(i.Keyboard) is null) return "Vælg et tastaturlayout";
        if (!InstallLocale.ValidTimeZone(i.TimeZone)) return "Vælg en tidszone";
        if (i.Notify is "mqtt")
        {
            if ((i.MqttHost ?? "").Trim().Length == 0) return "Skriv MQTT-brokerens adresse (eller vælg ingen notifikationer)";
            if (i.MqttPort is < 1 or > 65535) return "MQTT-porten skal være mellem 1 og 65535";
        }
        else if (i.Notify is not (null or "" or "none")) return "Ukendt valg for notifikationer";
        return null;
    }

    /// <summary>Opret administratoren og gem indstillingerne – kun hvis der stadig ingen brugere er.</summary>
    public async Task<SetupOutcome> Complete(SetupInput i)
    {
        await Gate.WaitAsync();
        try
        {
            if (!await IsOpen()) return new(SetupStatus.Closed);
            if (Validate(i) is { } err) return new(SetupStatus.Invalid, Error: err);

            var u = new User { Username = i.Username!.Trim(), PasswordHash = Crypto.HashPassword(i.Password!), Role = Roles.Admin };
            db.Users.Add(u);
            var values = new Dictionary<string, string>
            {
                ["server_url"] = NormalizeUrl(i.ServerUrl)!,
                [InstallLocale.KLanguage] = InstallLocale.FindLanguage(i.Language)!.Id,
                [InstallLocale.KKeyboard] = InstallLocale.FindKeyboard(i.Keyboard)!.Id,
                [InstallLocale.KTimeZone] = i.TimeZone!.Trim(),
            };
            if ((i.Household ?? "").Trim() is { Length: > 0 } h) values[IpadSupervision.KOrg] = SupervisionIdentity.Clean(h);
            if (i.Notify is "mqtt")
            {
                values["mqtt_host"] = i.MqttHost!.Trim();
                values["mqtt_port"] = (i.MqttPort ?? 1883).ToString();
                values["mqtt_user"] = (i.MqttUser ?? "").Trim();
                if ((i.MqttPass ?? "").Length > 0) values["mqtt_pass"] = i.MqttPass!;
                values["mqtt_prefix"] = (i.MqttPrefix ?? "").Trim() is { Length: > 0 } p ? p : "homeassistant";
            }
            foreach (var (k, v) in values)
            {
                var row = await db.Settings.FindAsync(k);
                if (row is null) db.Settings.Add(new Setting { Key = k, Value = v }); else row.Value = v;
            }
            db.Audit.Add(AuditService.Entry(u.Username, "setup", "Første administrator oprettet med opsætningsguiden"));
            await db.SaveChangesAsync();   // alt eller intet
            return new(SetupStatus.Done, u);
        }
        finally { Gate.Release(); }
    }
}
