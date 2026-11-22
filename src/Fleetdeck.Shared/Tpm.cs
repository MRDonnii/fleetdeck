using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>TPM-chippen (Trusted Platform Module) på maskinen. En del af helbredet; indeholder aldrig nøgler.</summary>
public sealed class TpmInfo
{
    /// <summary>Maskinen har en TPM, styresystemet kan se. False = ingen (eller slået fra i BIOS/UEFI).</summary>
    public bool Present { get; set; }
    /// <summary>Specifikationsversion: "2.0" eller "1.2".</summary>
    public string? Version { get; set; }
    /// <summary>TCG-producent-id som tekst, fx "INTC", "AMD", "IFX", "NTC", "STM", "MSFT".</summary>
    public string? ManufacturerId { get; set; }
    /// <summary>Producentens navn, fx "Intel" eller "Infineon".</summary>
    public string? Manufacturer { get; set; }
    /// <summary>Firmwareversion, fx "7.2.2.0".</summary>
    public string? Firmware { get; set; }
    /// <summary><see cref="TpmKinds"/>: discrete | firmware | virtual. Null = kan ikke udledes.</summary>
    public string? Kind { get; set; }
    public bool? Enabled { get; set; }
    public bool? Activated { get; set; }
    public bool? Owned { get; set; }
    /// <summary>Klar til brug (Windows: Win32_Tpm.IsReady). Null = ukendt.</summary>
    public bool? Ready { get; set; }
    /// <summary>Det, Windows melder, TPM'en mangler (fx genstart eller ryd), på dansk.</summary>
    public List<string>? Attention { get; set; }
    /// <summary>Mac: ingen TPM, men fx "Apple Silicon (Secure Enclave)" eller "Apple T2 (Secure Enclave)".</summary>
    public string? Apple { get; set; }
    /// <summary>Hvor oplysningerne kom fra: wmi | sysfs | tpm2-tools | mac.</summary>
    public string? Source { get; set; }
    /// <summary>TPM'en kunne ikke læses (oplysningerne ovenfor er så ukendte).</summary>
    public string? Error { get; set; }
}

public static class TpmKinds
{
    /// <summary>Selvstændig chip på bundkortet (fx Infineon, Nuvoton, STMicro).</summary>
    public const string Discrete = "discrete";
    /// <summary>Firmware-TPM i processoren (Intel PTT, AMD fTPM, Qualcomm, Microsoft Pluton).</summary>
    public const string Firmware = "firmware";
    /// <summary>Virtuel TPM i en virtuel maskine (swtpm/QEMU, VMware, Google).</summary>
    public const string Virtual = "virtual";
}

/// <summary>TCG's producent-id'er (TPM Vendor ID Registry).</summary>
public static class TpmVendors
{
    private static readonly Dictionary<string, (string Name, string? Kind)> Known = new(StringComparer.Ordinal)
    {
        ["AMD"] = ("AMD", TpmKinds.Firmware), ["INTC"] = ("Intel", TpmKinds.Firmware), ["QCOM"] = ("Qualcomm", TpmKinds.Firmware),
        ["MSFT"] = ("Microsoft (Pluton eller Hyper-V)", TpmKinds.Firmware),
        ["IFX"] = ("Infineon", TpmKinds.Discrete), ["NTC"] = ("Nuvoton", TpmKinds.Discrete), ["STM"] = ("STMicroelectronics", TpmKinds.Discrete),
        ["ATML"] = ("Atmel", TpmKinds.Discrete), ["BRCM"] = ("Broadcom", TpmKinds.Discrete), ["NSM"] = ("National Semiconductor", TpmKinds.Discrete),
        ["SMSC"] = ("SMSC", TpmKinds.Discrete), ["WEC"] = ("Winbond", TpmKinds.Discrete), ["NTZ"] = ("Nationz", TpmKinds.Discrete),
        ["SNS"] = ("Sinosun", TpmKinds.Discrete), ["TXN"] = ("Texas Instruments", TpmKinds.Discrete), ["SMSN"] = ("Samsung", TpmKinds.Discrete),
        ["IBM"] = ("IBM (software-TPM)", TpmKinds.Virtual), ["VMW"] = ("VMware", TpmKinds.Virtual), ["GOOG"] = ("Google", TpmKinds.Virtual),
        ["HPE"] = ("HPE", null), ["HPI"] = ("HP", null), ["LEN"] = ("Lenovo", null), ["CSCO"] = ("Cisco", null), ["HISI"] = ("Huawei", null),
        ["ROCC"] = ("Rockchip", null), ["FLYS"] = ("Flyslice", null), ["SECE"] = ("SecEdge", null),
    };

