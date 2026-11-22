using System.Text;

namespace Fleetdeck.Server.Services;

/// <summary>Et sprog, som netværksinstallationer kan sætte op (Windows-sprogkode, Ubuntu-locale og standard-tastatur).</summary>
public sealed record InstallLanguage(string Id, string Name, string Linux, string Keyboard);

/// <summary>Et tastaturlayout: xkb-layout (+ variant) til Linux og "sprog-id:layout-id" (InputLocale) til Windows.</summary>
public sealed record InstallKeyboard(string Id, string Name, string Xkb, string? XkbVariant, string Windows);

/// <summary>
/// Sprog, tastatur og tidszone til maskiner, Fleetdeck installerer (Windows-svarfil, Ubuntu autoinstall, iPad-overvågning).
/// Gemmes i indstillingerne <see cref="KLanguage"/>, <see cref="KKeyboard"/> og <see cref="KTimeZone"/> (IANA-navn).
/// Installationer fra før indstillingerne fandtes har fået de tidligere faste værdier (da-DK, dk, Europe/Copenhagen) ved migreringen.
/// </summary>
public sealed record InstallLocale(InstallLanguage Language, InstallKeyboard Keyboard, string TimeZone)
{
    public const string KLanguage = "install_language", KKeyboard = "install_keyboard", KTimeZone = "install_timezone";

    /// <summary>De værdier, Fleetdeck brugte, før de kunne vælges (og som eksisterende installationer får ved migreringen).</summary>
    public const string LegacyLanguage = "da-DK", LegacyKeyboard = "dk", LegacyTimeZone = "Europe/Copenhagen";

    public static readonly InstallLanguage[] Languages =
    [
        new("da-DK", "Dansk", "da_DK.UTF-8", "dk"),
        new("en-US", "English (United States)", "en_US.UTF-8", "us"),
        new("en-GB", "English (United Kingdom)", "en_GB.UTF-8", "gb"),
        new("sv-SE", "Svenska", "sv_SE.UTF-8", "se"),
        new("nb-NO", "Norsk (bokmål)", "nb_NO.UTF-8", "no"),
        new("fi-FI", "Suomi", "fi_FI.UTF-8", "fi"),
        new("de-DE", "Deutsch", "de_DE.UTF-8", "de"),
        new("nl-NL", "Nederlands", "nl_NL.UTF-8", "us-intl"),
        new("fr-FR", "Français", "fr_FR.UTF-8", "fr"),
        new("es-ES", "Español", "es_ES.UTF-8", "es"),
        new("it-IT", "Italiano", "it_IT.UTF-8", "it"),
        new("pl-PL", "Polski", "pl_PL.UTF-8", "pl"),
        new("pt-PT", "Português", "pt_PT.UTF-8", "pt"),
    ];

    public static readonly InstallKeyboard[] Keyboards =
    [
        new("dk", "Dansk", "dk", null, "0406:00000406"),
        new("us", "Amerikansk (US)", "us", null, "0409:00000409"),
        new("us-intl", "Amerikansk international", "us", "intl", "0409:00020409"),
        new("gb", "Britisk (UK)", "gb", null, "0809:00000809"),
        new("se", "Svensk", "se", null, "041d:0000041d"),
        new("no", "Norsk", "no", null, "0414:00000414"),
        new("fi", "Finsk", "fi", null, "040b:0000040b"),
        new("de", "Tysk", "de", null, "0407:00000407"),
        new("ch", "Schweizisk (tysk)", "ch", null, "0807:00000807"),
        new("fr", "Fransk (AZERTY)", "fr", null, "040c:0000040c"),
        new("be", "Belgisk (AZERTY)", "be", null, "080c:0000080c"),
        new("es", "Spansk", "es", null, "0c0a:0000040a"),
        new("it", "Italiensk", "it", null, "0410:00000410"),
        new("pl", "Polsk (programmør)", "pl", null, "0415:00000415"),
        new("pt", "Portugisisk", "pt", null, "0816:00000816"),
    ];

