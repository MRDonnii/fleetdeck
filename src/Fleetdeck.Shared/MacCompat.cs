using System.Runtime.InteropServices;

namespace Fleetdeck.Shared;

/// <summary>
/// Hvilke macOS-udgaver Fleetdeck kan køre på, og hvilken udgave af agent og app en Mac skal have.
/// Det almindelige byg (.NET 10 + Avalonia 12, og den native menu, bygget til macos12) kræver macOS 12 Monterey: binærernes
/// LC_BUILD_VERSION siger minos 12.0. Ældre Intel-Mac'er (macOS 10.15 Catalina og 11 Big Sur) får "legacy"-bygget på .NET 8
/// og Avalonia 11 (minos 10.15) og ingen native menu (appen viser selv ikonet). Ældre end 10.15 kan ingen af delene starte på.
/// </summary>
public static class MacCompat
{
    /// <summary>Det almindelige byg (Intel og Apple silicon).</summary>
    public static readonly Version Minimum = new(12, 0);
    /// <summary>Legacy-bygget (kun Intel).</summary>
    public static readonly Version LegacyMinimum = new(10, 15);
    /// <summary>Serverens navn for legacy-bygget i /api/agent/download?arch= (filerne hedder …-mac-x64-legacy).</summary>
    public const string LegacyArch = "x64-legacy";

    /// <summary>Dette program er legacy-bygget (net8.0).</summary>
    public static bool IsLegacyBuild =>
#if FLEETDECK_LEGACY
        true;
#else
        false;
#endif

    private static bool Below(Version? v, Version min) => v is { Major: > 0 } && new Version(v.Major, Math.Max(v.Minor, 0)) < min;

    /// <summary>"arm64", "x64" eller "x64-legacy" (Intel med macOS ældre end 12). Ukendt version = det almindelige byg.</summary>
    public static string ArchFor(Architecture a, Version? macOS) =>
        a == Architecture.Arm64 ? "arm64" : Below(macOS, Minimum) ? LegacyArch : "x64";

    /// <summary>Dansk besked, når macOS-udgaven er for gammel til Fleetdeck (null = den kan bruges).</summary>
    public static string? Unsupported(Architecture a, Version? macOS)
    {
        if (a == Architecture.Arm64 && Below(macOS, Minimum))
            return $"Fleetdeck kræver macOS 12 (Monterey) eller nyere på en Mac med Apple silicon – denne Mac har macOS {macOS}. " +
                   "Opdatér macOS (gratis) under Apple-menuen → Systemindstillinger → Softwareopdatering, og prøv igen.";
        if (a != Architecture.Arm64 && Below(macOS, LegacyMinimum))
            return $"Fleetdeck kræver macOS 10.15 (Catalina) eller nyere – denne Mac har macOS {macOS}. " +
                   "Se under Apple-menuen → Om denne Mac, om macOS kan opdateres.";
        return null;
    }
}