    /// <summary>Id'et som fire ASCII-tegn (stor-endian), fx 0x494E5443 → "INTC" og 0x414D4400 → "AMD". Null hvis det ikke er tekst.</summary>
    public static string? Decode(uint id)
    {
        if (id == 0) return null;
        var bytes = new[] { (byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id };
        var s = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        return s.Length > 0 && s.All(c => c is >= '!' and <= '~') ? s : null;
    }

    /// <summary>Tekst-id (fx "INTC" eller "IFX ") renset; tal (decimal eller 0x…) afkodes først.</summary>
    public static string? Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var t = id.Trim().Trim('"').Trim('\0', ' ');
        if (TpmParse.ParseUInt(t) is { } n && (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.All(char.IsAsciiDigit)))
            return Decode(n);
        return t.Length > 0 ? t.ToUpperInvariant() : null;
    }

    public static string? Name(string? id) => id is not null && Known.TryGetValue(id, out var v) ? v.Name : id;
    public static string? Kind(string? id) => id is not null && Known.TryGetValue(id, out var v) ? v.Kind : null;
}

/// <summary>Læsning af TPM-oplysninger: WMI (Windows), sysfs og tpm2_getcap (Linux) og Mac.</summary>
public static partial class TpmParse
{
    /// <summary>Win32_Tpm.IsReadyInformation: de flag, der kræver noget af brugeren.</summary>
    public static List<string> Attention(uint info)
    {
        var o = new List<string>();
        if ((info & 0x2) != 0 || (info & 0x4) != 0) o.Add("Kræver genstart");
        if ((info & 0x8) != 0) o.Add("Ejes i forvejen – skal ryddes eller have ejerens adgangskode");
        if ((info & 0x10) != 0) o.Add("Kræver bekræftelse ved opstart (fysisk tilstedeværelse)");
        if ((info & 0x20) != 0) o.Add("Skal ryddes, før den kan bruges");
        if ((info & 0x40) != 0) o.Add("Er slået fra eller deaktiveret");
        return o;
    }

    /// <summary>
    /// Win32_Tpm (root\CIMV2\Security\MicrosoftTpm) → kontrakt. <paramref name="props"/> er instansens egenskaber
    /// (null = ingen instans, dvs. ingen TPM eller slået fra i BIOS/UEFI); <paramref name="ready"/> og
    /// <paramref name="readyInfo"/> er svaret fra IsReady/IsReadyInformation.
    /// </summary>
    public static TpmInfo FromWmi(IReadOnlyDictionary<string, object?>? props, bool? ready = null, uint? readyInfo = null)
    {
        if (props is null) return new TpmInfo { Present = false, Source = "wmi" };
        object? P(string n) => props.TryGetValue(n, out var v) ? v : null;
        var id = TpmVendors.Normalize(P("ManufacturerIdTxt") as string)
            ?? (P("ManufacturerId") is { } raw && ToUInt(raw) is { } n ? TpmVendors.Decode(n) : null);
        var fw = (P("ManufacturerVersion") as string)?.Trim();
        var info = new TpmInfo
        {
            Present = true, Source = "wmi",
            Version = SpecVersion(P("SpecVersion") as string),
            ManufacturerId = id, Manufacturer = TpmVendors.Name(id), Kind = TpmVendors.Kind(id),
            Firmware = string.IsNullOrEmpty(fw) ? null : fw,
            Enabled = ToBool(P("IsEnabled_InitialValue")), Activated = ToBool(P("IsActivated_InitialValue")), Owned = ToBool(P("IsOwned_InitialValue")),
            Ready = ready,
        };
        if (readyInfo is { } ri && Attention(ri) is { Count: > 0 } a) info.Attention = a;
        return info;
    }

    /// <summary>"2.0, 0, 1.38" → "2.0"; "1.2, 2, 3" → "1.2".</summary>
    public static string? SpecVersion(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var first = spec.Split(',')[0].Trim();
        return first switch { "2" => "2.0", "1" => "1.2", _ => first.Length > 0 ? first : null };
    }

    /// <summary>TPM 2.0-firmware fra TPM2_PT_FIRMWARE_VERSION_1/2, som Windows viser den: "7.2.2.0".</summary>
    public static string FirmwareVersion(uint v1, uint v2) => $"{v1 >> 16}.{v1 & 0xFFFF}.{v2 >> 16}.{v2 & 0xFFFF}";

