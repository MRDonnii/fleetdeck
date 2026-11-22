using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Mac: "Funktioner" – temperaturmåling (powermetrics/SMC), tilstanden i appens vindue og hjælperens rettelser til Mac.</summary>
public class MacFeaturesTests
{
    private static readonly DateTime T0 = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, (bool On, DateTime At)> NoneSent = [];

    // ---------------------------------------------------------------- powermetrics (Intel)

    private const string PowermetricsIntel = """
        Machine model: MacBookPro14,1
        OS version: 20G1427

        *** Sampled system activity (Sun Nov 22 12:00:00 2026 +0100) (502.11ms elapsed) ***

        **** SMC sensors ****

        CPU Thermal level: 0
        GPU Thermal level: 0
        IO Thermal level: 0
        Fan: 1797.61 rpm
        CPU die temperature: 52.38 C
        GPU die temperature: 46.00 C
        CPU Plimit: 0.00
        GPU Plimit (Int): 0.00
        """;

    [Fact]
    public void Powermetrics_gives_cpu_and_gpu_die_temperature_on_intel()
    {
        Assert.Equal((52.4, 46.0), MacTemps.ParsePowermetrics(PowermetricsIntel));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("powermetrics: unrecognized sampler: smc")]                 // Apple silicon har ikke smc-sampleren
    [InlineData("**** Thermal pressure ****\nCurrent pressure level: Nominal")]
    [InlineData("CPU die temperature: 0.00 C")]                             // sensoren svarer ikke
    public void Powermetrics_without_a_temperature_gives_nothing(string? text) =>
        Assert.Equal(((double?)null, (double?)null), MacTemps.ParsePowermetrics(text));

    [Fact]
    public void Powermetrics_accepts_a_decimal_comma_and_only_cpu()
    {
        var (cpu, gpu) = MacTemps.ParsePowermetrics("CPU die temperature: 61,5 C\n");
        Assert.Equal(61.5, cpu);
        Assert.Null(gpu);
    }

    // ---------------------------------------------------------------- SMC

    [Fact]
    public void Smc_keys_are_big_endian_fourcc()
    {
        Assert.Equal(0x54433044u, MacTemps.FourCC("TC0D"));
        Assert.Equal("sp78", MacTemps.FourCCText(MacTemps.FourCC("sp78")));
        Assert.Equal("flt ", MacTemps.FourCCText(0x666C7420));
        Assert.Throws<ArgumentException>(() => MacTemps.FourCC("TC0"));
    }

    [Theory]
    [InlineData("sp78", new byte[] { 0x34, 0x80 }, 52.5)]        // 52 + 128/256
    [InlineData("sp78", new byte[] { 0x2D, 0x00, 0x00 }, 45.0)]  // ekstra bytes ignoreres
    [InlineData("sp78", new byte[] { 0xFF, 0x00 }, -1.0)]        // fortegn
    [InlineData("fpe2", new byte[] { 0x1C, 0x20 }, 1800.0)]      // blæser-rpm (0x1C20 / 4)
    [InlineData("ui8 ", new byte[] { 42 }, 42.0)]
    [InlineData("ui16", new byte[] { 0x01, 0x00 }, 256.0)]
    public void Smc_values_are_decoded_by_type(string type, byte[] data, double expected) =>
        Assert.Equal(expected, MacTemps.DecodeSmc(type, data)!.Value, 3);

    [Fact]
    public void Smc_float_and_unknown_types()
    {
        Assert.Equal(48.25, MacTemps.DecodeSmc("flt ", BitConverter.GetBytes(48.25f))!.Value, 3);
        Assert.Null(MacTemps.DecodeSmc("ch8*", [1, 2]));
        Assert.Null(MacTemps.DecodeSmc("sp78", [0x34]));   // for få bytes
        Assert.Null(MacTemps.DecodeSmc("flt ", [1, 2]));
    }

    [Fact]
    public void Smc_cpu_is_the_hottest_die_sensor_and_proximity_only_as_fallback()
    {
        var all = new Dictionary<string, double> { ["TC0P"] = 70, ["TC0D"] = 55.04, ["TC0E"] = 58.31, ["TC1C"] = 0, ["TG0D"] = -127, ["TG0P"] = 44.5 };
        Assert.Equal((58.3, 44.5), MacTemps.FromSmc(all));
        Assert.Equal((70.0, (double?)null), MacTemps.FromSmc(new Dictionary<string, double> { ["TC0P"] = 70, ["TC0D"] = 255 }));
        Assert.Equal(((double?)null, (double?)null), MacTemps.FromSmc(new Dictionary<string, double>()));
        Assert.Contains("TC0D", MacTemps.SmcKeys);
        Assert.Contains("TG0D", MacTemps.SmcKeys);
    }

