using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>Diskkryptering: BitLocker (Windows), FileVault (macOS) og LUKS (Linux).</summary>
public static class EncryptionKinds
{
    public const string BitLocker = "bitlocker", FileVault = "filevault", Luks = "luks";
    /// <summary>Kun BitLocker og FileVault har en gendannelsesnøgle, Fleetdeck kan gemme.</summary>
    public static bool Escrowable(string? kind) => kind is BitLocker or FileVault;
}

/// <summary>Kryptering på maskinen (en del af helbredet). Indeholder aldrig selve nøglerne.</summary>
public sealed class EncryptionInfo
{
    /// <summary>bitlocker | filevault | luks</summary>
    public string Kind { get; set; } = "";
    /// <summary>Bærbar (batteri eller bærbart kabinet). Null = ukendt.</summary>
    public bool? Portable { get; set; }
    public List<EncryptedVolume> Volumes { get; set; } = [];
    /// <summary>
    /// Agentens SHA-256 af de gendannelsesnøgler, den kan læse (kun BitLocker). Serveren sammenligner med sine egne
    /// og beder om nøglerne, hvis de afviger; feltet gemmes ikke.
    /// </summary>
    public string? KeysHash { get; set; }
    /// <summary>FileVault: har Mac'en en personlig gendannelsesnøgle? Null = ukendt.</summary>
    public bool? HasPersonalKey { get; set; }
    public string? Error { get; set; }
}

public sealed class EncryptedVolume
{
    /// <summary>Fx "C:", "/" eller "nvme0n1p3".</summary>
    public string Mount { get; set; } = "";
    /// <summary>Systemdrevet (Windows-drevet, Mac'ens startdisk eller den disk, / ligger på).</summary>
    public bool System { get; set; }
    /// <summary>Data er (helt eller delvist) krypteret.</summary>
    public bool? Encrypted { get; set; }
    /// <summary>Beskyttelsen er slået til (BitLocker kan være krypteret, men sat på pause).</summary>
    public bool? Protected { get; set; }
    /// <summary>Fx FullyEncrypted, EncryptionInProgress, FullyDecrypted.</summary>
    public string? Status { get; set; }
    public string? Method { get; set; }
    public double? Percent { get; set; }
    /// <summary>Antal numeriske gendannelsesadgangskoder (BitLocker) / personlige nøgler (FileVault) – aldrig selve nøglerne.</summary>
    public int RecoveryKeys { get; set; }
    /// <summary>BitLocker: drevet låses op med TPM'en (en Tpm-, TpmPin- eller TpmStartupKey-nøglebeskytter). Null = ukendt.</summary>
    public bool? UsesTpm { get; set; }
}

/// <summary>Én gendannelsesnøgle. Sendes kun fra agenten til serveren og vises kun via "Vis nøgle".</summary>
public sealed class RecoveryKeyItem
{
    public string Kind { get; set; } = "";
    public string Volume { get; set; } = "";
    /// <summary>BitLocker: nøglebeskytterens ID ({GUID}). FileVault: "personal".</summary>
    public string ProtectorId { get; set; } = "";
    public string Key { get; set; } = "";
    public override string ToString() => $"{Kind} {Volume} {ProtectorId}";   // aldrig nøglen
}

/// <summary>Agent → server: de nøgler, maskinen har nu. Full = alle maskinens nøgler af den slags (manglende er udskiftet).</summary>
public sealed class RecoveryKeyPost
{
    public List<RecoveryKeyItem> Keys { get; set; } = [];
    public bool Full { get; set; } = true;
    /// <summary>Jobbet, der lavede en ny nøgle (FileVault).</summary>
    public int? JobId { get; set; }
}

/// <summary>Svar på helbred: serveren mangler nøgler (eller har andre end maskinen).</summary>
public sealed class HealthAck
{
    public bool EscrowWanted { get; set; }
}

public static partial class RecoveryKeys
{
    public const string Hidden = "[gendannelsesnøgle skjult]";

    [GeneratedRegex(@"(?<!\d)\d{6}(?:-\d{6}){7}(?!\d)")]
    public static partial Regex BitLockerPattern();
    /// <summary>FileVault: 6 grupper á 4 store bogstaver/cifre.</summary>
    [GeneratedRegex(@"(?<![A-Z0-9-])[A-Z0-9]{4}(?:-[A-Z0-9]{4}){5}(?![A-Z0-9-])")]
    public static partial Regex FileVaultPattern();

    /// <summary>BitLockers numeriske adgangskode: 8 blokke á 6 cifre, hver deleligt med 11 og under 720896.</summary>
    public static bool IsBitLockerPassword(string? s)
    {
        if (s is null || !BitLockerPattern().IsMatch(s) || s.Length != 55) return false;
        foreach (var b in s.Split('-'))
        {
            var n = int.Parse(b, CultureInfo.InvariantCulture);
            if (n % 11 != 0 || n >= 720896) return false;
        }
        return true;
    }

