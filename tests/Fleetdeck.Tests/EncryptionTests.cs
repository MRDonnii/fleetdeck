using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "encryption", name));
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Uddata fra manage-bde, Get-BitLockerVolume, fdesetup, lsblk og cryptsetup.</summary>
public class EncryptionParseTests
{
    private const string K1 = "466895-217492-569250-069608-104434-135707-527241-083622";
    private const string K2 = "309540-054054-123915-625218-602910-100716-346984-130779";
    private const string K3 = "612062-085217-178486-321860-089188-571923-071489-318747";

    [Fact]
    public void Get_BitLockerVolume_json_gives_volumes_and_only_recovery_passwords()
    {
        var r = BitLockerParse.FromPowerShell(Fixture.Read("get-bitlockervolume.json"));
        Assert.Equal(["C:", "D:", "E:"], r.Volumes.Select(v => v.Mount));
        var c = r.Volumes[0];
        Assert.True(c.System);
        Assert.True(c.Protected);
        Assert.True(c.Encrypted);
        Assert.Equal("FullyEncrypted", c.Status);
        Assert.Equal("XtsAes128", c.Method);
        Assert.Equal(100, c.Percent);
        Assert.Equal(1, c.RecoveryKeys);
        var d = r.Volumes[1];
        Assert.False(d.System);
        Assert.False(d.Protected);
        Assert.False(d.Encrypted);
        Assert.Null(d.Method);
        Assert.Equal(0, d.RecoveryKeys);
        Assert.Equal(2, r.Keys.Count);
        Assert.Equal(("C:", "{A1B2C3D4-E5F6-4789-9ABC-DEF012345678}", K1), (r.Keys[0].Volume, r.Keys[0].ProtectorId, r.Keys[0].Key));
        Assert.Equal(("E:", K3), (r.Keys[1].Volume, r.Keys[1].Key));
        Assert.All(r.Keys, k => Assert.Equal(EncryptionKinds.BitLocker, k.Kind));
    }

    [Fact]
    public void Raw_ConvertTo_Json_with_numeric_enums_and_a_single_object_is_understood()
    {
        var r = BitLockerParse.FromPowerShell(Fixture.Read("get-bitlockervolume-raw.json"));
        var c = Assert.Single(r.Volumes);
        Assert.True(c.System);
        Assert.False(c.Protected);
        Assert.Equal("EncryptionInProgress", c.Status);
        Assert.Equal(57, c.Percent);
        Assert.Equal(K2, Assert.Single(r.Keys).Key);
    }

    [Theory]
    [InlineData("manage-bde-protectors-en.txt", 2)]
    [InlineData("manage-bde-protectors-da.txt", 1)]
    public void Manage_bde_protectors_in_english_and_danish(string file, int count)
    {
        var keys = BitLockerParse.ManageBdeProtectors(Fixture.Read(file), "c:");
        Assert.Equal(count, keys.Count);
        Assert.Equal("{A1B2C3D4-E5F6-4789-9ABC-DEF012345678}", keys[0].ProtectorId);
        Assert.Equal(K1, keys[0].Key);
        Assert.Equal("C:", keys[0].Volume);
        if (count == 2) Assert.Equal(K2, keys[1].Key);
    }

    [Fact]
    public void Manage_bde_status_in_english()
    {
        var v = BitLockerParse.ManageBdeStatus(Fixture.Read("manage-bde-status-en.txt"), "C:")!;
        Assert.Equal("EncryptionInProgress", v.Status);
        Assert.Equal(42.5, v.Percent);
        Assert.False(v.Protected);
        Assert.True(v.Encrypted);
        Assert.Equal("XTS-AES 128", v.Method);
        Assert.Null(BitLockerParse.ManageBdeStatus("Noget helt andet", "C:"));
    }