    // ---------------------------------------------------------------- tilstand i appen

    [Fact]
    public void Temperature_text_covers_every_state()
    {
        Assert.Contains("nyere", MacTemps.Text(null));
        Assert.StartsWith("Slået fra", MacTemps.Text(new MacTempState { On = false, Cpu = 50 }));
        Assert.Equal("Slået til – venter på første måling", MacTemps.Text(new MacTempState()));
        Assert.Contains("ingen af Mac'ens", MacTemps.Text(new MacTempState { Note = "ingen af Mac'ens temperatursensorer svarede" }));
        Assert.Equal("CPU 52 °C · GPU 46 °C · fra SMC (Intel)", MacTemps.Text(new MacTempState { Cpu = 52.4, Gpu = 46, Source = MacTemps.Smc }));
        Assert.Equal("CPU 41 °C · fra Apple silicon-sensorerne", MacTemps.Text(new MacTempState { Cpu = 41.2, Source = MacTemps.AppleSilicon }));
    }

    private static TrayStatus Status(MacTempState? temps, params ExtensionView[] ext) => new() { Temps = temps, Extensions = [.. ext] };

    [Fact]
    public void Temperature_is_a_toggle_in_the_measuring_group_and_not_listed_as_a_tool()
    {
        var rows = MacFeatures.Rows(Status(new MacTempState { Cpu = 50, Source = MacTemps.AppleSilicon }), Extensions.BuiltIn, NoneSent, T0);
        var t = rows[0];
        Assert.Equal((MacTemps.FeatureId, MacFeatures.Measuring, MacFeatures.Kind.Toggle, true, false, true), (t.Id, t.Group, t.Kind, t.On, t.Busy, t.CanChange));
        Assert.Single(rows, r => r.Id == MacTemps.FeatureId);
        Assert.Equal(MacFeatures.Measuring, rows.Single(r => r.Id == "diskhelbred").Group);
        Assert.All(rows.Where(r => r.Id != MacTemps.FeatureId), r => Assert.Equal(MacFeatures.Kind.Install, r.Kind));
        // kun Mac-udvidelser (ikke winget, lm-sensors eller flatpak)
        Assert.DoesNotContain(rows, r => r.Id is "winget" or "sensorer" or "flatpak");
        Assert.Contains(rows, r => r.Id == "homebrew");
    }

    [Fact]
    public void An_older_service_without_temperature_state_cannot_be_toggled()
    {
        var t = MacFeatures.Rows(Status(null), Extensions.BuiltIn, NoneSent, T0)[0];
        Assert.False(t.CanChange);
        Assert.Contains("nyere", t.Sub);
        Assert.All(MacFeatures.Rows(null, Extensions.BuiltIn, NoneSent, T0), r => Assert.False(r.CanChange));   // tjenesten kører ikke
    }

    [Fact]
    public void A_sent_toggle_shows_the_choice_until_the_service_confirms_it()
    {
        var off = new Dictionary<string, (bool, DateTime)> { [MacTemps.FeatureId] = (false, T0) };
        var s = Status(new MacTempState { Cpu = 50 });
        var waiting = MacFeatures.Rows(s, Extensions.BuiltIn, off, T0.AddSeconds(5))[0];
        Assert.Equal((false, true, false, "Slås fra…"), (waiting.On, waiting.Busy, waiting.CanChange, waiting.Sub));
        Assert.Null(MacFeatures.Outcome(waiting, (false, T0), null, T0.AddSeconds(5)));

        // tjenesten har slået den fra
        var done = MacFeatures.Rows(Status(new MacTempState { On = false }), Extensions.BuiltIn, off, T0.AddSeconds(8))[0];
        Assert.False(done.Busy);
        Assert.Equal(("Temperaturmåling er slået fra.", 'o'), MacFeatures.Outcome(done, (false, T0), null, T0.AddSeconds(8)));

        // ingen svar inden for ventetiden
        var late = MacFeatures.Rows(s, Extensions.BuiltIn, off, T0 + MacFeatures.Wait)[0];
        Assert.True(late.On);
        Assert.Equal('w', MacFeatures.Outcome(late, (false, T0), null, T0 + MacFeatures.Wait)!.Value.Tone);
    }