    public static bool IsFileVaultKey(string? s) => s is { Length: 29 } && FileVaultPattern().IsMatch(s);

    public static bool Valid(RecoveryKeyItem k) =>
        k.Volume.Length is > 0 and <= 64 && k.ProtectorId.Length is > 0 and <= 128 && k.Kind switch
        {
            EncryptionKinds.BitLocker => IsBitLockerPassword(k.Key),
            EncryptionKinds.FileVault => IsFileVaultKey(k.Key),
            _ => false,
        };

    /// <summary>Samme hash på agent og server: rækkefølgen er ligegyldig.</summary>
    public static string? Hash(IEnumerable<RecoveryKeyItem> keys)
    {
        var lines = keys.Select(k => $"{k.Kind}|{k.Volume.ToUpperInvariant()}|{k.ProtectorId.ToUpperInvariant()}|{k.Key}").Order(StringComparer.Ordinal).ToList();
        return lines.Count == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    /// <summary>Fjern alt, der ligner en gendannelsesnøgle (job-output, fremdrift og logs).</summary>
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var t = BitLockerPattern().Replace(text, Hidden);
        return FileVaultPattern().Replace(t, Hidden);
    }
}

/// <summary>Uddata fra Get-BitLockerVolume og manage-bde (Windows).</summary>
public static partial class BitLockerParse
{
    /// <summary>Kører som SYSTEM. Enums skrives som tekst, og et enkelt drev bliver stadig en liste.</summary>
    public const string Script = """
        $ErrorActionPreference = 'Stop'
        $v = @(Get-BitLockerVolume | ForEach-Object {
          [pscustomobject]@{
            MountPoint = $_.MountPoint; VolumeType = "$($_.VolumeType)"; ProtectionStatus = "$($_.ProtectionStatus)"
            VolumeStatus = "$($_.VolumeStatus)"; EncryptionMethod = "$($_.EncryptionMethod)"; EncryptionPercentage = $_.EncryptionPercentage
            KeyProtector = @($_.KeyProtector | ForEach-Object { [pscustomobject]@{ KeyProtectorId = $_.KeyProtectorId; KeyProtectorType = "$($_.KeyProtectorType)"; RecoveryPassword = $_.RecoveryPassword } })
          } })
        ConvertTo-Json -InputObject $v -Depth 4 -Compress
        """;

    public sealed record Result(List<EncryptedVolume> Volumes, List<RecoveryKeyItem> Keys);