    [Theory]
    [InlineData("fdesetup-status-on.txt", true, true, "FullyEncrypted", 100.0)]
    [InlineData("fdesetup-status-off.txt", false, false, "FullyDecrypted", 0.0)]
    [InlineData("fdesetup-status-encrypting.txt", true, true, "EncryptionInProgress", 34.71)]
    [InlineData("fdesetup-status-decrypting.txt", false, true, "DecryptionInProgress", 80.0)]
    public void Fdesetup_status(string file, bool prot, bool enc, string status, double pct)
    {
        var v = FileVaultParse.Status(Fixture.Read(file))!;
        Assert.Equal((prot, enc, status, pct), (v.Protected!.Value, v.Encrypted!.Value, v.Status, v.Percent!.Value));
        Assert.True(v.System);
        Assert.Equal("/", v.Mount);
        Assert.Null(FileVaultParse.Status(""));
        Assert.Null(FileVaultParse.Status("fdesetup: command not found"));
    }

    [Fact]
    public void Fdesetup_list_and_new_key()
    {
        Assert.Equal(["anna", "bent"], FileVaultParse.Users(Fixture.Read("fdesetup-list.txt")));
        Assert.Equal("Q7RT-2MXK-9WLP-H4ZD-B6NC-T3JV", FileVaultParse.NewKey(Fixture.Read("fdesetup-changerecovery.plist")));
        Assert.Equal("Q7RT-2MXK-9WLP-H4ZD-B6NC-T3JV", FileVaultParse.NewKey("New personal recovery key = 'Q7RT-2MXK-9WLP-H4ZD-B6NC-T3JV'\n"));
        Assert.Null(FileVaultParse.NewKey("Error: User authentication failed."));
    }

    [Fact]
    public void Fdesetup_input_plist_escapes_the_password_for_stdin()
    {
        var p = FileVaultParse.InputPlist("anna", "a<b&c\"d");
        Assert.Contains("<string>a&lt;b&amp;c&quot;d</string>", p);
        Assert.Contains("<key>Username</key><string>anna</string>", p);
    }

    [Fact]
    public void Lsblk_finds_luks_root_through_lvm_and_closed_containers()
    {
        var (list, rootFound, rootEnc) = LuksParse.Lsblk(Fixture.Read("lsblk-luks.json"));
        Assert.True(rootFound);
        Assert.True(rootEnc);
        Assert.Equal(2, list.Count);
        Assert.Equal(("nvme0n1p3", "dm_crypt-0"), (list[0].Device, list[0].Mapper));
        Assert.Contains("/", list[0].Mounts);
        Assert.Equal("sda1", list[1].Device);
        Assert.Null(list[1].Mapper);   // låst
    }

    [Fact]
    public void Lsblk_without_luks_reports_unencrypted_root()
    {
        var (list, rootFound, rootEnc) = LuksParse.Lsblk(Fixture.Read("lsblk-plain.json"));
        Assert.Empty(list);
        Assert.True(rootFound);
        Assert.False(rootEnc);
    }

    [Fact]
    public void Cryptsetup_status()
    {
        Assert.Equal((true, "LUKS2 aes-xts-plain64"), LuksParse.Status(Fixture.Read("cryptsetup-status.txt")));
        Assert.Equal((false, null), LuksParse.Status(""));
    }

    [Theory]
    [InlineData(K1, true)]
    [InlineData("466895-217492-569250-069608-104434-135707-527241-083623", false)]   // ikke deleligt med 11
    [InlineData("466895-217492-569250-069608-104434-135707-527241", false)]
    [InlineData("999999-217492-569250-069608-104434-135707-527241-083622", false)]   // for stor blok
    public void BitLocker_password_validation(string pw, bool ok) => Assert.Equal(ok, RecoveryKeys.IsBitLockerPassword(pw));

    [Fact]
    public void Hash_ignores_order_and_case_of_ids_but_not_the_key()
    {
        var a = new RecoveryKeyItem { Kind = "bitlocker", Volume = "C:", ProtectorId = "{ABC}", Key = K1 };
        var b = new RecoveryKeyItem { Kind = "bitlocker", Volume = "E:", ProtectorId = "{DEF}", Key = K3 };
        var a2 = new RecoveryKeyItem { Kind = "bitlocker", Volume = "c:", ProtectorId = "{abc}", Key = K1 };
        Assert.Equal(RecoveryKeys.Hash([a, b]), RecoveryKeys.Hash([b, a2]));
        Assert.NotEqual(RecoveryKeys.Hash([a]), RecoveryKeys.Hash([new RecoveryKeyItem { Kind = "bitlocker", Volume = "C:", ProtectorId = "{ABC}", Key = K2 }]));
        Assert.Null(RecoveryKeys.Hash([]));
        Assert.DoesNotContain(K1, a.ToString());
    }