    [Fact]
    public void Switching_on_without_a_sensor_is_a_warning()
    {
        var on = new Dictionary<string, (bool, DateTime)> { [MacTemps.FeatureId] = (true, T0) };
        var row = MacFeatures.Rows(Status(new MacTempState { Note = "ingen af Mac'ens temperatursensorer svarede" }), Extensions.BuiltIn, on, T0.AddSeconds(3))[0];
        Assert.Equal('w', MacFeatures.Outcome(row, (true, T0), null, T0.AddSeconds(3))!.Value.Tone);
        var good = MacFeatures.Rows(Status(new MacTempState { Cpu = 50, Source = MacTemps.Smc }), Extensions.BuiltIn, on, T0.AddSeconds(3))[0];
        Assert.Equal(("Temperaturmåling er slået til: CPU 50 °C · fra SMC (Intel)", 'o'), MacFeatures.Outcome(good, (true, T0), null, T0.AddSeconds(3)));
    }

    [Fact]
    public void Tools_show_installed_pending_and_what_they_need()
    {
        var s = Status(new MacTempState(),
            new ExtensionView { Id = "homebrew", Name = "Homebrew", Gives = "x", Installed = false },
            new ExtensionView { Id = "mas", Name = "App Store (mas)", Gives = "Programmer fra App Store" },
            new ExtensionView { Id = "fjernskrivebord", Name = "Fjernskrivebord (RustDesk)", Gives = "Fjernskrivebord", Installed = true },
            new ExtensionView { Id = Extensions.UsbIpadId, Name = "iPad via USB", Gives = "iPads", Pending = true });
        var rows = MacFeatures.Rows(s, Extensions.BuiltIn, NoneSent, T0).ToDictionary(r => r.Id);
        Assert.Equal((true, false, "Installeret"), (rows["fjernskrivebord"].On, rows["fjernskrivebord"].CanChange, rows["fjernskrivebord"].Sub));
        Assert.Equal((true, false, "Installeres…"), (rows[Extensions.UsbIpadId].Busy, rows[Extensions.UsbIpadId].CanChange, rows[Extensions.UsbIpadId].Sub));
        Assert.True(rows["mas"].CanChange);
        Assert.Contains("kræver Homebrew", rows["mas"].Sub);
        Assert.Contains("kræver Homebrew", rows["diskhelbred"].Sub);
        Assert.DoesNotContain("kræver", rows["homebrew"].Sub);
        // Homebrew på vej: de andre venter på den
        s.Extensions[0].Pending = true;
        Assert.Equal("Venter på Homebrew", MacFeatures.Rows(s, Extensions.BuiltIn, NoneSent, T0).Single(r => r.Id == "mas").Sub);
    }

    [Fact]
    public void An_install_is_busy_right_after_the_click_and_reports_how_it_ended()
    {
        var sent = new Dictionary<string, (bool, DateTime)> { ["mas"] = (true, T0) };
        MacFeatures.Row Mas(TrayStatus s, DateTime now) => MacFeatures.Rows(s, Extensions.BuiltIn, sent, now).Single(r => r.Id == "mas");
        var notYet = Status(new MacTempState(), new ExtensionView { Id = "mas", Name = "App Store (mas)" });
        Assert.True(Mas(notYet, T0.AddSeconds(2)).Busy);   // tjenesten har ikke nået at melde "på vej" endnu
        Assert.Null(MacFeatures.Outcome(Mas(notYet, T0.AddSeconds(2)), (true, T0), null, T0.AddSeconds(2)));

        var installed = Status(new MacTempState(), new ExtensionView { Id = "mas", Name = "App Store (mas)", Installed = true });
        Assert.Equal(("App Store (mas) er installeret.", 'o'), MacFeatures.Outcome(Mas(installed, T0.AddMinutes(3)), (true, T0), T0.AddMinutes(2), T0.AddMinutes(3)));

        // jobbet er slut uden at programmet ses: vent på programlisten, og sig så, at det ikke lykkedes
        var after = Mas(notYet, T0.AddMinutes(3));
        Assert.Null(MacFeatures.Outcome(after, (true, T0), T0.AddMinutes(3).AddSeconds(-10), T0.AddMinutes(3)));
        Assert.Equal('b', MacFeatures.Outcome(after, (true, T0), T0.AddMinutes(1), T0.AddMinutes(3))!.Value.Tone);
        // aldrig set på vej: tjenesten tog ikke imod ordren
        Assert.Equal('w', MacFeatures.Outcome(after, (true, T0), null, T0.AddMinutes(3))!.Value.Tone);
    }

