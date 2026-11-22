using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using static Fleetdeck.Shared.MacKeepAwake;

namespace Fleetdeck.Tests;

/// <summary>"Hold vågen på strøm" på Mac: tilstandsmaskinen, ejerskab (markøren), vagten, kommandoer og to-vejs synkronisering.</summary>
public class MacKeepAwakeTests
{
    // ---------------------------------------------------------------- tilstandsmaskine: strøm/batteri × indstilling × markør × disablesleep

    [Theory]
    // indstilling til, på strøm
    [InlineData(true, true, false, false, Act.Enable, States.Held)]     // vi sætter den og skriver markøren
    [InlineData(true, true, false, true, Act.None, States.User)]        // brugeren havde selv sat den: rør den ikke
    [InlineData(true, true, false, null, Act.None, States.Error)]       // kan ikke se det: gør intet
    [InlineData(true, true, true, true, Act.None, States.Held)]         // allerede vores
    [InlineData(true, true, true, false, Act.Enable, States.Held)]      // vores, men væk: sæt den igen
    [InlineData(true, true, true, null, Act.Enable, States.Held)]
    // indstilling til, på batteri: altid dvale
    [InlineData(true, false, true, true, Act.Disable, States.Battery)]
    [InlineData(true, false, true, null, Act.Disable, States.Battery)]
    [InlineData(true, false, false, false, Act.None, States.Battery)]
    [InlineData(true, false, false, true, Act.None, States.User)]       // brugerens egen pmset: ikke vores at ændre
    // indstilling fra
    [InlineData(false, true, true, true, Act.Disable, States.Off)]
    [InlineData(false, false, true, true, Act.Disable, States.Off)]
    [InlineData(false, true, false, false, Act.None, States.Off)]
    [InlineData(false, true, false, true, Act.None, States.User)]
    [InlineData(false, false, false, null, Act.None, States.Off)]
    public void Decide_matrix(bool setting, bool onAc, bool marker, bool? sleepDisabled, Act act, string state)
    {
        var d = Decide(setting, onAc, marker, sleepDisabled);
        Assert.Equal(act, d.Act);
        Assert.Equal(state, d.State);
    }

    [Fact]
    public void Never_enables_on_battery_and_never_disables_without_marker()
    {
        foreach (var setting in new[] { true, false })
        foreach (var marker in new[] { true, false })
        foreach (var sd in new bool?[] { true, false, null })
        {
            Assert.NotEqual(Act.Enable, Decide(setting, onAc: false, marker, sd).Act);
            if (!marker) Assert.NotEqual(Act.Disable, Decide(setting, true, marker, sd).Act);
            if (!marker) Assert.NotEqual(Act.Disable, Decide(setting, false, marker, sd).Act);
        }
    }

    [Fact]
    public void Battery_with_marker_acts_without_reading_pmset()
    {
        Assert.False(NeedsSleepDisabled(setting: true, onAc: false, marker: true));
        Assert.False(NeedsSleepDisabled(setting: false, onAc: true, marker: true));
        Assert.True(NeedsSleepDisabled(setting: true, onAc: true, marker: true));
        Assert.True(NeedsSleepDisabled(setting: true, onAc: true, marker: false));
        Assert.True(NeedsSleepDisabled(setting: false, onAc: true, marker: false));   // for at kunne melde "sat af brugeren"
    }

