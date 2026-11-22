using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Omdøbning af maskiner. Værtsnavnet (<see cref="Machine.Name"/>) er agentens identitet og ændres aldrig herfra;
/// admin giver i stedet maskinen et visningsnavn (<see cref="Machine.DisplayName"/>), som agenten aldrig overskriver.
/// </summary>
public static partial class MachineNames
{
    public const int MaxLength = 64;

    public sealed record RenameResult(int Status, string? Error, Machine? Machine = null, string? Old = null);

    /// <summary>Trimmer og samler mellemrum. Returnerer en fejlbesked, eller null når navnet kan bruges.</summary>
    public static string? Validate(string? raw, out string clean)
    {
        clean = Spaces().Replace(raw ?? "", " ").Trim();
        if (clean.Length == 0) return "Skriv et navn";
        if (clean.Length > MaxLength) return $"Navnet må højst være {MaxLength} tegn";
        if (clean.Any(char.IsControl)) return "Navnet indeholder ugyldige tegn";
        return null;
    }

    /// <summary>Navnet er optaget, hvis en anden maskine allerede vises med det (store/små bogstaver ignoreres).</summary>
    public static bool Taken(IEnumerable<(int Id, string Label)> machines, int id, string name) =>
        machines.Any(x => x.Id != id && string.Equals(x.Label, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Giver maskinen et nyt visningsnavn. Samme navn som værtsnavnet nulstiller til værtsnavnet.</summary>
    public static async Task<RenameResult> Rename(AppDb db, int id, string? raw, CancellationToken ct = default)
    {
        var m = await db.Machines.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return new(404, "Maskinen findes ikke");
        var all = await db.Machines.AsNoTracking().Select(x => new { x.Id, x.Name, x.DisplayName }).ToListAsync(ct);
        var r = Apply(m, all.Select(x => (x.Id, x.DisplayName ?? x.Name)), raw);
        if (r.Error is null) await db.SaveChangesAsync(ct);
        return r;
    }

    /// <summary>Selve reglerne (uden database): tjekker navnet og sætter visningsnavnet på maskinen.</summary>
    public static RenameResult Apply(Machine m, IEnumerable<(int Id, string Label)> all, string? raw)
    {
        if (Validate(raw, out var name) is { } err) return new(400, err, m);
        if (Taken(all, m.Id, name)) return new(409, $"En anden maskine hedder allerede \"{name}\"", m);
        var old = m.Label;
        m.DisplayName = name == m.Name ? null : name;
        return new(200, null, m, old);
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