    [Fact]
    public void Status_window_summary()
    {
        var s = Status(new MacTempState { Cpu = 47.6 },
            new ExtensionView { Id = "homebrew", Installed = true }, new ExtensionView { Id = "mas", Pending = true });
        var (temps, tools) = MacFeatures.Summary(s, Extensions.BuiltIn);
        Assert.Equal("Temperaturmåling: CPU 48 °C", temps);
        var n = Extensions.BuiltIn.Count(e => e.Mac is not null && e.Id != MacTemps.FeatureId);
        Assert.Equal($"1 af {n} hjælpeværktøjer installeret · 1 installeres", tools);
        Assert.Equal("Temperaturmåling: slået fra", MacFeatures.Summary(Status(new MacTempState { On = false }), Extensions.BuiltIn).Temps);
    }

    [Fact]
    public void Feature_command_is_admin_only_and_round_trips()
    {
        var c = MacFeatures.Command(MacTemps.FeatureId, false);
        Assert.False(TrayCommands.UserAllowed(c.Action));   // fra brugermappen afvises den: kræver administrator (som pause)
        var back = TrayCommands.Decode(TrayCommands.Encode(c))!;
        Assert.Equal((MacFeatures.FeatureAction, MacTemps.FeatureId, (bool?)false), (back.Action, back.Id, back.On));
    }

