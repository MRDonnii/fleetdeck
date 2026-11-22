using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// TPM på Linux (/sys/class/tpm/tpm0 og tpm2_getcap, hvis tpm2-tools findes) og Secure Enclave på Mac.
/// Mac-svaret ændrer sig ikke og huskes derfor.
/// </summary>
public static class TpmCollect
{
    private const string Dir = "/sys/class/tpm/tpm0";
    private static TpmInfo? mac;

    public static TpmInfo Status()
    {
        if (Mac.Is)
            return mac ??= TpmParse.Mac(Shell.Out("/usr/bin/uname -m 2>/dev/null"),
                Shell.Out("/usr/sbin/system_profiler SPiBridgeDataType 2>/dev/null | grep -i 'Model Name'", 15));
        var present = Directory.Exists(Dir);
        var info = TpmParse.FromSysfs(present, Read("tpm_version_major"), Read("device/caps") ?? Read("caps"),
            Read("device/enabled") ?? Read("enabled"), Read("device/active") ?? Read("active"), Read("device/owned") ?? Read("owned"),
            File.Exists("/dev/tpmrm0"));
        if (present && info.Version != "1.2" && File.Exists("/dev/tpmrm0") && Shell.Has("tpm2_getcap"))
            info = TpmParse.WithTpm2Getcap(info, Shell.Out(
                "tpm2_getcap -T device:/dev/tpmrm0 properties-fixed 2>/dev/null || tpm2_getcap properties-fixed 2>/dev/null || tpm2_getcap -c properties-fixed 2>/dev/null", 10));
        return info;
    }

    private static string? Read(string rel)
    {
        try { var p = Path.Combine(Dir, rel); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
