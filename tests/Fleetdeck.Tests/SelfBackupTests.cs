using System.IO.Compression;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Tests;

public sealed class SelfBackupTests : IDisposable
{
    private const string Pw = "korrekt-hest-batteri";
    private readonly List<string> _dirs = [];

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "fd-backup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private static AppDb Db(string dataDir) =>
        new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(dataDir, SelfBackup.DbFile)}").Options);

    /// <summary>Indsæt en bruger med de kolonner, tabellen har på dette trin (påkrævede kolonner uden standard får 0 eller '').</summary>
    private static void AddUserRaw(AppDb db, string user)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        var cols = new List<(string Name, string Value)>();
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(Users)";
            using var r = info.ExecuteReader();
            while (r.Read())
            {
                string name = r.GetString(1), type = r.GetString(2);
                bool notNull = r.GetInt32(3) == 1, hasDefault = !r.IsDBNull(4), pk = r.GetInt32(5) > 0;
                if (pk) continue;
                var value = name switch
                {
                    "Username" => user, "PasswordHash" => "x", "Role" => Roles.Admin, "SecurityStamp" => Guid.NewGuid().ToString("N"),
                    "Created" => DateTime.UtcNow.ToString("O"), "Ipads" or "Perms" or "RecoveryCodes" => "[]",
                    _ => notNull && !hasDefault ? (type.Contains("INT", StringComparison.OrdinalIgnoreCase) ? "0" : "") : null,
                };
                if (value is not null) cols.Add((name, value));
            }
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"INSERT INTO Users ({string.Join(", ", cols.Select(c => $"\"{c.Name}\""))}) VALUES ({string.Join(", ", cols.Select((_, i) => "$p" + i))})";
        for (var i = 0; i < cols.Count; i++) cmd.Parameters.AddWithValue("$p" + i, cols[i].Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>En datamappe som på en rigtig server: database med en bruger, nøgler, egne udvidelser og et gendannelsessæt.</summary>
    private string DataDir(string user, string? migration = null)
    {
        var d = TempDir();
        using (var db = Db(d))
        {
            if (migration is null) db.Database.Migrate();
            else db.GetService<IMigrator>().Migrate(migration);
            if (migration is null) db.Users.Add(new User { Username = user, PasswordHash = "x" });
            else AddUserRaw(db, user);   // ældre skema: den nuværende model kan have kolonner, der ikke findes endnu
            db.Settings.Add(new Setting { Key = "server_url", Value = "http://" + user });
            db.SaveChanges();
        }
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(Path.Combine(d, "keys"));
        File.WriteAllText(Path.Combine(d, "keys", "key-1.xml"), "<key>" + user + "</key>");
        File.WriteAllText(Path.Combine(d, "keys", "agent-signing.pem"), "PEM-" + user);
        File.WriteAllText(Path.Combine(d, "extensions.json"), "[]");
        Directory.CreateDirectory(Path.Combine(d, "kits", "1"));
        File.WriteAllBytes(Path.Combine(d, "kits", "1", "kit.zip"), [1, 2, 3, 4]);
        return d;
    }

    private static List<string> Known()
    {
        using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite("Data Source=:memory:").Options);
        return db.Database.GetMigrations().ToList();
    }

    private static string MakeBackup(string dataDir, string? pw = Pw, BackupOptions? opt = null)
    {
        var file = Path.Combine(dataDir, "backups", SelfBackup.FileName("manual", DateTime.UtcNow, "9.9.9"));
        SelfBackup.Create(dataDir, file, pw is null ? BackupSecret.FromLocalKey(SelfBackup.LocalKey(dataDir)) : BackupSecret.FromPassword(pw),
            "manual", opt ?? new(Kits: true), "9.9.9");
        return file;
    }

    [Fact]
    public void RoundTrip_RestoresDatabaseKeysAndKits_AndKeepsPreRestoreBackup()
    {
        var source = DataDir("anna");
        var file = MakeBackup(source);
        var h = SelfBackup.ReadHeader(file);
        Assert.Equal("manual", h.Kind);
        Assert.Equal("9.9.9", h.AppVersion);
        Assert.Contains("kits", h.Contents);
        Assert.Equal(Known().Last(), h.Migration);

        var target = DataDir("bent");
        var plan = SelfBackup.Stage(target, file, BackupSecret.FromPassword(Pw), Known(), "test");
        Assert.NotNull(SelfBackup.Pending(target));
        Assert.Equal(h.Created, plan.BackupCreated);

        var r = SelfBackup.ApplyPending(target, "9.9.9", _ => { });
        Assert.NotNull(r);
        Assert.True(r!.Ok, r.Message);
        Assert.False(Directory.Exists(Path.Combine(target, SelfBackup.StagingDir)));
        using (var db = Db(target))
        {
            Assert.Equal(["anna"], db.Users.Select(u => u.Username).ToList());
            Assert.Empty(db.Database.GetPendingMigrations());
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal("<key>anna</key>", File.ReadAllText(Path.Combine(target, "keys", "key-1.xml")));
        Assert.Equal("PEM-anna", File.ReadAllText(Path.Combine(target, "keys", "agent-signing.pem")));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(target, "kits", "1", "kit.zip")));
        Assert.True(SelfBackup.LastResult(target)!.Ok);

        // De hidtidige data ("bent") ligger i en pre-restore-backup, som kan gendannes uden adgangskode på samme server
        Assert.NotNull(r.PreRestore);
        var pre = Path.Combine(target, SelfBackup.SafetyDir, r.PreRestore!);
        Assert.Equal("local", SelfBackup.ReadHeader(pre).Kdf);
        var check = TempDir();
        SelfBackup.Extract(pre, BackupSecret.FromLocalKey(SelfBackup.LocalKey(target)), Path.Combine(check, "x"));
        Assert.Equal("PEM-bent", File.ReadAllText(Path.Combine(check, "x", "keys", "agent-signing.pem")));
        // … men ikke på en anden server
        var other = TempDir();
        var e = Assert.Throws<BackupException>(() => SelfBackup.Extract(pre, BackupSecret.FromLocalKey(SelfBackup.LocalKey(other)), Path.Combine(other, "x")));
        Assert.Equal("foreign", e.Code);
    }

    [Fact]
    public void RestoreIntoEmptyDataDir_Works()
    {
        var file = MakeBackup(DataDir("anna"), opt: new());
        var target = TempDir();
        SelfBackup.Stage(target, file, BackupSecret.FromPassword(Pw), Known(), "test");
        var r = SelfBackup.ApplyPending(target, "9.9.9", _ => { })!;
        Assert.True(r.Ok, r.Message);
        Assert.Null(r.PreRestore);
        Assert.False(Directory.Exists(Path.Combine(target, "kits")));
        using var db = Db(target);
        Assert.Equal("anna", db.Users.Single().Username);
    }

    [Fact]
    public void WrongPassword_FailsCleanly()
    {
        var file = MakeBackup(DataDir("anna"));
        var target = DataDir("bent");
        var e = Assert.Throws<BackupException>(() => SelfBackup.Stage(target, file, BackupSecret.FromPassword("forkert-adgangskode"), Known(), "test"));
        Assert.Equal("password", e.Code);
        Assert.False(Directory.Exists(Path.Combine(target, SelfBackup.StagingDir)));
        Assert.Null(SelfBackup.ApplyPending(target, "9.9.9", _ => { }));
        e = Assert.Throws<BackupException>(() => SelfBackup.Stage(target, file, BackupSecret.FromLocalKey(SelfBackup.LocalKey(target)), Known(), "test"));
        Assert.Equal("password", e.Code);
    }

    [Fact]
    public void ShortPassword_IsRefused()
    {
        var d = DataDir("anna");
        var e = Assert.Throws<BackupException>(() => MakeBackup(d, "kort"));
        Assert.Equal("password", e.Code);
    }

    /// <summary>Skriver backuppen om med ændret hoved og/eller indhold.</summary>
    private static string Rewrite(string file, Func<byte[], byte[]>? header = null, Func<byte[], byte[]>? payload = null)
    {
        byte[] h, p;
        using (var z = ZipFile.OpenRead(file))
        {
            using (var ms = new MemoryStream()) { z.GetEntry("fdbackup.json")!.Open().CopyTo(ms); h = ms.ToArray(); }
            using (var ms = new MemoryStream()) { z.GetEntry("payload.bin")!.Open().CopyTo(ms); p = ms.ToArray(); }
        }
        var outFile = file + "." + Guid.NewGuid().ToString("N") + ".fdbackup";
        using (var z = ZipFile.Open(outFile, ZipArchiveMode.Create))
        {
            using (var s = z.CreateEntry("fdbackup.json").Open()) s.Write(header?.Invoke(h) ?? h);
            using (var s = z.CreateEntry("payload.bin").Open()) s.Write(payload?.Invoke(p) ?? p);
        }
        return outFile;
    }

    [Fact]
    public void TamperedFile_IsRejected()
    {
        var src = DataDir("anna");
        var file = MakeBackup(src);
        var target = TempDir();

        // en bit vendt midt i det krypterede indhold
        var flipped = Rewrite(file, payload: p => { var c = (byte[])p.Clone(); c[c.Length / 2] ^= 0x01; return c; });
        Assert.Equal("tampered", Assert.Throws<BackupException>(() => SelfBackup.Stage(target, flipped, BackupSecret.FromPassword(Pw), Known(), "t")).Code);

        // sidste blok fjernet (afkortet fil)
        var truncated = Rewrite(file, payload: p => p[..^20]);
        Assert.Equal("tampered", Assert.Throws<BackupException>(() => SelfBackup.Stage(target, truncated, BackupSecret.FromPassword(Pw), Known(), "t")).Code);

        // hovedet ændret (fx typen) – det er autentificeret sammen med indholdet
        var header = Rewrite(file, header: h => System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(h).Replace("\"manual\"", "\"auto\"")));
        Assert.Equal("auto", SelfBackup.ReadHeader(header).Kind);
        Assert.Equal("tampered", Assert.Throws<BackupException>(() => SelfBackup.Stage(target, header, BackupSecret.FromPassword(Pw), Known(), "t")).Code);

        // ikke en backup overhovedet
        var junk = Path.Combine(target, "junk.fdbackup");
        File.WriteAllText(junk, "hej");
        Assert.Equal("invalid", Assert.Throws<BackupException>(() => SelfBackup.ReadHeader(junk)).Code);

        Assert.False(Directory.Exists(Path.Combine(target, SelfBackup.StagingDir)));
        // den ægte fil virker stadig
        SelfBackup.Stage(target, file, BackupSecret.FromPassword(Pw), Known(), "t");
    }

    [Fact]
    public void BackupFromNewerVersion_IsRejected()
    {
        var file = MakeBackup(DataDir("anna"));
        var target = TempDir();
        var e = Assert.Throws<BackupException>(() => SelfBackup.Stage(target, file, BackupSecret.FromPassword(Pw), Known().SkipLast(1), "t"));
        Assert.Equal("newer", e.Code);
    }

    [Theory]
    [InlineData("fleetdeck.db", true)]
    [InlineData("keys/key-1.xml", true)]
    [InlineData("kits/3/kit.zip", true)]
    [InlineData("ipad-backups/ipad-1/a/b", true)]
    [InlineData("../evil", false)]
    [InlineData("kits/../../evil", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("keys/sub/x", false)]
    [InlineData("backup-local.key", false)]
    [InlineData("appsettings.json", false)]
    public void ArchivePaths_AreRestricted(string name, bool ok) => Assert.Equal(ok, SelfBackup.AllowedEntry(name));

    [Fact]
    public void Retention_KeepsNewestPerKind()
    {
        var d = TempDir();
        var t = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 6; i++) File.WriteAllText(Path.Combine(d, SelfBackup.FileName("auto", t.AddDays(i), "1.0.0")), "");
        for (var i = 0; i < 2; i++) File.WriteAllText(Path.Combine(d, SelfBackup.FileName("manual", t.AddDays(i), "1.0.0")), "");
        File.WriteAllText(Path.Combine(d, "fleetdeck-auto-andet.fdbackup"), "");   // ukendt navn røres ikke

        var removed = SelfBackup.Prune(d, "auto", 3);
        Assert.Equal(3, removed.Count);
        var left = Directory.GetFiles(d).Select(Path.GetFileName).ToList();
        Assert.Equal(3, left.Count(n => SelfBackup.NameRx().IsMatch(n!) && SelfBackup.KindOf(n!) == "auto"));
        Assert.Contains(SelfBackup.FileName("auto", t.AddDays(5), "1.0.0"), left);
        Assert.DoesNotContain(SelfBackup.FileName("auto", t.AddDays(2), "1.0.0"), left);
        Assert.Equal(2, left.Count(n => SelfBackup.KindOf(n!) == "manual"));
        Assert.Contains("fleetdeck-auto-andet.fdbackup", left);
    }

    [Fact]
    public async Task PendingMigrations_TriggerBackupFirst_KeepingThree()
    {
        var all = Known();
        var d = DataDir("anna", migration: all[^2]);
        var names = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            await using var db = Db(d);
            var n = await SelfBackupService.BeforeMigrate(db, d, "9.9.9", NullLogger.Instance);
            Assert.NotNull(n);
            names.Add(n!);
        }
        SqliteConnection.ClearAllPools();
        var dir = Path.Combine(d, SelfBackup.SafetyDir);
        var left = Directory.GetFiles(dir, "*.fdbackup").Select(Path.GetFileName).ToList();
        Assert.Equal(3, left.Count);
        Assert.DoesNotContain(names[0], left);
        var h = SelfBackup.ReadHeader(Path.Combine(dir, names[^1]));
        Assert.Equal("pre-migration", h.Kind);
        Assert.Equal(all[^2], h.Migration);

        // efter migrationen (eller på en ny server uden database): ingen backup
        await using (var db = Db(d)) { await db.Database.MigrateAsync(); Assert.Null(await SelfBackupService.BeforeMigrate(db, d, "9.9.9", NullLogger.Instance)); }
        var empty = TempDir();
        await using (var db = Db(empty)) Assert.Null(await SelfBackupService.BeforeMigrate(db, empty, "9.9.9", NullLogger.Instance));
    }

    [Fact]
    public void StaleAndFailedBackups_RaiseAlert()
    {
        var s = new BackupSchedule(true, 3, 7, "backups", "", false, false, true);
        var now = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("", SelfBackupService.Evaluate(s, now.AddDays(-1), "", now.AddDays(-30), now).State);
        Assert.Equal("failed", SelfBackupService.Evaluate(s, now.AddDays(-1), "disken er fuld", now.AddDays(-30), now).State);
        Assert.Equal("stale", SelfBackupService.Evaluate(s, now.AddDays(-3), "", now.AddDays(-30), now).State);
        Assert.Equal("stale", SelfBackupService.Evaluate(s, null, "", now.AddDays(-3), now).State);
        Assert.Equal("", SelfBackupService.Evaluate(s, null, "", now.AddHours(-1), now).State);
        Assert.Equal("", SelfBackupService.Evaluate(s with { Enabled = false }, now.AddDays(-9), "fejl", now.AddDays(-30), now).State);
    }

    [Fact]
    public async Task AdminReset_ResetsPasswordAndSessions()
    {
        var d = DataDir("anna");
        await using var db = Db(d);
        var anna = db.Users.Single();
        anna.Role = Roles.Admin;
        var stamp = anna.SecurityStamp;
        await db.SaveChangesAsync();
        var r = await AdminReset.Reset(db, null);
        Assert.Equal("anna", r.Username);
        Assert.False(r.Created);
        Assert.True(Crypto.VerifyPassword(r.Password, db.Users.Single().PasswordHash));
        Assert.NotEqual(stamp, db.Users.Single().SecurityStamp);
        var r2 = await AdminReset.Reset(db, "nødadmin");
        Assert.True(r2.Created);
        Assert.Equal(Roles.Admin, db.Users.Single(u => u.Username == "nødadmin").Role);
    }

    [Fact]
    public void Endpoints_AreAdminOnly_ExceptFirstRun()
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=:memory:"));
        b.Services.AddScoped<AuditService>();
        b.Services.AddSingleton<SelfBackupService>();
        var app = b.Build();
        app.MapBackupEndpoints();
        var eps = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(eps);
        foreach (var e in eps)
        {
            var route = e.RoutePattern.RawText ?? "";
            var policies = e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            if (route.StartsWith("/api/backup/first-run", StringComparison.Ordinal)) Assert.Empty(policies);
            else Assert.Contains("admin", policies);
        }
        Assert.Equal(3, eps.Count(e => e.RoutePattern.RawText!.StartsWith("/api/backup/first-run", StringComparison.Ordinal)));
    }
}