    [Fact]
    public void Status_file_carries_the_temperature_state()
    {
        var json = TrayCommands.Serialize(new TrayCommand());   // samme indstillinger som status.json
        Assert.NotNull(json);
        var s = System.Text.Json.JsonSerializer.Deserialize<TrayStatus>(
            System.Text.Json.JsonSerializer.Serialize(new TrayStatus { Temps = new MacTempState { On = false } }, TrayCommands.Json), TrayCommands.Json)!;
        Assert.False(s.Temps!.On);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<TrayStatus>("{}", TrayCommands.Json)!.Temps);   // ældre tjeneste
    }

    // ---------------------------------------------------------------- server: katalog, råd og udvidelser

    [Fact]
    public void Catalog_has_mac_fixes_that_only_run_on_mac()
    {
        foreach (var id in new[] { "temps_enable_mac", "disk_tools_mac" })
        {
            var f = HelperCatalog.Get(id);
            Assert.NotNull(f?.Script);
            Assert.Null(HelperService.PlatformBlocker(f!, Platforms.Mac));
            Assert.NotNull(HelperService.PlatformBlocker(f!, Platforms.Windows));
            Assert.NotNull(HelperService.PlatformBlocker(f!, Platforms.Linux));
        }
        var t = HelperCatalog.Get("temps_enable_mac")!.Script!;
        Assert.Contains($"rm -f '{MacTemps.OffFile}'", t);
        Assert.Contains($"{MacPaths.AgentExe}", t);
        Assert.DoesNotContain("brew", t);   // intet at installere
        var d = HelperCatalog.Get("disk_tools_mac")!.Script!;
        Assert.Contains("sudo -u \"$U\"", d);   // Homebrew aldrig som root
        Assert.Contains("exit 0", d);           // uden Homebrew: en forklaring, ikke en fejl
    }

    [Fact]
    public void Mac_extensions_point_at_the_mac_helpers()
    {
        var temp = Extensions.BuiltIn.Single(e => e.Id == MacTemps.FeatureId).Mac!;
        Assert.Equal(("helper", "temps_enable_mac", MacTemps.Detect), (temp.Type, temp.Identifier, temp.Detect));
        var disk = Extensions.BuiltIn.Single(e => e.Id == "diskhelbred").Mac!;
        Assert.Equal(("helper", "disk_tools_mac"), (disk.Type, disk.Identifier));
        foreach (var e in Extensions.BuiltIn.Select(x => x.Mac).Where(x => x?.Type == "helper"))
            Assert.Null(HelperService.PlatformBlocker(HelperCatalog.Get(e!.Identifier!)!, Platforms.Mac));
        // Homebrew installeres før de udvidelser, der bruger den
        var order = Extensions.BuiltIn.Select(x => x.Id).ToList();
        Assert.All(MacFeatures.Requires, kv => Assert.True(order.IndexOf(kv.Value) < order.IndexOf(kv.Key), kv.Key));
    }

    [Fact]
    public void Advice_for_missing_temperature_on_mac_runs_the_mac_helper()
    {
        var mac = HelperCatalog.AdviceFor("notemp", "info", Platforms.Mac)!;
        Assert.Equal("temps_enable_mac", mac.FixId);
        Assert.Contains(mac.Steps, s => s.Contains("Funktioner"));
        Assert.Equal("temps_enable", HelperCatalog.AdviceFor("notemp", "info", Platforms.Windows)!.FixId);
        Assert.Null(HelperCatalog.AdviceFor("notemp", "info", Platforms.Linux)!.FixId);
    }

    [Fact]
    public void Advice_for_a_hot_mac_has_mac_steps_and_no_windows_tool()
    {
        var a = HelperCatalog.AdviceFor("cputemp", "warning", Platforms.Mac)!;
        Assert.Null(a.FixId);
        Assert.Contains(a.Steps, s => s.Contains("Aktivitetsmonitor"));
        Assert.Contains(a.Steps, s => s.Contains("SMC"));
        Assert.Equal("crash_analysis", HelperCatalog.AdviceFor("cputemp", "warning", Platforms.Windows)!.FixId);
    }

    [Fact]
    public async Task Mac_temperature_extension_is_installed_when_the_mac_reports_a_temperature()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var withTemps = new HealthReport { Live = new LiveFacts { Temps = new Temperatures { Cpu = 51, Source = MacTemps.Smc } } };
        var off = new HealthReport { Live = new LiveFacts { Temps = new Temperatures { Source = MacTemps.Off } } };
        db.Machines.Add(new Machine { Id = 1, Name = "IMAC", Platform = Platforms.Mac, HealthJson = Json.Ser(withTemps) });
        db.Machines.Add(new Machine { Id = 2, Name = "MACBOOK", Platform = Platforms.Mac, HealthJson = Json.Ser(off) });
        db.Machines.Add(new Machine { Id = 3, Name = "PC", Platform = Platforms.Windows, HealthJson = Json.Ser(withTemps) });
        await db.SaveChangesAsync();
        var dir = Directory.CreateTempSubdirectory("fd-ext-").FullName;
        var live = new Live(new NoHub<UiHub>(), new NoHub<AgentHub>());
        var svc = new ExtensionService(db, new JobService(db, live), new Paths(dir, dir), new UsbIpadStore(), NullLogger<ExtensionService>.Instance);

        Assert.True((await svc.For(db.Machines.Find(1)!)).Single(e => e.Id == MacTemps.FeatureId).Installed);
        Assert.False((await svc.For(db.Machines.Find(2)!)).Single(e => e.Id == MacTemps.FeatureId).Installed);
        // Windows bruger stadig LibreHardwareMonitor i programlisten – en temperatur alene er ikke nok
        Assert.False((await svc.For(db.Machines.Find(3)!)).Single(e => e.Id == MacTemps.FeatureId).Installed);

        // Installér på Mac'en: hjælperens script som job
        Assert.Equal(1, await svc.Install(db.Machines.Find(2)!, MacTemps.FeatureId, "manual", "anna"));
        var job = await db.Jobs.SingleAsync(j => j.MachineId == 2);
        Assert.Equal("Udvidelse: Temperaturmåling", job.Title);
        Assert.Contains(MacTemps.OffFile, job.ParamsJson);
        Assert.True((await svc.For(db.Machines.Find(2)!)).Single(e => e.Id == MacTemps.FeatureId).Pending);
        Directory.Delete(dir, true);
    }

    private sealed class NoHub<T> : IHubContext<T> where T : Hub
    {
        public IHubClients Clients { get; } = new NoClients();
        public IGroupManager Groups { get; } = new NoGroups();
    }
    private sealed class NoClients : IHubClients
    {
        private static readonly IClientProxy P = new NoProxy();
        public IClientProxy All => P;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Client(string connectionId) => P;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => P;
        public IClientProxy Group(string groupName) => P;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => P;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => P;
        public IClientProxy User(string userId) => P;
        public IClientProxy Users(IReadOnlyList<string> userIds) => P;
    }
    private sealed class NoProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
