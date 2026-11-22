using System.Management;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// TPM fra WMI (root\CIMV2\Security\MicrosoftTpm, Win32_Tpm). Kræver administrator/SYSTEM; en fejl giver kun en
/// fejltekst i helbredet og vælter aldrig agenten.
/// </summary>
public static class TpmCollect
{
    private const string Scope = @"root\CIMV2\Security\MicrosoftTpm";
    private static readonly string[] Props =
        ["IsActivated_InitialValue", "IsEnabled_InitialValue", "IsOwned_InitialValue", "ManufacturerId", "ManufacturerIdTxt", "ManufacturerVersion", "SpecVersion"];

    public static TpmInfo? Collect()
    {
        if (!AgentOptions.IsSystem) return null;
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(Scope), new ObjectQuery("SELECT * FROM Win32_Tpm"));
            using var all = s.Get();
            var o = all.Cast<ManagementObject>().FirstOrDefault();
            if (o is null) return TpmParse.FromWmi(null);   // ingen TPM (eller slået fra i BIOS/UEFI)
            using (o)
            {
                var props = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var name in Props)
                    try { props[name] = o[name]; }
                    catch (ManagementException) { }   // ManufacturerIdTxt findes ikke på ældre Windows
                bool? ready = null; uint? info = null;
                try
                {
                    using var r = o.InvokeMethod("IsReadyInformation", null, null);
                    if (r?["IsReady"] is bool b) ready = b;
                    if (r?["Information"] is { } i) info = Convert.ToUInt32(i, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (ManagementException)
                {
                    try { using var r = o.InvokeMethod("IsReady", null, null); if (r?["IsReady"] is bool b) ready = b; }
                    catch (ManagementException) { }
                }
                return TpmParse.FromWmi(props, ready, info);
            }
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // WMI-klassen mangler (ingen TPM-driver) eller adgang nægtet
            return e is ManagementException { ErrorCode: ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass }
                ? TpmParse.FromWmi(null)
                : new TpmInfo { Present = false, Source = "wmi", Error = $"TPM kunne ikke læses ({e.GetType().Name})." };
        }
    }
}