    [Fact]
    public void Scrub_removes_bitlocker_and_filevault_keys_from_job_output()
    {
        var output = Fixture.Read("manage-bde-protectors-en.txt") + "\n" + Fixture.Read("fdesetup-changerecovery.plist") + "\nok 2024-01-01 123-456";
        var clean = RecoveryKeys.Scrub(output);
        Assert.DoesNotContain(K1, clean);
        Assert.DoesNotContain(K2, clean);
        Assert.DoesNotContain("Q7RT-2MXK-9WLP-H4ZD-B6NC-T3JV", clean);
        Assert.Contains(RecoveryKeys.Hidden, clean);
        Assert.Contains("{A1B2C3D4-E5F6-4789-9ABC-DEF012345678}", clean);   // ID'er er ikke hemmelige
        Assert.Contains("5A1B2C3D-4E5F-6A7B-8C9D-0E1F2A3B4C5D", clean);   // en UUID er ikke en nøgle
        Assert.Contains("ok 2024-01-01 123-456", clean);
        Assert.Equal("", RecoveryKeys.Scrub(null));
    }
}

/// <summary>Opbevaring (krypteret, historik, ændringer), visning (admin + bekræftelse + audit + besked) og alarmer.</summary>
public sealed class RecoveryKeyVaultTests : IDisposable
{
    private const string K1 = "466895-217492-569250-069608-104434-135707-527241-083622";
    private const string K2 = "309540-054054-123915-625218-602910-100716-346984-130779";
    private const string FV1 = "Q7RT-2MXK-9WLP-H4ZD-B6NC-T3JV", FV2 = "ABCD-EFGH-JKLM-NPQR-STUV-WX23";
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly IDataProtectionProvider dp = new EphemeralDataProtectionProvider();
    private readonly TestClock clock = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private readonly RecoveryKeyVault vault;
    private readonly ReauthTokens tokens;
    private readonly User admin, viewer;
    private readonly List<string> notes = [];

