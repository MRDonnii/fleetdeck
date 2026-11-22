using System.Diagnostics;
using System.Management;
using System.Text;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// BitLocker: status pr. drev og de numeriske gendannelsesadgangskoder (kun som SYSTEM).
/// Nøglerne holdes kun i hukommelsen, logges aldrig og sendes kun, når de har ændret sig (eller serveren mangler dem).
/// </summary>
public static class DiskCrypto
{
    public sealed record Snapshot(EncryptionInfo Info, List<RecoveryKeyItem> Keys);

    private const string StateFile = "escrow.sha256";

    public static async Task<Snapshot?> Collect(CancellationToken ct)
    {
        if (!AgentOptions.IsSystem) return null;
        var info = new EncryptionInfo { Kind = EncryptionKinds.BitLocker, Portable = Portable() };
        var sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();
        BitLockerParse.Result? r = null;
        var ps = await Run("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(BitLockerParse.Script))}", ct);
        if (ps.Exit == 0)
            try { r = BitLockerParse.FromPowerShell(ps.Out); }
            catch (System.Text.Json.JsonException) { }
        // Windows uden BitLocker-modulet til PowerShell (fx Home med enhedskryptering): WMI og manage-bde
        r ??= await Fallback(ct);
        if (r is null)
        {
            // hverken BitLocker-modulet eller WMI-klassen findes (fx Windows Home uden enhedskryptering): disken er ikke krypteret
            info.Volumes = [new EncryptedVolume { Mount = sys, System = true, Encrypted = false, Protected = false, Status = "FullyDecrypted" }];
            info.Error = "BitLocker findes ikke på denne Windows-udgave (eller kunne ikke læses).";
            return new(info, []);
        }
        foreach (var v in r.Volumes) v.System = v.System || v.Mount == sys;
        info.Volumes = r.Volumes.OrderByDescending(v => v.System).ThenBy(v => v.Mount, StringComparer.Ordinal).ToList();
        info.KeysHash = RecoveryKeys.Hash(r.Keys);
        return new(info, r.Keys);
    }

    /// <summary>Win32_EncryptableVolume.GetKeyProtectors: 1 = TPM, 4 = TPM + PIN, 5 = TPM + startnøgle, 6 = TPM + PIN + startnøgle.</summary>
    private static bool? TpmProtector(ManagementObject o)
    {
        try
        {
            foreach (var type in new uint[] { 1, 4, 5, 6 })
            {
                var args = o.GetMethodParameters("GetKeyProtectors");
                args["KeyProtectorType"] = type;
                using var res = o.InvokeMethod("GetKeyProtectors", args, null);
                if (res?["VolumeKeyProtectorID"] is string[] { Length: > 0 }) return true;
            }
            return false;
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<BitLockerParse.Result?> Fallback(CancellationToken ct)
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2\Security\MicrosoftVolumeEncryption"),
                new ObjectQuery("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"));
            var vols = new List<EncryptedVolume>();
            var keys = new List<RecoveryKeyItem>();
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    if (o["DriveLetter"] is not string letter || letter.Length == 0) continue;
                    string? status = null; double? pct = null;
                    try
                    {
                        var res = o.InvokeMethod("GetConversionStatus", null, null);
                        status = Convert.ToInt32(res?["ConversionStatus"]) switch
                        {
                            0 => "FullyDecrypted", 1 => "FullyEncrypted", 2 => "EncryptionInProgress", 3 => "DecryptionInProgress",
                            4 => "EncryptionPaused", 5 => "DecryptionPaused", _ => null,
                        };
                        pct = res?["EncryptionPercentage"] is { } p ? Convert.ToDouble(p) : null;
                    }
                    catch (ManagementException) { }
                    var prot = o["ProtectionStatus"] is { } ps ? Convert.ToInt32(ps) switch { 1 => "On", 0 => "Off", _ => "Unknown" } : null;
                    var mine = new List<RecoveryKeyItem>();
                    if (status is not null and not "FullyDecrypted")
                    {
                        var mb = await Run("manage-bde.exe", $"-protectors -get {letter} -Type RecoveryPassword", ct);
                        mine = BitLockerParse.ManageBdeProtectors(mb.Out, letter);
                        keys.AddRange(mine);
                    }
                    var vol = BitLockerParse.Volume(letter, false, prot, status, null, pct, mine.Count);
                    vol.UsesTpm = TpmProtector(o);
                    vols.Add(vol);
                }
            return new(vols, keys);
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Bærbar: bærbart kabinet eller et batteri.</summary>
    internal static bool? Portable()
    {
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
                foreach (ManagementObject o in s.Get())
                    using (o)
                        if (o["ChassisTypes"] is ushort[] t && t.Any(x => x is 8 or 9 or 10 or 11 or 12 or 14 or 18 or 21 or 30 or 31 or 32)) return true;
            using var b = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
            return b.Get().Count > 0;
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>Send nøglerne, hvis de er ændret siden sidst, eller serveren mangler dem.</summary>
    public static bool Due(string dir, string? hash, bool serverWants)
    {
        if (hash is null) return false;
        if (serverWants) return true;
        try { return File.ReadAllText(Path.Combine(dir, StateFile)).Trim() != hash; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static void Sent(string dir, string hash)
    {
        try { File.WriteAllText(Path.Combine(dir, StateFile), hash); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Kør direkte (aldrig via jobbets output/fremdrift – her kan der stå nøgler).</summary>
    private static async Task<(int Exit, string Out)> Run(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } ct.ThrowIfCancellationRequested(); return (-1, ""); }
            return (p.ExitCode, await o);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); }
    }
}
