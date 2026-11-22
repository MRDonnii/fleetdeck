using Fleetdeck.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// <c>kontrolrum-sso &lt;brugernavn&gt; [...]</c>: lav en ny SSO-nøgle til Kontrolrum (gemmes krypteret som fra Indstillinger),
/// sæt flueben "Må logges ind fra Kontrolrum" for de nævnte brugere, og skriv KUN nøglen på stdout – så den kan sendes direkte
/// videre til Kontrolrum (pipe) uden at blive vist. Alt andet skrives på stderr. Genstart serveren bagefter (indstillinger caches).
/// </summary>
public static class KontrolrumSsoCli
{
    public static async Task<int> Run(string[] args, Paths paths)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Brug: kontrolrum-sso <brugernavn> [brugernavn …]"); return 2; }
        var dbFile = Path.Combine(paths.Data, SelfBackup.DbFile);
        if (!File.Exists(dbFile)) { Console.Error.WriteLine($"Fandt ingen database i {paths.Data}."); return 1; }
        var dp = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(paths.Data, "keys")), b => b.SetApplicationName("Fleetdeck"));
        var sso = new KontrolrumSso(dp, TimeProvider.System);
        await using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={dbFile}").Options);
        if ((await db.Database.GetPendingMigrationsAsync()).Any()) { Console.Error.WriteLine("Databasen er ikke opdateret – start serveren én gang først."); return 1; }
        var users = await db.Users.Where(u => args.Contains(u.Username)).ToListAsync();
        var missing = args.Except(users.Select(u => u.Username)).ToList();
        if (missing.Count > 0) { Console.Error.WriteLine("Ukendte brugere: " + string.Join(", ", missing)); return 1; }
        var key = KontrolrumSso.NewKey();
        async Task Set(string k, string v)
        {
            var s = await db.Settings.FindAsync(k);
            if (s is null) db.Settings.Add(new Setting { Key = k, Value = v }); else s.Value = v;
        }
        await Set(KontrolrumSso.KeySetting, sso.Protect(key));
        await Set(KontrolrumSso.CreatedSetting, DateTime.UtcNow.ToString("O"));
        foreach (var u in users) u.KontrolrumSso = true;
        db.Audit.Add(new AuditEntry { User = "system", Action = "settings.kontrolrum_sso", Detail = $"ny nøgle fra kommandolinjen · {KontrolrumSso.Fingerprint(key)} · brugere: {string.Join(", ", args)}" });
        await db.SaveChangesAsync();
        Console.Error.WriteLine($"SSO-nøgle lavet ({KontrolrumSso.Fingerprint(key)}), flueben sat for: {string.Join(", ", args)}. Genstart Fleetdeck.");
        Console.Out.Write(key);
        return 0;
    }
}
