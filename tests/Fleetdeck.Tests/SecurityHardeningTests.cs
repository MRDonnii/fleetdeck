using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Fleetdeck.Agent.Windows;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using WinAccess = Fleetdeck.Agent.Windows.Access;

namespace Fleetdeck.Tests;

/// <summary>
/// Windows: ingen almindelig bruger kan ændre noget, agenten kører med SYSTEM- eller administratorrettigheder (userwork-hullet),
/// og agenten nægter at køre fra en placering, de kan ændre. Reglerne testes med en falsk ACL-læser, så de kører på alle platforme.
/// </summary>
public sealed class WindowsHardeningTests
{
    private const string AuthUsers = "S-1-5-11";
    private const string Bob = "S-1-5-21-1000-1000-1000-1001";

    /// <summary>Et lille filsystem med ACL'er, som på en standard-Windows.</summary>
    private sealed class FakeFs : ISecurityReader
    {
        public readonly Dictionary<string, PathSecurity> Paths = new(StringComparer.OrdinalIgnoreCase);
        public bool Throw;
        public PathSecurity? Read(string path) => Throw ? throw new UnauthorizedAccessException("nægtet") : Paths.GetValueOrDefault(path);
        public FakeFs Dir(string p, string owner, params AceInfo[] aces) { Paths[p] = new(owner, false, true, aces); return this; }
        public FakeFs File(string p, string owner, params AceInfo[] aces) { Paths[p] = new(owner, false, false, aces); return this; }

        /// <summary>En mappe eller fil med ACL'en fra en <see cref="DirAcl"/> plus det, den arver fra mappen over.</summary>
        public FakeFs Planned(DirAcl a, bool isFile = false)
        {
            var inherited = a.Protected ? [] : Paths[WinPath.Parent(a.Path)!].Aces.Where(x => x.Mask != WinAccess.WriteData || x.Sid != Sids.Users).ToList();
            var own = a.Aces.Where(x => !isFile || x.Scope != AceScope.ThisOnly)
                .Select(x => new AceInfo(x.Sid, x.Mask, InheritOnly: !isFile && x.Scope == AceScope.FilesOnly));
            Paths[a.Path] = new(a.OwnerSid, false, !isFile, [.. inherited, .. own]);
            return this;
        }
    }

    private static AceInfo Full(string sid) => new(sid, WinAccess.FullControl);
    private static AceInfo Rx(string sid) => new(sid, WinAccess.ReadAndExecute);

    /// <summary>C:\ og C:\ProgramData som på en ny Windows 11, og Fleetdecks mapper som efter <see cref="Hardening.Apply"/>.</summary>
    private static FakeFs Windows()
    {
        var fs = new FakeFs()
            .Dir(@"C:\", Sids.TrustedInstaller, Full(Sids.System), Full(Sids.Administrators), Rx(Sids.Users),
                new AceInfo(AuthUsers, WinAccess.AppendData), new AceInfo(AuthUsers, WinAccess.Modify, InheritOnly: true))
            .Dir(@"C:\ProgramData", Sids.System, Full(Sids.System), Full(Sids.Administrators), new AceInfo(Sids.CreatorOwner, WinAccess.FullControl, InheritOnly: true),
                Rx(Sids.Users), new AceInfo(Sids.Users, WinAccess.WriteData | WinAccess.AppendData | WinAccess.WriteEa | WinAccess.WriteAttributes))
            .Dir(@"C:\Program Files", Sids.TrustedInstaller, Full(Sids.TrustedInstaller), Full(Sids.System), Full(Sids.Administrators), Rx(Sids.Users));
        fs.Planned(Hardening.InstallDir(@"C:\Program Files\Fleetdeck"));
        fs.Planned(Hardening.DataDir(@"C:\ProgramData\Fleetdeck"));
        fs.Planned(Hardening.Inherit(@"C:\ProgramData\Fleetdeck\work"));
        fs.Planned(Hardening.UserWork(@"C:\ProgramData\Fleetdeck\userwork"));
        fs.Planned(Hardening.Dropbox(@"C:\ProgramData\Fleetdeck\userwork\cmd"));
        fs.Planned(Hardening.Dropbox(@"C:\ProgramData\Fleetdeck\userwork\out"));
        return fs;
    }