    // Hvis ICU mangler (TryConvertIanaIdToWindowsId fejler), dækker denne tabel de almindelige zoner
    private static readonly Dictionary<string, string> WinZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Copenhagen"] = "Romance Standard Time", ["Europe/Paris"] = "Romance Standard Time", ["Europe/Brussels"] = "Romance Standard Time",
        ["Europe/Madrid"] = "Romance Standard Time", ["Europe/Stockholm"] = "W. Europe Standard Time", ["Europe/Oslo"] = "W. Europe Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time", ["Europe/Amsterdam"] = "W. Europe Standard Time", ["Europe/Rome"] = "W. Europe Standard Time",
        ["Europe/Zurich"] = "W. Europe Standard Time", ["Europe/Vienna"] = "W. Europe Standard Time", ["Europe/Warsaw"] = "Central European Standard Time",
        ["Europe/Helsinki"] = "FLE Standard Time", ["Europe/London"] = "GMT Standard Time", ["Europe/Lisbon"] = "GMT Standard Time",
        ["Atlantic/Reykjavik"] = "Greenwich Standard Time", ["America/New_York"] = "Eastern Standard Time", ["America/Chicago"] = "Central Standard Time",
        ["America/Denver"] = "Mountain Standard Time", ["America/Los_Angeles"] = "Pacific Standard Time", ["UTC"] = "UTC", ["Etc/UTC"] = "UTC",
    };

    public static InstallLanguage? FindLanguage(string? id) =>
        Languages.FirstOrDefault(l => string.Equals(l.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
    public static InstallKeyboard? FindKeyboard(string? id) =>
        Keyboards.FirstOrDefault(k => string.Equals(k.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Gyldig IANA-tidszone (fx "Europe/Stockholm"), som både Linux og Windows kan bruge.</summary>
    public static bool ValidTimeZone(string? tz) => tz is { Length: > 0 and <= 64 } && WindowsZoneOf(tz.Trim()) is not null;

    public static string? WindowsZoneOf(string iana)
    {
        if (WinZones.TryGetValue(iana, out var w)) return w;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var win)) return win;
        return null;
    }

    /// <summary>Serverens egen tidszone som IANA-navn (bruges som forslag på en ny installation), ellers "UTC".</summary>
    public static string ServerTimeZone()
    {
        var id = Environment.GetEnvironmentVariable("TZ") is { Length: > 0 } tz && ValidTimeZone(tz) ? tz : TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) id = iana;
        return ValidTimeZone(id) ? id : "UTC";
    }

    /// <summary>Sprog-forslag ud fra fx browserens "sv-SE" eller "sv" (ellers en-US).</summary>
    public static InstallLanguage GuessLanguage(string? tag)
    {
        if (FindLanguage(tag) is { } l) return l;
        var two = (tag ?? "").Split('-', '_')[0];
        return Languages.FirstOrDefault(x => x.Id.StartsWith(two + "-", StringComparison.OrdinalIgnoreCase) && two.Length == 2) ?? FindLanguage("en-US")!;
    }

    /// <summary>Byg ud fra de gemte værdier. Tomme/ukendte værdier: en-US, sprogets tastatur og serverens tidszone.</summary>
    public static InstallLocale From(string? language, string? keyboard, string? timeZone)
    {
        var lang = FindLanguage(language) ?? FindLanguage("en-US")!;
        var kb = FindKeyboard(keyboard) ?? FindKeyboard(lang.Keyboard)!;
        var tz = ValidTimeZone(timeZone) ? timeZone!.Trim() : ServerTimeZone();
        return new(lang, kb, tz);
    }

    public static async Task<InstallLocale> Load(SettingsService s) =>
        From(await s.Get(KLanguage), await s.Get(KKeyboard), await s.Get(KTimeZone));

    // ---------------------------------------------------------------- hvad målene skal have
    public string WindowsLanguage => Language.Id;
    public string WindowsInputLocale => Keyboard.Windows;
    public string WindowsTimeZone => WindowsZoneOf(TimeZone) ?? "UTC";

    /// <summary>iPad (cfgutil prepare): sprog "da" og region "DK" ud fra "da-DK".</summary>
    public (string Language, string Region) Apple => (Language.Id.Split('-')[0], Language.Id.Split('-')[1]);

    /// <summary>Ubuntu autoinstall: locale, keyboard og timezone (YAML, to mellemrum ind under "autoinstall:").</summary>
    public string UbuntuYaml()
    {
        var sb = new StringBuilder();
        sb.Append($"  locale: {Language.Linux}\n  keyboard:\n    layout: {Keyboard.Xkb}\n");
        if (Keyboard.XkbVariant is { } v) sb.Append($"    variant: {v}\n");
        sb.Append($"  timezone: {TimeZone}\n");
        return sb.ToString();
    }

    /// <summary>
    /// PXE\deploy\defaults.cmd-linjer: WinPE-menuen (provision/pxe/menu.cmd) sætter dem ind i svarfilen
    /// (__LANG__, __INPUTLOCALE__, __TIMEZONE__ i USB\autounattend-pxe.xml).
    /// </summary>
    public string WinPeCmd() =>
        $"set \"DEF_LANG={WindowsLanguage}\"\r\nset \"DEF_INPUTLOCALE={WindowsInputLocale}\"\r\nset \"DEF_TIMEZONE={WindowsTimeZone}\"\r\n";

    /// <summary>locale.json i roden af WinSetup-sharet: USB-nøglen (provision/windows/Lib.ps1) og pxe-build.ps1 læser den.</summary>
    public string ShareJson() => Json.Ser(new
    {
        language = WindowsLanguage, inputLocale = WindowsInputLocale, timeZone = WindowsTimeZone, ianaTimeZone = TimeZone,
    });

    /// <summary>Sæt sprog, tastatur og tidszone ind i en Windows-svarfil (pladsholderne i provision/usb/*.xml).</summary>
    public string RenderUnattend(string template) => template
        .Replace("__LANG__", WindowsLanguage).Replace("__INPUTLOCALE__", WindowsInputLocale).Replace("__TIMEZONE__", WindowsTimeZone);
}