    public RecoveryKeyVaultTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        db.Machines.Add(new Machine { Id = 1, Name = "STUE-PC", DisplayName = "Stue-pc", Platform = Platforms.Windows });
        db.Machines.Add(new Machine { Id = 2, Name = "MACBOOK", Platform = Platforms.Mac });
        admin = new User { Id = 1, Username = "anna", PasswordHash = Crypto.HashPassword("rigtig-kode"), Role = Roles.Admin };
        viewer = new User { Id = 2, Username = "bent", PasswordHash = Crypto.HashPassword("rigtig-kode"), Role = Roles.Viewer };
        db.Users.AddRange(admin, viewer);
        db.SaveChanges();
        vault = new RecoveryKeyVault(db, dp, clock);
        tokens = new ReauthTokens(dp, clock);
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private static RecoveryKeyItem Bl(string vol, string id, string key) => new() { Kind = EncryptionKinds.BitLocker, Volume = vol, ProtectorId = id, Key = key };
    private static ClaimsPrincipal As(User u) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Name, u.Username), new Claim(ClaimTypes.Role, u.Role)], "test"));
    private Task<RecoveryKeyVault.RevealResult> Reveal(User u, int keyId, string? token, int machineId = 1) =>
        vault.Reveal(As(u), machineId, keyId, token, tokens, new AuditService(db).Log, notes.Add);

    [Fact]
    public async Task Keys_are_encrypted_at_rest_with_data_protection()
    {
        await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{A}", K1)] });
        var row = await db.RecoveryKeys.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(K1, row.Protected);
        Assert.DoesNotContain("466895", row.Protected);
        Assert.Equal(K1, dp.CreateProtector(RecoveryKeyVault.Purpose).Unprotect(row.Protected));
        // og ikke kun i EF's model: selve databasefilen
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_concat(Protected) FROM RecoveryKeys";
        Assert.DoesNotContain(K1, (string)(await cmd.ExecuteScalarAsync())!);
        // listen til brugerfladen har aldrig nøglen
        Assert.DoesNotContain(K1, Json.Ser(await vault.List(1)));
    }

    [Fact]
    public async Task Same_keys_again_only_confirm_and_changed_keys_supersede_with_history()
    {
        var r1 = await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{A}", K1)] });
        Assert.Equal((1, 0, 0), (r1.Added, r1.Confirmed, r1.Superseded));
        clock.Now = clock.Now.AddHours(1);
        var r2 = await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("c:", "{a}", K1)] });
        Assert.Equal((0, 1, 0), (r2.Added, r2.Confirmed, r2.Superseded));
        Assert.Equal(clock.Now.UtcDateTime, (await db.RecoveryKeys.AsNoTracking().SingleAsync()).LastSeen);

        // ny beskytter på C: (den gamle er fjernet på maskinen): den gamle bevares som udskiftet
        var r3 = await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{B}", K2)] });
        Assert.Equal((1, 0, 1), (r3.Added, r3.Confirmed, r3.Superseded));
        var all = await db.RecoveryKeys.AsNoTracking().OrderBy(k => k.Id).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.NotNull(all[0].Superseded);
        Assert.Null(all[1].Superseded);

        // samme beskytter, ny adgangskode
        var r4 = await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{B}", K1)] });
        Assert.Equal((1, 1), (r4.Added, r4.Superseded));
        Assert.Equal(3, await db.RecoveryKeys.CountAsync());
        Assert.Equal(1, await db.RecoveryKeys.CountAsync(k => k.Superseded == null));
    }

    [Fact]
    public async Task Server_hash_matches_agent_hash_so_unchanged_keys_are_not_requested()
    {
        var keys = new List<RecoveryKeyItem> { Bl("C:", "{A}", K1), Bl("D:", "{B}", K2) };
        Assert.NotEqual(RecoveryKeys.Hash(keys), await vault.CurrentHash(1, EncryptionKinds.BitLocker));
        await vault.Store(1, new RecoveryKeyPost { Keys = keys });
        Assert.Equal(RecoveryKeys.Hash(keys), await vault.CurrentHash(1, EncryptionKinds.BitLocker));
        Assert.Null(await vault.CurrentHash(2, EncryptionKinds.BitLocker));
    }

    [Fact]
    public async Task Invalid_keys_are_rejected_and_never_supersede_good_ones()
    {
        await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{A}", K1)] });
        var r = await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{A}", "123"), new() { Kind = "luks", Volume = "/", ProtectorId = "x", Key = "hemmelig" }] });
        Assert.Equal((0, 0, 2), (r.Added, r.Superseded, r.Rejected));
        Assert.Equal(1, await db.RecoveryKeys.CountAsync(k => k.Superseded == null));
    }

    [Fact]
    public async Task New_filevault_key_from_rotation_supersedes_the_old_one()
    {
        await vault.Store(2, new RecoveryKeyPost { Full = false, JobId = 7, Keys = [new() { Kind = EncryptionKinds.FileVault, Volume = "/", ProtectorId = "personal", Key = FV1 }] });
        await vault.Store(2, new RecoveryKeyPost { Full = false, JobId = 8, Keys = [new() { Kind = EncryptionKinds.FileVault, Volume = "/", ProtectorId = "personal", Key = FV2 }] });
        var rows = await db.RecoveryKeys.AsNoTracking().Where(k => k.MachineId == 2).OrderBy(k => k.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].Superseded);
        Assert.Equal(("rotate", 8), (rows[1].Source, rows[1].JobId));
    }

    [Fact]
    public async Task Reveal_requires_admin_and_is_audited()
    {
        await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{A}", K1)] });
        var id = (await db.RecoveryKeys.SingleAsync()).Id;
        var r = await Reveal(viewer, id, tokens.Issue(viewer).Token);
        Assert.Equal(403, r.Status);
        Assert.Null(r.Key);
        var a = await db.Audit.AsNoTracking().SingleAsync();
        Assert.Equal(("bent", RecoveryKeyVault.AuditDenied, 1), (a.User, a.Action, a.MachineId));
        Assert.Empty(notes);
    }

    [Fact]
    public async Task Reveal_requires_a_reauth_within_five_minutes_bound_to_the_user()
    {
        await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{A}", K1)] });
        var id = (await db.RecoveryKeys.SingleAsync()).Id;

        Assert.Equal(428, (await Reveal(admin, id, null)).Status);
        Assert.True((await Reveal(admin, id, "noget-forfalsket")).NeedsReauth);
        Assert.Equal(428, (await Reveal(admin, id, tokens.Issue(viewer).Token)).Status);   // en andens bevis

        var (token, expires) = tokens.Issue(admin);
        Assert.Equal(clock.Now.AddMinutes(5).UtcDateTime, expires);
        clock.Now = clock.Now.AddMinutes(5).AddSeconds(1);
        Assert.Equal(428, (await Reveal(admin, id, token)).Status);   // for gammelt

        token = tokens.Issue(admin).Token;
        admin.SecurityStamp = Guid.NewGuid().ToString("N");          // ny adgangskode/logget ud overalt
        db.Users.Update(admin);
        await db.SaveChangesAsync();
        Assert.Equal(428, (await Reveal(admin, id, token)).Status);

        Assert.Empty(notes);
        Assert.All(await db.Audit.AsNoTracking().ToListAsync(), x => Assert.Equal(RecoveryKeyVault.AuditDenied, x.Action));
    }

    [Fact]
    public async Task Successful_reveal_returns_the_key_audits_and_notifies()
    {
        await vault.Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{A}", K1)] });
        var id = (await db.RecoveryKeys.SingleAsync()).Id;
        clock.Now = clock.Now.AddMinutes(1);
        var token = tokens.Issue(admin).Token;
        clock.Now = clock.Now.AddMinutes(4);
        var r = await Reveal(admin, id, token);
        Assert.Equal(200, r.Status);
        Assert.Equal(K1, r.Key);
        var a = await db.Audit.AsNoTracking().SingleAsync();
        Assert.Equal(("anna", RecoveryKeyVault.AuditReveal, 1), (a.User, a.Action, a.MachineId));
        Assert.Contains("Stue-pc", a.Detail);
        Assert.DoesNotContain(K1, a.Detail);
        var n = Assert.Single(notes);
        Assert.StartsWith("🔑 Gendannelsesnøgle vist for Stue-pc af anna", n);
        Assert.DoesNotContain(K1, n);
        // en nøgle fra en anden maskine kan ikke hentes via denne maskine
        Assert.Equal(404, (await Reveal(admin, id, token, machineId: 2)).Status);
    }

    [Fact]
    public void Alerts_unencrypted_laptop_is_info_and_missing_escrow_is_warning()
    {
        var laptop = new EncryptionInfo { Kind = EncryptionKinds.BitLocker, Portable = true, Volumes = [new() { Mount = "C:", System = true, Protected = false, Encrypted = false, Status = "FullyDecrypted" }] };
        var f = Assert.Single(RecoveryKeyVault.Findings(laptop, []));
        Assert.Equal(("encryption", "info"), (f.Key, f.Severity));
        laptop.Portable = false;
        Assert.Empty(RecoveryKeyVault.Findings(laptop, []));   // stationær: ingen besked

        var on = new EncryptionInfo
        {
            Kind = EncryptionKinds.BitLocker, Portable = true,
            Volumes = [new() { Mount = "C:", System = true, Protected = true, RecoveryKeys = 1 }, new() { Mount = "D:", Protected = true, RecoveryKeys = 0 }],
        };
        var w = Assert.Single(RecoveryKeyVault.Findings(on, []));
        Assert.Equal(("escrow", "warning"), (w.Key, w.Severity));
        Assert.Contains("C:, D:", w.Detail);
        Assert.Contains("ingen numerisk", w.Detail);
        Assert.Contains("D:", Assert.Single(RecoveryKeyVault.Findings(on, [(EncryptionKinds.BitLocker, "c:")])).Detail);
        Assert.Empty(RecoveryKeyVault.Findings(on, [(EncryptionKinds.BitLocker, "C:"), (EncryptionKinds.BitLocker, "D:")]));

        var mac = new EncryptionInfo { Kind = EncryptionKinds.FileVault, Portable = true, Volumes = [new() { Mount = "/", System = true, Protected = true }] };
        Assert.Equal("escrow", Assert.Single(RecoveryKeyVault.Findings(mac, [])).Key);
        Assert.Empty(RecoveryKeyVault.Findings(mac, [(EncryptionKinds.FileVault, "/")]));
        // LUKS: kun status, ingen nøgle at gemme
        var luks = new EncryptionInfo { Kind = EncryptionKinds.Luks, Portable = true, Volumes = [new() { Mount = "/", System = true, Protected = true }] };
        Assert.Empty(RecoveryKeyVault.Findings(luks, []));
        Assert.NotNull(HelperCatalog.AdviceFor("encryption", "info", Platforms.Windows));
        Assert.NotNull(HelperCatalog.AdviceFor("escrow", "warning", Platforms.Mac));
    }

    [Fact]
    public async Task Self_backup_contains_the_keys_encrypted_and_restores_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fd-escrow-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var keysDir = new DirectoryInfo(Path.Combine(dir, "keys"));
            var provider = DataProtectionProvider.Create(keysDir, b => b.SetApplicationName("Fleetdeck"));
            await using (var fdb = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(dir, SelfBackup.DbFile)}").Options))
            {
                await fdb.Database.MigrateAsync();
                fdb.Machines.Add(new Machine { Id = 1, Name = "PC" });
                await fdb.SaveChangesAsync();
                await new RecoveryKeyVault(fdb, provider, TimeProvider.System).Store(1, new RecoveryKeyPost { Keys = [Bl("C:", "{A}", K1)] });
            }
            SqliteConnection.ClearAllPools();
            var file = Path.Combine(dir, "backups", SelfBackup.FileName("manual", DateTime.UtcNow, "9.9.9"));
            SelfBackup.Create(dir, file, BackupSecret.FromPassword("korrekt-hest-batteri"), "manual", new(), "9.9.9");
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.Equal(-1, IndexOf(bytes, Encoding.UTF8.GetBytes(K1)));
            Assert.Equal(-1, IndexOf(bytes, Encoding.UTF8.GetBytes("RecoveryKeys")));   // selv tabelnavnet er krypteret

            var outDir = Path.Combine(dir, "x");
            SelfBackup.Extract(file, BackupSecret.FromPassword("korrekt-hest-batteri"), outDir);
            var restored = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(outDir, "keys")), b => b.SetApplicationName("Fleetdeck"));
            await using var rdb = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={Path.Combine(outDir, SelfBackup.DbFile)}").Options);
            var row = await rdb.RecoveryKeys.SingleAsync();
            Assert.Equal(K1, restored.CreateProtector(RecoveryKeyVault.Purpose).Unprotect(row.Protected));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (var i = 0; i <= hay.Length - needle.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Fact]
    public void Reveal_and_rotate_endpoints_require_admin_and_status_requires_login()
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=:memory:"));
        b.Services.AddSignalR();
        b.Services.AddHttpClient();
        b.Services.AddDataProtection();
        b.Services.AddSingleton(TimeProvider.System);
        b.Services.AddSingleton<Live>();
        b.Services.AddSingleton<Notifier>();
        b.Services.AddSingleton<TwoFactorService>();
        b.Services.AddSingleton<ReauthTokens>();
        b.Services.AddScoped<RecoveryKeyVault>();
        b.Services.AddScoped<JobService>();
        b.Services.AddScoped<AuditService>();
        b.Services.AddAuthorization();
        var app = b.Build();
        app.MapEncryptionEndpoints();
        var eps = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToDictionary(e => e.RoutePattern.RawText!);
        string? Policy(string route) => eps[route].Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).LastOrDefault();
        Assert.Equal("admin", Policy("/api/machines/{id:int}/recovery-keys/{keyId:int}/reveal"));
        Assert.Equal("admin", Policy("/api/machines/{id:int}/encryption/rotate"));
        Assert.Equal("user", Policy("/api/machines/{id:int}/encryption"));
        Assert.Equal("any", Policy("/api/account/reauth"));
        Assert.All(eps.Values, e => Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>()));
    }
}