    /// <summary>JSON fra <see cref="Script"/> eller rå "Get-BitLockerVolume | ConvertTo-Json" (tal i stedet for tekst).</summary>
    public static Result FromPowerShell(string json)
    {
        var vols = new List<EncryptedVolume>();
        var keys = new List<RecoveryKeyItem>();
        var start = json.IndexOfAny(['[', '{']);
        if (start < 0) return new(vols, keys);
        using var doc = JsonDocument.Parse(json[start..]);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        foreach (var v in items)
        {
            if (v.ValueKind != JsonValueKind.Object) continue;
            var mount = Str(v, "MountPoint")?.Trim() ?? "";
            if (mount.Length == 0) continue;
            var type = Enum(v, "VolumeType", ["OperatingSystem", "Data", "Removable"]);
            var prot = Enum(v, "ProtectionStatus", ["Off", "On", "Unknown"]);
            var status = Enum(v, "VolumeStatus", ["FullyDecrypted", "FullyEncrypted", "EncryptionInProgress", "DecryptionInProgress", "EncryptionPaused", "DecryptionPaused"]);
            var pct = v.TryGetProperty("EncryptionPercentage", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : (double?)null;
            var n = 0;
            bool? tpm = null;
            if (v.TryGetProperty("KeyProtector", out var kps))
                foreach (var kp in kps.ValueKind == JsonValueKind.Array ? kps.EnumerateArray().ToList() : [kps])
                {
                    if (kp.ValueKind != JsonValueKind.Object) continue;
                    var pw = Str(kp, "RecoveryPassword")?.Trim();
                    var kt = Enum(kp, "KeyProtectorType", ProtectorTypes);
                    tpm = tpm == true || IsTpmProtector(kt);
                    if (kt != "RecoveryPassword" && !RecoveryKeys.IsBitLockerPassword(pw)) continue;
                    n++;
                    if (RecoveryKeys.IsBitLockerPassword(pw) && Str(kp, "KeyProtectorId") is { Length: > 0 } id)
                        keys.Add(new RecoveryKeyItem { Kind = EncryptionKinds.BitLocker, Volume = mount.ToUpperInvariant(), ProtectorId = id.Trim().ToUpperInvariant(), Key = pw! });
                }
            var vol = Volume(mount, type == "OperatingSystem", prot, status, Method(Str(v, "EncryptionMethod")), pct, n);
            vol.UsesTpm = tpm ?? (kps.ValueKind == JsonValueKind.Array ? false : null);
            vols.Add(vol);
        }
        return new(vols, keys);
    }

    public static EncryptedVolume Volume(string mount, bool system, string? protection, string? status, string? method, double? pct, int keys) => new()
    {
        Mount = mount.ToUpperInvariant(), System = system,
        Protected = protection switch { "On" => true, "Off" => false, _ => null },
        Encrypted = status switch { null or "" => null, "FullyDecrypted" => false, _ => true },
        Status = status, Method = method, Percent = pct, RecoveryKeys = keys,
    };

    /// <summary>BitLockerVolumeKeyProtectorType (rå ConvertTo-Json giver tallene).</summary>
    private static readonly string[] ProtectorTypes =
        ["Unknown", "Tpm", "ExternalKey", "RecoveryPassword", "TpmPin", "TpmStartupKey", "TpmPinStartupKey", "PublicKey", "Password", "TpmNetworkKey", "AdAccountOrGroup"];

    /// <summary>Tpm, TpmPin, TpmStartupKey, TpmPinStartupKey eller TpmNetworkKey.</summary>
    public static bool IsTpmProtector(string? type) => type is not null && type.StartsWith("Tpm", StringComparison.OrdinalIgnoreCase);

    private static string? Method(string? m) => m switch
    {
        null or "" or "None" or "0" => null,
        _ => m,
    };

    /// <summary>
    /// "manage-bde -protectors -get C: -Type RecoveryPassword" (eller uden -Type). Teksten er oversat på danske Windows,
    /// så der kigges kun efter ID'er ({GUID}) og 48-cifrede adgangskoder – adgangskoden hører til det seneste ID.
    /// </summary>
    public static List<RecoveryKeyItem> ManageBdeProtectors(string text, string volume)
    {
        var keys = new List<RecoveryKeyItem>();
        string? id = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (GuidPattern().Match(line) is { Success: true } g) { id = g.Value.ToUpperInvariant(); continue; }
            if (id is not null && RecoveryKeys.BitLockerPattern().Match(line) is { Success: true } k && RecoveryKeys.IsBitLockerPassword(k.Value))
            {
                keys.Add(new RecoveryKeyItem { Kind = EncryptionKinds.BitLocker, Volume = volume.ToUpperInvariant(), ProtectorId = id, Key = k.Value });
                id = null;
            }
        }
        return keys;
    }

