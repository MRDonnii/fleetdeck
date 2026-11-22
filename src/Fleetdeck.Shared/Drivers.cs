namespace Fleetdeck.Shared;

/// <summary>En installeret driver (Windows: Win32_PnPSignedDriver).</summary>
public sealed class DriverEntry
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Provider { get; set; }
    /// <summary>Enhedsklassen (DISPLAY, NET, MEDIA …).</summary>
    public string? Class { get; set; }
    /// <summary>Driverens dato (yyyy-MM-dd).</summary>
    public string? Date { get; set; }
}

public static class DriverList
{
    public const int Max = 400;

    /// <summary>
    /// Ryd listen: uden Microsofts egne indbyggede drivere (de skifter kun med Windows selv), uden tomme navne og dubletter
    /// (samme enhed og version flere gange), sorteret og begrænset til <see cref="Max"/>.
    /// </summary>
    public static List<DriverEntry> Clean(IEnumerable<DriverEntry> all) => all
        .Where(d => !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.Version))
        .Where(d => d.Provider is null || !d.Provider.Trim().Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
        .Select(d => new DriverEntry
        {
            Name = SoftwareIcons.Clip(d.Name, 200)!, Version = SoftwareIcons.Clip(d.Version, 60), Provider = SoftwareIcons.Clip(d.Provider, 100),
            Class = SoftwareIcons.Clip(d.Class, 40), Date = d.Date,
        })
        .DistinctBy(d => (d.Name.ToLowerInvariant(), d.Version))
        .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
        .Take(Max)
        .ToList();
}