    /// <summary>
    /// Linux: /sys/class/tpm/tpm0. <paramref name="present"/> = mappen findes; resten er filernes indhold (null = mangler).
    /// TPM 1.2 har "caps" (Manufacturer/TCG version/Firmware version) og enabled/active/owned.
    /// </summary>
    public static TpmInfo FromSysfs(bool present, string? versionMajor, string? caps = null, string? enabled = null, string? active = null, string? owned = null,
        bool tpmrmDevice = false)
    {
        if (!present) return new TpmInfo { Present = false, Source = "sysfs" };
        var info = new TpmInfo { Present = true, Source = "sysfs" };
        info.Version = versionMajor?.Trim() switch { "2" => "2.0", "1" => "1.2", _ => null };
        if (!string.IsNullOrWhiteSpace(caps))
        {
            string? F(string k) => Regex.Match(caps, @"^\s*" + k + @":\s*(.+?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;
            if (TpmVendors.Normalize(F("Manufacturer")) is { } id) { info.ManufacturerId = id; info.Manufacturer = TpmVendors.Name(id); info.Kind = TpmVendors.Kind(id); }
            info.Version ??= F("TCG version") is { } tv ? SpecVersion(tv) : null;
            info.Firmware = F("Firmware version");
        }
        info.Version ??= tpmrmDevice ? "2.0" : null;   // ældre kerne uden tpm_version_major: /dev/tpmrm0 findes kun for TPM 2.0
        info.Enabled = Flag(enabled); info.Activated = Flag(active); info.Owned = Flag(owned);
        if (info.Version == "1.2" && info.Enabled is { } e && info.Activated is { } a) info.Ready = e && a && info.Owned != false;
        return info;
    }

    /// <summary>"tpm2_getcap properties-fixed" (tpm2-tools 4/5 med raw:/value: og 3.x med værdien på samme linje) lagt oven i <paramref name="info"/>.</summary>
    public static TpmInfo WithTpm2Getcap(TpmInfo info, string? text)
    {
        var p = Tpm2Properties(text);
        if (p.Count == 0) return info;
        info.Source = "tpm2-tools";
        if (p.TryGetValue("PT_FAMILY_INDICATOR", out var fam) && fam.Value is { Length: > 0 } fv) info.Version = SpecVersion(fv);
        else info.Version ??= "2.0";
        if (p.TryGetValue("PT_MANUFACTURER", out var man))
        {
            var id = man.Raw is { } r ? TpmVendors.Decode(r) : TpmVendors.Normalize(man.Value);
            if (id is not null) { info.ManufacturerId = id; info.Manufacturer = TpmVendors.Name(id); info.Kind = TpmVendors.Kind(id); }
        }
        if (p.TryGetValue("PT_FIRMWARE_VERSION_1", out var f1) && f1.Raw is { } v1)
            info.Firmware = FirmwareVersion(v1, p.TryGetValue("PT_FIRMWARE_VERSION_2", out var f2) && f2.Raw is { } v2 ? v2 : 0);
        return info;
    }

    /// <summary>Egenskaberne fra tpm2_getcap som navn (uden "TPM2_"/"TPM_") → (rå tal, tekst).</summary>
    public static Dictionary<string, (uint? Raw, string? Value)> Tpm2Properties(string? text)
    {
        var o = new Dictionary<string, (uint? Raw, string? Value)>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return o;
        string? key = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (HeaderPattern().Match(line) is { Success: true } h)
            {
                key = h.Groups[1].Value;
                var rest = h.Groups[2].Value.Trim();
                o[key] = rest.Length == 0 ? (null, null) : (ParseUInt(rest), Unquote(rest));
                continue;
            }
            if (key is null) continue;
            if (SubPattern().Match(line) is not { Success: true } s) continue;
            var (r, v) = o[key];
            var val = s.Groups[2].Value.Trim();
            if (s.Groups[1].Value is "raw" or "as UINT32") r = ParseUInt(val) ?? r;
            else v = Unquote(val);
            o[key] = (r, v);
        }
        return o;
    }

    /// <summary>
    /// Mac: ingen TPM. Apple Silicon (arm64) og Intel-Mac med T2 har Secure Enclave i stedet.
    /// <paramref name="bridge"/> er "system_profiler SPiBridgeDataType" (tom på Intel-Mac uden T2).
    /// </summary>
    public static TpmInfo Mac(string? arch, string? bridge)
    {
        var info = new TpmInfo { Present = false, Source = "mac" };
        if (arch?.Trim() == "arm64") info.Apple = "Apple Silicon (Secure Enclave)";
        else if (bridge is not null && bridge.Contains("T2", StringComparison.Ordinal)) info.Apple = "Apple T2 (Secure Enclave)";
        return info;
    }

    internal static uint? ParseUInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().Trim('"');
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) && h <= uint.MaxValue ? (uint)h : null;
        return uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string? Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Trim('\0', ' ') : s;

    private static bool? Flag(string? s) => s?.Trim() switch { "1" => true, "0" => false, _ => null };

    private static bool? ToBool(object? v) => v switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var b) => b,
        _ => null,
    };

    private static uint? ToUInt(object v)
    {
        try { return v is string s ? ParseUInt(s) : Convert.ToUInt32(v, CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    [GeneratedRegex(@"^\s*TPM2?_(PT_[A-Z0-9_]+):\s*(.*)$")]
    private static partial Regex HeaderPattern();
    [GeneratedRegex(@"^\s+(raw|value|as UINT32|as string):\s*(.*)$")]
    private static partial Regex SubPattern();
}
