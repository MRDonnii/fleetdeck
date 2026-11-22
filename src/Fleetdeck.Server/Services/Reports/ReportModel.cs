using System.Globalization;

namespace Fleetdeck.Server.Services.Reports;

/// <summary>
/// En kolonne i en rapport. Kind styrer visning og eksport:
/// text | num | pct | date | day | bool | sev (critical/warning/info) | min (minutter).
/// </summary>
public sealed record ReportColumn(string Key, string Label, string Kind = "text");

public sealed record ReportStat(string Label, string Value, string? Color = null);

/// <summary>Én tabel i en rapport (fx "Ventende" og "Fejlede" i Opdateringer). Rækkerne er værdier i kolonnernes rækkefølge.</summary>
public sealed class ReportSection
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Note { get; init; }
    public List<ReportColumn> Columns { get; init; } = [];
    public List<object?[]> Rows { get; init; } = [];
}

public sealed class ReportResult
{
    public string Name { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Filteret i ord, fx "Seneste 30 dage · profil Børn".</summary>
    public string Filter { get; set; } = "";
    public DateTime Generated { get; init; }
    public List<ReportStat> Stats { get; init; } = [];
    public List<ReportSection> Sections { get; init; } = [];

    public int RowCount => Sections.Sum(s => s.Rows.Count);

    /// <summary>Kort linje til push-beskeder (planlagte rapporter).</summary>
    public string Summary => $"{Title}: " + (Stats.Count == 0 ? $"{RowCount} rækker" : string.Join(" · ", Stats.Take(3).Select(s => $"{s.Label} {s.Value}")));
}

/// <summary>Filtre fra URL'en. Machines/Profile afgrænser maskinerne, Days perioden, Q et navnefilter (Software).</summary>
public sealed record ReportFilter(IReadOnlyList<int>? Machines = null, int? Profile = null, int? Days = null, string? Q = null)
{
    public static ReportFilter Parse(string? machines, int? profile, int? days, string? q) => new(
        machines is { Length: > 0 } ? machines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).Distinct().ToList() : null,
        profile is > 0 ? profile : null, days is > 0 ? Math.Min(days.Value, 3650) : null, string.IsNullOrWhiteSpace(q) ? null : q.Trim());
}

public sealed record ReportDef(string Name, string Title, string Description, string Group, bool Machines, bool Period, bool Search, int DefaultDays,
    int[] DayChoices);

/// <summary>Formatering til CSV og PDF (dansk: decimalkomma, lokal tid).</summary>
public static class ReportFormat
{
    public static readonly CultureInfo Da = CultureInfo.GetCultureInfo("da-DK");

    public static string Text(object? v, string kind) => v switch
    {
        null => "",
        DateTime d => Local(d).ToString(kind == "day" ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "Ja" : "Nej",
        double x => kind == "pct" ? x.ToString("0.#", Da) + " %" : x.ToString("0.##", Da),
        float x => ((double)x).ToString("0.##", Da),
        int or long when kind == "min" => Minutes(Convert.ToInt64(v, CultureInfo.InvariantCulture)),
        int or long => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + (kind == "pct" ? " %" : ""),
        string s when kind == "sev" => s switch { "critical" => "Kritisk", "warning" => "Advarsel", "info" => "Info", _ => s },
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Tal i CSV uden enhed (så Excel regner med dem).</summary>
    public static string CsvNumber(object v) => v switch
    {
        double x => x.ToString("0.##", Da),
        float x => ((double)x).ToString("0.##", Da),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    public static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), IpadEngine.Tz);

    public static string Minutes(long m) => m < 60 ? $"{m} m" : $"{m / 60} t {m % 60} m";
}
