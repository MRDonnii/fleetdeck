using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Glemt administrator-adgangskode, uden adgang til brugerfladen:
/// <list type="bullet">
/// <item><c>dotnet Fleetdeck.Server.dll reset-admin [brugernavn]</c> (fx med <c>docker exec</c>) – skriver en midlertidig adgangskode ud.</item>
/// <item>eller en fil <c>reset-admin</c> i datamappen (evt. med brugernavnet i) – ved næste start skrives den midlertidige
/// adgangskode i containerens log, og filen slettes.</item>
/// </list>
/// Brugeren bliver administrator, alle dens sessioner logges ud, og findes brugernavnet ikke, oprettes det.
/// </summary>
public static class AdminReset
{
    public const string ResetFile = "reset-admin";

    public sealed record Result(string Username, string Password, bool Created);

    /// <summary>Nulstiller (eller opretter) en administrator. Uden navn: den eneste administrator, hvis der kun er én.</summary>
    public static async Task<Result> Reset(AppDb db, string? username)
    {
        username = username?.Trim();
        User? u;
        if (string.IsNullOrEmpty(username))
        {
            var admins = await db.Users.Where(x => x.Role == Roles.Admin).OrderBy(x => x.Id).ToListAsync();
            if (admins.Count == 0)
            {
                if (await db.Users.AnyAsync()) throw new InvalidOperationException("Der er ingen administrator. Angiv et brugernavn, der skal oprettes eller gøres til administrator.");
                username = "admin";
                u = null;
            }
            else if (admins.Count == 1) { u = admins[0]; username = u.Username; }
            else throw new InvalidOperationException("Der er flere administratorer – angiv hvilken: " + string.Join(", ", admins.Select(a => a.Username)));
        }
        else
        {
            if (username.Length is < 2 or > 64) throw new InvalidOperationException("Brugernavnet skal være på 2–64 tegn");
            u = await db.Users.FirstOrDefaultAsync(x => x.Username == username);
        }
        var pw = Crypto.Token(12);
        var created = u is null;
        if (u is null) { u = new User { Username = username! }; db.Users.Add(u); }
        u.PasswordHash = Crypto.HashPassword(pw);
        u.Role = Roles.Admin;
        TwoFactorService.Disable(u);   // mistet telefon: totrinsbekræftelsen slås fra, så den midlertidige adgangskode virker
        u.SecurityStamp = Guid.NewGuid().ToString("N");   // log alle sessioner ud
        db.Audit.Add(new AuditEntry { User = "system", Action = "admin.reset", Detail = (created ? "Administrator oprettet: " : "Adgangskode nulstillet: ") + u.Username });
        await db.SaveChangesAsync();
        return new(u.Username, pw, created);
    }

    /// <summary><c>reset-admin [brugernavn]</c> fra kommandolinjen. Returnerer procesens slutkode.</summary>
    public static async Task<int> Cli(string[] args, Paths paths)
    {
        var dbFile = Path.Combine(paths.Data, SelfBackup.DbFile);
        if (!File.Exists(dbFile))
        {
            Console.Error.WriteLine($"Fandt ingen database i {paths.Data}. Er DATA_DIR sat rigtigt?");
            return 1;
        }
        try
        {
            await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={dbFile}").Options);
            if ((await db.Database.GetPendingMigrationsAsync()).Any())
            {
                Console.Error.WriteLine("Databasen er ikke opdateret til denne version. Start serveren én gang først (eller brug filen reset-admin i datamappen).");
                return 1;
            }
            var r = await Reset(db, args.FirstOrDefault());
            Print(r, Console.Out);
            return 0;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>Ved opstart: findes filen <c>reset-admin</c> i datamappen, nulstilles adgangskoden, og filen slettes.</summary>
    public static async Task FromFile(AppDb db, Paths paths, ILogger log)
    {
        var f = Path.Combine(paths.Data, ResetFile);
        if (!File.Exists(f)) return;
        string? name;
        try { name = (await File.ReadAllTextAsync(f)).Trim(); }
        catch (IOException e) { log.LogError("reset-admin kunne ikke læses: {Msg}", e.Message); return; }
        try
        {
            var r = await Reset(db, name.Length == 0 ? null : name.Split('\n')[0].Trim());
            Print(r, Console.Out);
            log.LogWarning("Administrator-adgangskoden for {User} er nulstillet via filen reset-admin (se ovenfor)", r.Username);
        }
        catch (InvalidOperationException e) { log.LogError("reset-admin: {Msg}", e.Message); }
        finally { SelfBackup.TryDelete(f); }
    }

    private static void Print(Result r, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("==================== Fleetdeck ====================");
        w.WriteLine(r.Created ? $"Administrator oprettet: {r.Username}" : $"Adgangskode nulstillet for: {r.Username}");
        w.WriteLine($"Midlertidig adgangskode: {r.Password}");
        w.WriteLine("Log ind og skift den under Indstillinger > Brugere.");
        w.WriteLine("===================================================");
        w.WriteLine();
        w.Flush();
    }
}