    /// <summary>
    /// "manage-bde -status C:" på engelsk (de øvrige sprog giver kun drevet og nøglerne fra ManageBdeProtectors).
    /// </summary>
    public static EncryptedVolume? ManageBdeStatus(string text, string volume)
    {
        string? F(string label) => Regex.Match(text, @"^\s*" + Regex.Escape(label) + @":\s*(.+?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
        var conv = F("Conversion Status");
        var prot = F("Protection Status");
        if (conv is null && prot is null) return null;
        var pct = F("Percentage Encrypted") is { } ps && double.TryParse(ps.TrimEnd('%', ' ').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
        var status = conv switch
        {
            "Fully Encrypted" => "FullyEncrypted", "Fully Decrypted" => "FullyDecrypted", "Encryption in Progress" => "EncryptionInProgress",
            "Decryption in Progress" => "DecryptionInProgress", "Encryption Paused" => "EncryptionPaused", "Decryption Paused" => "DecryptionPaused",
            "Used Space Only Encrypted" => "FullyEncrypted", _ => conv,
        };
        var protection = prot is null ? null : prot.StartsWith("Protection On", StringComparison.OrdinalIgnoreCase) ? "On" : prot.StartsWith("Protection Off", StringComparison.OrdinalIgnoreCase) ? "Off" : "Unknown";
        var method = F("Encryption Method");
        return Volume(volume, false, protection, status, method is "None" ? null : method, pct, 0);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    /// <summary>Enum som tekst eller som tal (rå ConvertTo-Json).</summary>
    private static string? Enum(JsonElement e, string name, string[] names) =>
        Str(e, name) is not { } s ? null : int.TryParse(s, out var i) ? (i >= 0 && i < names.Length ? names[i] : s) : s;

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex GuidPattern();
}

/// <summary>fdesetup (macOS).</summary>
public static partial class FileVaultParse
{
    /// <summary>"fdesetup status": On/Off og evt. fremdrift for kryptering eller dekryptering.</summary>
    public static EncryptedVolume? Status(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var on = text.Contains("FileVault is On", StringComparison.Ordinal);
        var off = text.Contains("FileVault is Off", StringComparison.Ordinal);
        if (!on && !off) return null;
        var pct = PercentPattern().Match(text) is { Success: true } m && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
        var encrypting = text.Contains("Encryption in progress", StringComparison.OrdinalIgnoreCase);
        var decrypting = text.Contains("Decryption in progress", StringComparison.OrdinalIgnoreCase);
        var status = encrypting ? "EncryptionInProgress" : decrypting ? "DecryptionInProgress" : on ? "FullyEncrypted" : "FullyDecrypted";
        return new EncryptedVolume
        {
            Mount = "/", System = true, Protected = on && !decrypting, Encrypted = on || encrypting || decrypting, Status = status,
            Method = on ? "XTS-AES-128" : null, Percent = encrypting || decrypting ? pct : on ? 100 : 0,
        };
    }

    /// <summary>"fdesetup list": brugere, der kan låse disken op ("navn,UUID" pr. linje).</summary>
    public static List<string> Users(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(l => l.Split(',')[0].Trim()).Where(u => u.Length > 0).ToList();

    /// <summary>Den nye nøgle fra "fdesetup changerecovery -personal -outputplist" (plist eller tekst).</summary>
    public static string? NewKey(string output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        if (PlistKeyPattern().Match(output) is { Success: true } p && RecoveryKeys.IsFileVaultKey(p.Groups[1].Value)) return p.Groups[1].Value;
        var all = RecoveryKeys.FileVaultPattern().Matches(output).Select(m => m.Value).Distinct().ToList();
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>Indput til fdesetup via stdin (adgangskoden skrives aldrig til disk eller på kommandolinjen).</summary>
    public static string InputPlist(string user, string password) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        $"<plist version=\"1.0\"><dict><key>Username</key><string>{Xml(user)}</string><key>Password</key><string>{Xml(password)}</string></dict></plist>\n";

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    [GeneratedRegex(@"Percent completed = ([0-9.]+)")]
    private static partial Regex PercentPattern();
    [GeneratedRegex(@"<key>RecoveryKey</key>\s*<string>([^<]+)</string>")]
    private static partial Regex PlistKeyPattern();
}

/// <summary>LUKS (Linux): "lsblk -J -o NAME,TYPE,FSTYPE,MOUNTPOINT" og "cryptsetup status".</summary>
public static class LuksParse
{
    public sealed class Container
    {
        public string Device { get; init; } = "";
        /// <summary>Navnet på den åbne beholder (/dev/mapper/…). Null = låst/ikke åbnet.</summary>
        public string? Mapper { get; set; }
        public List<string> Mounts { get; } = [];
    }

    /// <summary>LUKS-beholdere (fstype crypto_LUKS), deres åbne mapper (type crypt) og hvad der er monteret ovenpå.</summary>
    public static (List<Container> Containers, bool RootFound, bool RootEncrypted) Lsblk(string json)
    {
        var list = new List<Container>();
        bool rootFound = false, rootEnc = false;
        var start = json.IndexOf('{');
        if (start < 0) return (list, false, false);
        using var doc = JsonDocument.Parse(json[start..]);
        if (!doc.RootElement.TryGetProperty("blockdevices", out var devs) || devs.ValueKind != JsonValueKind.Array) return (list, false, false);
        void Walk(JsonElement d, Container? c)
        {
            var name = S(d, "name") ?? "";
            var type = S(d, "type");
            if (S(d, "fstype") == "crypto_LUKS") { c = new Container { Device = name }; list.Add(c); }
            else if (type == "crypt" && c is null) { c = new Container { Device = name, Mapper = name }; list.Add(c); }   // dm-crypt uden LUKS-hoved
            else if (type == "crypt" && c!.Mapper is null) c.Mapper = name;
            foreach (var mp in Mounts(d))
            {
                c?.Mounts.Add(mp);
                if (mp == "/") { rootFound = true; rootEnc = c?.Mapper is not null; }
            }
            if (d.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
                foreach (var x in ch.EnumerateArray()) Walk(x, c);
        }
        foreach (var d in devs.EnumerateArray()) Walk(d, null);
        return (list, rootFound, rootEnc);
    }

    /// <summary>"cryptsetup status navn": type og cipher, fx "LUKS2 aes-xts-plain64".</summary>
    public static (bool Active, string? Method) Status(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, null);
        string? F(string k) => Regex.Match(text, @"^\s*" + k + @":\s*(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
        var active = text.Contains(" is active", StringComparison.Ordinal);
        var method = string.Join(' ', new[] { F("type"), F("cipher") }.Where(x => x is not null));
        return (active, method.Length > 0 ? method : null);
    }

    private static string? S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Mounts(JsonElement d)
    {
        if (S(d, "mountpoint") is { Length: > 0 } one) yield return one;
        if (d.TryGetProperty("mountpoints", out var many) && many.ValueKind == JsonValueKind.Array)
            foreach (var m in many.EnumerateArray())
                if (m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } s) yield return s;
    }
}