    [Fact]
    public void Scripts_and_installers_in_the_protected_work_folder_may_run()
    {
        var fs = Windows();
        fs.File(@"C:\ProgramData\Fleetdeck\work\a.ps1", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators));
        Assert.Null(ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\a.ps1", fs));
        fs.File(@"C:\Program Files\Fleetdeck\Fleetdeck.exe", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators), Rx(Sids.Users));
        Assert.Null(ExecGuard.Check(@"C:\Program Files\Fleetdeck\Fleetdeck.exe", fs));
        Assert.Null(ExecGuard.CheckFolder(@"C:\Program Files\Fleetdeck", fs));
    }

    [Fact]
    public void A_run_folder_for_the_user_session_is_readable_by_that_user_only_and_writable_by_nobody_else()
    {
        var fs = Windows();
        var dir = @"C:\ProgramData\Fleetdeck\work\u-0123";
        fs.Planned(Hardening.UserRun(dir, Bob));
        fs.Planned(Hardening.UserRun(dir + @"\setup.exe", Bob), isFile: true);
        Assert.Null(ExecGuard.Check(dir + @"\setup.exe", fs));
        Assert.Contains(fs.Paths[dir].Aces, a => a.Sid == Bob && a.Mask == WinAccess.ReadAndExecute);
        Assert.DoesNotContain(fs.Paths[dir].Aces, a => a.Sid == Sids.Users);
    }

    [Fact]
    public void The_old_userwork_acl_with_users_modify_is_refused()
    {
        // sådan så userwork ud før: Users: Modify (nedarvet) – en bruger kunne bytte scriptet, før det kørte
        var fs = Windows()
            .Dir(@"C:\ProgramData\Fleetdeck\userwork", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators), new AceInfo(Sids.Users, WinAccess.Modify))
            .File(@"C:\ProgramData\Fleetdeck\userwork\job.ps1", Sids.System, Full(Sids.System), new AceInfo(Sids.Users, WinAccess.Modify));
        var why = ExecGuard.Check(@"C:\ProgramData\Fleetdeck\userwork\job.ps1", fs);
        Assert.NotNull(why);
        Assert.Contains(Sids.Users, why);
        Assert.True(Hardening.NeedsRepair(@"C:\ProgramData\Fleetdeck\userwork", fs));
    }

    [Fact]
    public void Nothing_is_ever_run_from_the_users_dropboxes()
    {
        var fs = Windows();
        fs.File(@"C:\ProgramData\Fleetdeck\userwork\cmd\x.exe", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators));
        // filen selv er beskyttet, men brugere kan oprette filer i mappen (fx en DLL ved siden af)
        Assert.Contains("kan ændres af " + Sids.Users, ExecGuard.Check(@"C:\ProgramData\Fleetdeck\userwork\cmd\x.exe", fs));
    }

    [Fact]
    public void Userwork_lets_users_read_and_drop_files_but_never_create_folders_or_delete_others_files()
    {
        var uw = Hardening.UserWork(@"C:\x\userwork");
        Assert.True(uw.Protected);
        Assert.Equal(Sids.Administrators, uw.OwnerSid);
        var users = Assert.Single(uw.Aces, a => a.Sid == Sids.Users);
        Assert.Equal(0, users.Mask & WinAccess.ChangesFolder);
        var box = Hardening.Dropbox(@"C:\x\userwork\cmd");
        var u = Assert.Single(box.Aces, a => a.Sid == Sids.Users);
        Assert.Equal(WinAccess.WriteData, u.Mask);                       // opret filer – ikke undermapper (ingen junctions)
        Assert.Equal(AceScope.ThisOnly, u.Scope);
        Assert.Equal(0, u.Mask & (WinAccess.AppendData | WinAccess.DeleteChild | WinAccess.WriteDac | WinAccess.WriteOwner));
        Assert.Equal(AceScope.FilesOnly, Assert.Single(box.Aces, a => a.Sid == Sids.CreatorOwner).Scope);   // kun egne filer
        var data = Hardening.DataDir(@"C:\x");
        Assert.True(data.Protected);
        Assert.All(data.Aces, a => Assert.True(Sids.Trusted(a.Sid)));
        Assert.DoesNotContain(Hardening.InstallDir(@"C:\p").Aces, a => !Sids.Trusted(a.Sid) && (a.Mask & WinAccess.ChangesFolder) != 0);
    }

    [Fact]
    public void A_folder_users_can_create_files_in_is_refused_dll_planting()
    {
        var fs = Windows()
            .Dir(@"C:\ProgramData\Vendor", Sids.System, Full(Sids.System), Full(Sids.Administrators), new AceInfo(Sids.Users, WinAccess.WriteData | WinAccess.AppendData))
            .File(@"C:\ProgramData\Vendor\uninstall.exe", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators), Rx(Sids.Users));
        Assert.Contains("C:\\ProgramData\\Vendor kan ændres af", ExecGuard.Check(@"C:\ProgramData\Vendor\uninstall.exe", fs));
    }

    [Fact]
    public void A_folder_higher_up_that_users_can_rename_or_take_over_is_refused()
    {
        var fs = Windows()
            .Dir(@"C:\Tools", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators), new AceInfo(Sids.Users, WinAccess.DeleteChild))
            .Dir(@"C:\Tools\Bin", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators))
            .File(@"C:\Tools\Bin\x.exe", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators));
        Assert.Contains(@"C:\Tools kan ændres af", ExecGuard.Check(@"C:\Tools\Bin\x.exe", fs));
        fs.Dir(@"C:\Tools", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators), new AceInfo(Sids.Users, WinAccess.WriteDac));
        Assert.NotNull(ExecGuard.Check(@"C:\Tools\Bin\x.exe", fs));
        // at kunne oprette nye mapper længere oppe (som på C:\) er ikke farligt
        fs.Dir(@"C:\Tools", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators), new AceInfo(Sids.Users, WinAccess.AppendData | WinAccess.WriteData));
        Assert.Null(ExecGuard.Check(@"C:\Tools\Bin\x.exe", fs));
    }

    [Fact]
    public void Writable_file_untrusted_owner_and_reparse_points_are_refused()
    {
        var fs = Windows();
        fs.File(@"C:\ProgramData\Fleetdeck\work\a.exe", Sids.Administrators, Full(Sids.System), new AceInfo(Bob, WinAccess.WriteData));
        Assert.Contains(Bob, ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\a.exe", fs));
        fs.File(@"C:\ProgramData\Fleetdeck\work\a.exe", Bob, Full(Sids.System));                     // ejeren kan altid ændre adgangen
        Assert.Contains("ejes af " + Bob, ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\a.exe", fs));
        fs.Paths[@"C:\ProgramData\Fleetdeck\work\a.exe"] = new(Sids.Administrators, true, false, [Full(Sids.System)]);
        Assert.Contains("genvej", ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\a.exe", fs));
        fs.File(@"C:\ProgramData\Fleetdeck\work\a.exe", Sids.Administrators, Full(Sids.System));
        fs.Paths[@"C:\ProgramData\Fleetdeck\work"] = fs.Paths[@"C:\ProgramData\Fleetdeck\work"] with { IsReparsePoint = true };   // junction på vejen
        Assert.Contains("genvej", ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\a.exe", fs));
    }

    [Fact]
    public void Inherit_only_creator_owner_deny_and_service_sids_do_not_count_as_user_write()
    {
        var fs = Windows();
        const string appx = "S-1-5-80-3153452013-3049546193-1009395018-2186212924-1385468346";   // en tjeneste-SID (NT SERVICE\…)
        fs.Dir(@"C:\ProgramData\Fleetdeck\work", Sids.Administrators, Full(Sids.System), Full(Sids.Administrators),
            new AceInfo(Sids.Users, WinAccess.Modify, InheritOnly: true), new AceInfo(Sids.CreatorOwner, WinAccess.FullControl),
            new AceInfo(Sids.Users, WinAccess.Modify, Allow: false), Full(appx));
        fs.File(@"C:\ProgramData\Fleetdeck\work\a.exe", appx, Full(Sids.System));
        Assert.Null(ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\a.exe", fs));
    }

    [Fact]
    public void Unknown_or_unreadable_paths_are_refused_fail_closed()
    {
        var fs = Windows();
        Assert.Contains("findes ikke", ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\mangler.exe", fs));
        Assert.NotNull(ExecGuard.Check(@"\\nas\share\setup.exe", fs));
        Assert.NotNull(ExecGuard.Check(@"setup.exe", fs));
        Assert.NotNull(ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\..\userwork\x.exe", fs));
        Assert.NotNull(ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\a.exe:stream", fs));
        Assert.Equal(@"C:\Program Files\Fleetdeck", WinPath.Normalize(@"C:/Program Files//Fleetdeck\"));
        fs.Throw = true;
        Assert.Contains("kunne ikke læses", ExecGuard.Check(@"C:\ProgramData\Fleetdeck\work\a.ps1", fs));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\uninst.exe\" /S", "C:\\Program Files\\App\\uninst.exe")]
    [InlineData("C:\\Program Files\\App\\uninst.exe /S", "C:\\Program Files\\App\\uninst.exe")]
    [InlineData("C:\\Users\\bob\\AppData\\Local\\App\\Update.exe --uninstall", "C:\\Users\\bob\\AppData\\Local\\App\\Update.exe")]
    [InlineData("MsiExec.exe /X{1234}", null)]
    [InlineData("", null)]
    public void The_program_in_an_uninstall_string_is_found(string cmd, string? exe) => Assert.Equal(exe, ExecGuard.ExeOfCommandLine(cmd));

    [Fact]
    public void Leftovers_in_userwork_are_removed_and_user_owned_or_reparse_entries_never_kept()
    {
        PathSecurity Sys(bool dir) => new(Sids.System, false, dir, []);
        var del = Hardening.UserWorkLeftovers([
            ("status.json", Sys(false)), ("helpdesk.json", Sys(false)), ("cmd", Sys(true)), ("out", Sys(true)),
            ("0123abcd.ps1", Sys(false)), ("setup.exe", Sys(false)), ("old.log", new(Bob, false, false, [])),
            ("STATUS.JSON", new PathSecurity(Bob, false, false, [])),
            ("junction", new(Bob, true, true, [])), ("cmd2", Sys(true)),
        ]);
        Assert.Equal(["0123abcd.ps1", "setup.exe", "old.log", "STATUS.JSON", "junction", "cmd2"], del);
        Assert.Contains("cmd", Hardening.UserWorkLeftovers([("cmd", new(Sids.Administrators, true, true, []))]));   // cmd som junction
        Assert.Contains("cmd", Hardening.UserWorkLeftovers([("cmd", Sys(false))]));                                 // cmd som fil
    }

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine([Root(), "src", .. parts]));
    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Fleetdeck.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Fleetdeck.slnx");
    }

    [Fact]
    public void The_agent_no_longer_gives_users_modify_or_writes_runnable_files_to_userwork()
    {
        var tray = Src("Fleetdeck.Agent.Windows", "TrayBridge.cs");
        var installer = Src("Fleetdeck.Agent.Windows", "Installer.cs");
        foreach (var s in new[] { tray, installer })
            Assert.DoesNotContain("FileSystemRights.Modify", s);
        Assert.Contains("Hardening.EnsureDropboxes", tray);
        Assert.Contains("Hardening.Apply", installer);
        Assert.Contains("Hardening.Apply", Src("Fleetdeck.Agent.Windows", "Program.cs"));   // selvreparation ved hver start
        var runner = Src("Fleetdeck.Agent.Windows", "Infrastructure.cs");
        var ps = runner[runner.IndexOf("public async Task<RunResult> PowerShell", StringComparison.Ordinal)..];
        ps = ps[..ps.IndexOf("public RunResult? Refused", StringComparison.Ordinal)];
        Assert.DoesNotContain("UserWork", ps);
        Assert.Contains("Refused(file)", ps);
        var jobs = Src("Fleetdeck.Agent.Windows", "Jobs.cs");
        Assert.DoesNotContain("opt.UserWork", jobs);
        Assert.Contains("Hardening.NewRunDir", jobs);
    }
}

/// <summary>Andre rettelser fra sikkerhedsgennemgangen (agenter og server).</summary>
public sealed class SecurityFixesTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly AppDb _db;

    public SecurityFixesTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }
    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Theory]
    [InlineData("570", true)]
    [InlineData("570\" & calc & \"", false)]
    [InlineData("٣", false)]
    [InlineData("", false)]
    public void Steam_ids_are_digits_only(string id, bool ok) => Assert.Equal(ok, PackageIds.SteamId(id));

    [Theory]
    [InlineData("ms-windows-store://pdp/?productid=9NBLGGH4NNS1", true)]
    [InlineData("https://example.com/a?b=1&c=%20", true)]                 // & og % er harmløse inden i anførselstegnene
    [InlineData("https://x\" & calc & \"", false)]
    [InlineData("https://x y", false)]
    [InlineData("https://x\r\ncalc", false)]
    [InlineData("calc.exe", false)]
    public void Uris_cannot_break_out_of_the_quotes(string uri, bool ok) => Assert.Equal(ok, PackageIds.SafeUri(uri));

    [Theory]
    [InlineData("ChromeSetup.exe", "ChromeSetup.exe")]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("tool.msi", "tool.msi")]
    [InlineData("x&y.msi", "setup.msi")]
    [InlineData(null, "setup.exe")]
    public void Installer_file_names_are_plain(string? id, string name) => Assert.Equal(name, PackageIds.FileName(id));

    [Fact]
    public void Self_update_is_refused_without_a_pinned_key()
    {
        var f = Path.GetTempFileName();
        try
        {
            var r = AgentSigning.CheckPinned(f, null, null, null);
            Assert.False(r.Ok);
            Assert.Contains("ikke husket", r.Why);
            Assert.False(AgentSigning.CheckPinned(f, "", "abc", "def").Ok);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Root_scripts_on_a_mac_look_in_the_system_folders_first()
    {
        var mac = UnixRoot.RootPath(mac: true).Split(':');
        Assert.True(Array.IndexOf(mac, "/usr/bin") < Array.IndexOf(mac, "/usr/local/bin"));
        Assert.True(Array.IndexOf(mac, "/bin") < Array.IndexOf(mac, "/usr/local/bin"));
        Assert.Equal("/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/snap/bin", UnixRoot.RootPath(mac: false));   // Linux uændret
    }

    [Fact]
    public void Unix_work_folders_are_root_owned_and_never_in_var_tmp()
    {
        foreach (var d in new[] { UnixRoot.KitWork, UnixRoot.IosWork, SpareCard.Work })
        {
            Assert.StartsWith("/var/lib/fleetdeck/", d);
            Assert.NotNull(UnixRoot.Relative(d));
        }
        Assert.Null(UnixRoot.Relative("/var/tmp/fleetdeck-kit"));
        Assert.Null(UnixRoot.Relative("/var/lib/fleetdeck/../../tmp/x"));
        Assert.Contains(UnixRoot.KitWork, SpareCard.Exclude);          // kopieres aldrig med til et reservekort
        Assert.Contains(SpareCard.Work, SpareCard.Exclude);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleetdeck.slnx"))) root = root.Parent;
        foreach (var f in new[] { "RecoveryKit.cs", "Ios.cs", "Jobs.cs", "SpareCardAgent.cs" })
        {
            var src = File.ReadAllText(Path.Combine(root!.FullName, "src", "Fleetdeck.Agent.Unix", f));
            Assert.DoesNotContain("\"/var/tmp/fleetdeck", src);
            Assert.DoesNotContain("W=/var/tmp", src);
        }
        Assert.Contains("umask 077", File.ReadAllText(Path.Combine(root!.FullName, "src", "Fleetdeck.Agent.Unix", "RecoveryKit.cs")));
    }

    [Fact]
    public void A_symlink_where_a_root_folder_should_be_is_removed_not_followed()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var t = Path.Combine(Path.GetTempPath(), "fd-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(t);
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(t, "target"));
            File.WriteAllText(Path.Combine(target.FullName, "keep.txt"), "x");
            var link = Path.Combine(t, "kit");
            Directory.CreateSymbolicLink(link, target.FullName);
            UnixRoot.RemoveIfNotPlainDir(link);
            Assert.False(Path.Exists(link));
            Assert.True(File.Exists(Path.Combine(target.FullName, "keep.txt")));   // det, genvejen pegede på, er urørt
            var file = Path.Combine(t, "ios");
            File.WriteAllText(file, "x");
            UnixRoot.RemoveIfNotPlainDir(file);
            Assert.False(File.Exists(file));
            var plain = Directory.CreateDirectory(Path.Combine(t, "plain"));
            UnixRoot.RemoveIfNotPlainDir(plain.FullName);
            Assert.True(plain.Exists);
        }
        finally { Directory.Delete(t, true); }
    }

    [Fact]
    public async Task The_last_administrator_can_neither_be_deleted_nor_demoted()
    {
        var a = new User { Username = "ejer", Role = Roles.Admin };
        var b = new User { Username = "anden", Role = Roles.Admin };
        var v = new User { Username = "laeser", Role = Roles.Viewer };
        _db.Users.AddRange(a, b, v);
        await _db.SaveChangesAsync();
        Assert.False(await LastAdmin.WouldRemove(_db, b, null));          // der er en anden administrator
        Assert.False(await LastAdmin.WouldRemove(_db, v, null));
        _db.Users.Remove(b);
        await _db.SaveChangesAsync();
        Assert.True(await LastAdmin.WouldRemove(_db, a, null));
        Assert.True(await LastAdmin.WouldRemove(_db, a, Roles.Viewer));
        Assert.False(await LastAdmin.WouldRemove(_db, a, Roles.Admin));
        Assert.False(await LastAdmin.WouldRemove(_db, v, Roles.Admin));
    }

    [Fact]
    public void Parents_only_get_ipad_and_job_changes_and_never_job_progress()
    {
        ClaimsPrincipal P(string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
        Assert.Equal([UiHub.Staff], UiHub.GroupsFor(P(Roles.Admin)));
        Assert.Equal([UiHub.Staff], UiHub.GroupsFor(P(Roles.Viewer)));
        Assert.Equal([UiHub.Family], UiHub.GroupsFor(P(Roles.Parent)));
        Assert.Empty(UiHub.GroupsFor(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal([UiHub.Staff], UiHub.GroupsForKind("machines"));
        Assert.Contains(UiHub.Family, UiHub.GroupsForKind("ipads"));
        var core = File.ReadAllText(Path.Combine(Root(), "src", "Fleetdeck.Server", "Services", "Core.cs"));
        Assert.DoesNotContain("ui.Clients.All", core);
        Assert.Contains("Clients.Group(UiHub.Staff).SendAsync(UiHubMethods.Progress", core);
    }

    [Fact]
    public void Readers_do_not_see_webhook_urls_or_push_tokens_and_sessions_last_a_week()
    {
        Assert.Contains("webhook_url", SettingsService.AdminOnly);
        Assert.All(SettingsService.AdminOnly, k => Assert.Contains(k, SettingsService.Public));
        Assert.Equal(TimeSpan.FromDays(7), SessionLifetime.Days);
        var checks = File.ReadAllText(Path.Combine(Root(), "src", "Fleetdeck.Server", "Endpoints", "CheckEndpoints.cs"));
        Assert.Contains("c.Type == CheckTypes.Push && admin ?", checks);
    }

    [Fact]
    public void Unauthenticated_audit_rows_are_capped()
    {
        UnauthenticatedAuditLimit.Reset();
        var t = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < UnauthenticatedAuditLimit.PerWindow; i++) Assert.True(UnauthenticatedAuditLimit.Allow(t.AddSeconds(i)));
        Assert.False(UnauthenticatedAuditLimit.Allow(t.AddMinutes(5)));
        Assert.True(UnauthenticatedAuditLimit.Allow(t + UnauthenticatedAuditLimit.Window));   // nyt vindue
        UnauthenticatedAuditLimit.Reset();
    }

    [Fact]
    public async Task Amt_passwords_are_scrubbed_from_cancelled_and_expired_jobs_but_kept_while_pending()
    {
        _db.Machines.Add(new Machine { Id = 1, Name = "PC1" });
        Job J(string type, string status) => new() { MachineId = 1, Type = type, Status = status, Title = "t", ParamsJson = "{\"text\":\"hemmelig\"}" };
        _db.Jobs.AddRange(J(JobTypes.AmtActivate, JobStatus.Cancelled), J(JobTypes.AmtActivate, JobStatus.Expired),
            J(JobTypes.AmtActivate, JobStatus.Pending), J(JobTypes.Script, JobStatus.Cancelled));
        await _db.SaveChangesAsync();
        Assert.Equal(2, await AmtService.ScrubFinishedJobs(_db));
        var jobs = await _db.Jobs.AsNoTracking().OrderBy(j => j.Id).Select(j => j.ParamsJson).ToListAsync();
        Assert.Equal(["{}", "{}", "{\"text\":\"hemmelig\"}", "{\"text\":\"hemmelig\"}"], jobs);
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Fleetdeck.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Fleetdeck.slnx");
    }
}