    [Theory]
    [InlineData("AC Power", true)]
    [InlineData("Battery Power", false)]
    [InlineData("UPS Power", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_AC_Power_counts_as_ac(string? providing, bool ac) => Assert.Equal(ac, IsAc(providing));

    [Fact]
    public void Parses_pmset_output()
    {
        Assert.True(ParseSleepDisabled("System-wide power settings:\n SleepDisabled\t\t1\nCurrently in use:\n standby 1\n"));
        Assert.False(ParseSleepDisabled("System-wide power settings:\n SleepDisabled\t\t0\nCurrently in use:\n"));
        Assert.False(ParseSleepDisabled("Currently in use:\n standby 1\n sleep 1\n"));   // linjen mangler = 0
        Assert.Null(ParseSleepDisabled(""));
        Assert.Null(ParseSleepDisabled("pmset: command not found"));
        Assert.True(ParsePsIsAc("Now drawing from 'AC Power'\n -InternalBattery-0 (id=1)\t100%; charged;"));
        Assert.False(ParsePsIsAc("Now drawing from 'Battery Power'\n -InternalBattery-0 (id=1)\t80%; discharging;"));
        Assert.False(ParsePsIsAc("Now drawing from 'UPS Power'"));
        Assert.False(ParsePsIsAc(""));
    }

    [Fact]
    public void Text_for_the_states()
    {
        Assert.Equal("Strøm · holdes vågen", Text(new KeepAwakeState { State = States.Held }));
        Assert.Equal("Batteri · sover normalt", Text(new KeepAwakeState { State = States.Battery }));
        Assert.Equal("Fra", Text(new KeepAwakeState { State = States.Off }));
    }

    // ---------------------------------------------------------------- vagten

    [Fact]
    public void Watchdog_script_only_reverts_with_marker_and_not_on_ac()
    {
        var s = WatchdogScriptText();
        Assert.StartsWith("#!/bin/sh\n", s);
        Assert.Contains($"MARKER='{Marker}'", s);
        Assert.Contains("[ -f \"$MARKER\" ] || exit 0", s);
        Assert.Contains("*\"'AC Power'\"*) exit 0 ;;", s);
        // markørtjek før strømtjek, strømtjek før pmset
        Assert.True(s.IndexOf("|| exit 0", StringComparison.Ordinal) < s.IndexOf("pmset -g ps", StringComparison.Ordinal));
        Assert.True(s.IndexOf("'AC Power'", StringComparison.Ordinal) < s.IndexOf("disablesleep 0", StringComparison.Ordinal));
        Assert.DoesNotContain("disablesleep 1", s);
        Assert.DoesNotContain("\r", s);
    }

    [Fact]
    public void Watchdog_plist_runs_every_30_seconds()
    {
        var p = WatchdogPlistText();
        Assert.Contains($"<string>{WatchdogLabel}</string>", p);
        Assert.Contains("<key>StartInterval</key><integer>30</integer>", p);
        Assert.Contains($"<string>{WatchdogScript}</string>", p);
        Assert.StartsWith("/Library/LaunchDaemons/", WatchdogPlist);
    }

    /// <summary>Kører det rigtige vagt-script med /bin/sh og en falsk pmset.</summary>
    [Theory]
    [InlineData(false, "Now drawing from 'Battery Power'", 0, false, false)]   // ingen markør: rør intet
    [InlineData(true, "Now drawing from 'AC Power'", 0, false, true)]          // strøm: rør intet
    [InlineData(true, "Now drawing from 'Battery Power'", 0, true, false)]     // batteri + markør: dvale til, markør væk
    [InlineData(true, "Now drawing from 'UPS Power'", 0, true, false)]         // UPS = batteri
    [InlineData(true, "", 0, true, false)]                                     // ukendt = batteri
    [InlineData(true, "Now drawing from 'Battery Power'", 1, true, true)]      // pmset fejlede: markøren bliver (prøv igen)
    public void Watchdog_script_behaviour(bool marker, string ps, int setExit, bool expectRevert, bool markerAfter)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh")) return;
        var dir = Directory.CreateTempSubdirectory("fd-ka-").FullName;
        try
        {
            var log = Path.Combine(dir, "calls.log");
            var markerFile = Path.Combine(dir, "keepawake.marker");
            var fake = Path.Combine(dir, "pmset");
            File.WriteAllText(fake, $"""
                #!/bin/sh
                echo "$*" >> '{log}'
                if [ "$1" = "-g" ]; then printf '%s\n' "{ps}"; exit 0; fi
                exit {setExit}

                """);
            File.SetUnixFileMode(fake, (UnixFileMode)0b111_101_101);
            if (marker) File.WriteAllText(markerFile, "x");
            var script = Path.Combine(dir, "watchdog.sh");
            File.WriteAllText(script, WatchdogScriptText().Replace(Marker, markerFile).Replace("/usr/bin/pmset", fake).Replace("/usr/bin/logger", "true"));
            var p = Process.Start(new ProcessStartInfo("/bin/sh", [script]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(10_000);
            Assert.Equal(0, p.ExitCode);
            var calls = File.Exists(log) ? File.ReadAllText(log) : "";
            Assert.Equal(expectRevert, calls.Contains("-a disablesleep 0"));
            Assert.DoesNotContain("disablesleep 1", calls);
            Assert.Equal(markerAfter, File.Exists(markerFile));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Uninstall_reverts_before_config_is_removed_and_removes_watchdog()
    {
        var u = MacInstall.UninstallScript();
        var revert = u.IndexOf("pmset -a disablesleep 0", StringComparison.Ordinal);
        Assert.True(revert > u.IndexOf($"bootout system/{MacPaths.AgentLabel}", StringComparison.Ordinal));
        Assert.True(revert < u.IndexOf($"rm -rf", StringComparison.Ordinal));
        Assert.Contains($"if [ -f '{Marker}' ]", u);   // kun hvis Fleetdeck satte den
        Assert.Contains($"bootout system/{WatchdogLabel}", u);
        Assert.Contains(WatchdogPlist, u);
    }

    // ---------------------------------------------------------------- lokal ordre fra menulinjen/appen

    [Fact]
    public void Local_command_is_allowed_from_user_dir_and_parsed()
    {
        Assert.True(TrayCommands.UserAllowed("keepawake"));
        var c = TrayCommands.Parse("{\"action\":\"keepawake\",\"on\":true}");   // præcis det, menulinje-hjælperen skriver
        Assert.NotNull(c);
        Assert.Equal("keepawake", c!.Action);
        Assert.True(c.On);
        Assert.False(TrayCommands.Parse(TrayCommands.Serialize(new TrayCommand { Action = "keepawake", On = false }))!.On);
    }

    [Fact]
    public void Local_toggle_applies_at_once_and_is_reported_until_acknowledged()
    {
        var t0 = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var l = Toggle(true, t0);
        Assert.True(l.On);
        Assert.True(l.Pending);
        var sent = Outgoing(l, t0.AddSeconds(3))!;
        Assert.True(sent.On);
        Assert.Equal(t0, sent.At);
        // ældre server / afvist svar: behold og send igen
        Assert.Same(l, Merge(l, null, sent.At));
        Assert.NotNull(Outgoing(l, t0.AddSeconds(40)));
        // serveren har afgjort: brug dens udgave, intet mere at sende
        var acked = Merge(l, new KeepAwakeSetting { On = true, At = t0.AddSeconds(1), Source = "mac" }, sent.At);
        Assert.False(acked.Pending);
        Assert.Null(Outgoing(acked, t0.AddMinutes(1)));
    }

    [Fact]
    public void Newer_local_change_is_not_overwritten_by_answer_to_older_checkin()
    {
        var t0 = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var first = Toggle(true, t0);
        var sent = Outgoing(first, t0)!.At;
        var second = Toggle(false, t0.AddSeconds(2));   // brugeren klikker igen, mens check-in er undervejs
        var merged = Merge(second, new KeepAwakeSetting { On = true, At = t0, Source = "mac" }, sent);
        Assert.False(merged.On);
        Assert.True(merged.Pending);
        // og en server-ændring overskriver ikke en ubekræftet lokal ændring, der ikke var med i check-in
        Assert.False(Merge(second, new KeepAwakeSetting { On = true, At = t0.AddHours(1), Source = "web" }, null).On);
    }

    [Fact]
    public void Server_change_is_adopted_when_nothing_is_pending()
    {
        var l = new Local { On = false };
        var m = Merge(l, new KeepAwakeSetting { On = true, At = DateTime.UtcNow, Source = "web" }, null);
        Assert.True(m.On);
        Assert.Equal("web", m.Source);
    }

    [Fact]
    public void Accept_corrects_clock_skew_and_last_change_wins()
    {
        var server = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        // Mac'ens ur går 10 min. bagud: ændringen 1 min. før check-in er 11:59 på serverens ur
        var behind = new KeepAwakeChange { On = true, At = server.AddMinutes(-11), Now = server.AddMinutes(-10) };
        Assert.Equal(server.AddMinutes(-1), Accept(null, behind, server));
        Assert.Equal(server.AddMinutes(-1), Accept(server.AddMinutes(-5), behind, server));   // nyere end web-ændringen kl. 11:55
        Assert.Null(Accept(server.AddSeconds(-30), behind, server));                         // web kl. 11:59:30 er nyest
        // Mac'ens ur går forud: aldrig senere end serverens nu
        var ahead = new KeepAwakeChange { On = true, At = server.AddHours(2), Now = server.AddHours(1) };
        Assert.Equal(server, Accept(null, ahead, server));
    }

    // ---------------------------------------------------------------- server: indstillingen, to-vejs og signeret svar

    [Fact]
    public void Web_and_mac_changes_roundtrip_last_wins()
    {
        var m = new Machine { Id = 1, Name = "MBP", Platform = Platforms.Mac };
        var t = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        KeepAwakeSync.FromWeb(m, true, "admin", t);
        Assert.Equal(new KeepAwakeSetting { On = true, At = t, Source = "web" }.On, KeepAwakeSync.For(m)!.On);
        Assert.Equal("web", m.KeepAwakeSource);
        Assert.Equal("admin", m.KeepAwakeBy);
        // ændret på Mac'en efter web: vinder
        Assert.True(KeepAwakeSync.FromMac(m, new KeepAwakeChange { On = false, At = t.AddMinutes(1), Now = t.AddMinutes(1) }, t.AddMinutes(1)));
        Assert.False(m.KeepAwakeOnAc);
        Assert.Equal("mac", m.KeepAwakeSource);
        Assert.Null(m.KeepAwakeBy);
        // en gammel Mac-ændring (sendt igen efter en web-ændring) taber
        KeepAwakeSync.FromWeb(m, true, "admin", t.AddMinutes(5));
        Assert.False(KeepAwakeSync.FromMac(m, new KeepAwakeChange { On = false, At = t.AddMinutes(2), Now = t.AddMinutes(6) }, t.AddMinutes(6)));
        Assert.True(m.KeepAwakeOnAc);
        Assert.Equal("web", m.KeepAwakeSource);
    }

    [Fact]
    public void Only_macs_have_the_setting()
    {
        var pc = new Machine { Platform = Platforms.Windows };
        Assert.Null(KeepAwakeSync.For(pc));
        Assert.False(KeepAwakeSync.FromMac(pc, new KeepAwakeChange { On = true, At = DateTime.UtcNow, Now = DateTime.UtcNow }, DateTime.UtcNow));
        Assert.False(pc.KeepAwakeOnAc);
    }

    [Fact]
    public void Endpoint_requires_admin()
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=:memory:"));
        b.Services.AddSignalR();
        b.Services.AddSingleton<Live>();
        b.Services.AddScoped<AuditService>();
        b.Services.AddAuthorization();
        var app = b.Build();
        app.MapKeepAwakeEndpoints();
        var e = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>());
        Assert.Equal("/api/machines/{id:int}/keepawake", e.RoutePattern.RawText);
        Assert.Contains(e.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == "admin");
        Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
    }
}

/// <summary>Indstillingen i databasen og i det signerede check-in-svar.</summary>
public sealed class MacKeepAwakeCheckinTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;
    private readonly SettingsService _s;

    public MacKeepAwakeCheckinTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _s = new SettingsService(_db);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private async Task<Machine> Add(string platform, bool on)
    {
        var m = new Machine { Name = $"M{Guid.NewGuid():N}"[..10], Platform = platform };
        if (on) KeepAwakeSync.FromWeb(m, true, "admin", DateTime.UtcNow);
        _db.Machines.Add(m);
        await _db.SaveChangesAsync();
        return m;
    }

    private static (AgentBinary Bin, SigningKeys Files, JobSigningKey Jobs) Keys()
    {
        var paths = new Paths();
        return (new AgentBinary(paths), new SigningKeys(paths, NullLogger<SigningKeys>.Instance), new JobSigningKey(Directory.CreateTempSubdirectory().FullName));
    }

    [Fact]
    public async Task Setting_survives_the_database()
    {
        var m = await Add(Platforms.Mac, on: true);
        _db.ChangeTracker.Clear();
        var back = await _db.Machines.SingleAsync(x => x.Id == m.Id);
        Assert.True(back.KeepAwakeOnAc);
        Assert.Equal("web", back.KeepAwakeSource);
        Assert.Equal("admin", back.KeepAwakeBy);
        Assert.NotNull(back.KeepAwakeAt);
    }

    [Fact]
    public async Task Signed_payload_carries_the_setting_and_the_gate_uses_it()
    {
        var m = await Add(Platforms.Mac, on: true);
        var (bin, files, jobs) = Keys();
        var nonce = CommandSigning.NewNonce();
        var r = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m, new CheckinRequest { Nonce = nonce, Trust = new AgentTrustReport() }, m.Id, "http://x");
        Assert.True(r.KeepAwake!.On);
        var v = CommandSigning.Verify(jobs.PublicPem, r.Signed, m.Id, nonce, DateTime.UtcNow);
        Assert.True(v.Ok, v.Why);
        Assert.True(v.Commands!.KeepAwake!.On);
        Assert.Equal("web", v.Commands.KeepAwake.Source);
        Assert.Contains("\"keepAwake\":{\"on\":true", r.Signed!.Payload);
        // en agent med pinnet nøgle bruger kun det signerede: et ændret usigneret felt tæller ikke
        r.KeepAwake = new KeepAwakeSetting { On = false };
        var store = new Store { JobKey = jobs.PublicPem };
        var gate = CommandGate.Apply(r, store, m.Id, nonce, DateTime.UtcNow);
        Assert.True(gate.Trusted);
        Assert.True(r.KeepAwake!.On);
    }

    [Fact]
    public async Task Rejected_answer_leaves_no_setting_so_agent_keeps_last_known()
    {
        var m = await Add(Platforms.Mac, on: true);
        var (bin, files, jobs) = Keys();
        var r = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m, new CheckinRequest { Nonce = CommandSigning.NewNonce() }, m.Id, "http://x");
        var gate = CommandGate.Apply(r, new Store { JobKey = jobs.PublicPem }, m.Id, CommandSigning.NewNonce(), DateTime.UtcNow);   // forkert nonce
        Assert.False(gate.Trusted);
        Assert.Null(r.KeepAwake);
        var local = new MacKeepAwake.Local { On = true, At = DateTime.UtcNow, Source = "web" };
        Assert.True(MacKeepAwake.Merge(local, r.KeepAwake, null).On);
    }

    [Fact]
    public async Task Non_mac_gets_no_setting()
    {
        var m = await Add(Platforms.Linux, on: false);
        var (bin, files, jobs) = Keys();
        var nonce = CommandSigning.NewNonce();
        var r = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m, new CheckinRequest { Nonce = nonce }, m.Id, "http://x");
        Assert.Null(r.KeepAwake);
        Assert.DoesNotContain("keepAwake", r.Signed!.Payload);
    }

    private sealed class Store : ITrustStore
    {
        public string? JobKey { get; set; }
        public string? TlsPin { get; set; }
    }
}
