using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

public class DebloatCatalogTests
{
    [Fact]
    public void Ids_are_unique_and_well_formed()
    {
        var ids = DebloatCatalog.Items.Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var i in DebloatCatalog.Items)
        {
            Assert.Matches(@"^[a-z]+\.[a-z0-9_]+$", i.Id);
            Assert.StartsWith(i.Category + ".", i.Id);
        }
    }

    [Fact]
    public void Every_item_is_complete()
    {
        var cats = DebloatCatalog.Categories.Select(c => c.Id).ToHashSet();
        foreach (var i in DebloatCatalog.Items)
        {
            Assert.Contains(i.Category, cats);
            Assert.Contains(i.Risk, DebloatRisk.All);
            Assert.False(string.IsNullOrWhiteSpace(i.Title), i.Id);
            Assert.False(string.IsNullOrWhiteSpace(i.Description), i.Id);
            Assert.All(i.Profiles, p => Assert.Contains(p, DebloatProfiles.All));
            Assert.All(i.Editions, e => Assert.Contains(e, new[] { "home", "pro", "enterprise" }));
            Assert.True(i.Registry.Length + i.Services.Length + i.Tasks.Length + i.Apps.Length + i.Winget.Length + i.PowerSettings.Length + i.Features.Length > 0
                || i.PowerPlan is not null || i.HibernateOff || i.WakeOnLan, $"{i.Id} gør ingenting");
            Assert.True(i.MaxBuild == 0 || i.MaxBuild >= i.MinBuild, i.Id);
        }
        Assert.All(DebloatCatalog.Categories, c => Assert.Contains(DebloatCatalog.Items, i => i.Category == c.Id));
    }

    [Fact]
    public void Every_item_can_be_undone()
    {
        foreach (var i in DebloatCatalog.Items)
        {
            // registrering: før-værdien gemmes; uden før-værdi bruges Windows' standard (slet eller standardværdi)
            foreach (var r in i.Registry)
            {
                Assert.Contains(r.Hive, new[] { RegHive.Machine, RegHive.Users, RegHive.DefaultUser, RegHive.Logon, RegHive.Classes });
                Assert.Contains(r.Kind, new[] { RegKind.Dword, RegKind.String });
                Assert.False(r.Path.StartsWith('\\') || r.Path.EndsWith('\\') || r.Path.Contains(@"\\"), $"{i.Id}: {r.Path}");
                Assert.False(r.Path.StartsWith("HK", StringComparison.OrdinalIgnoreCase), $"{i.Id}: hive skrives i Hive, ikke i stien");
                Assert.True(r.Value is null || (r.Kind == RegKind.Dword ? r.Value is int : r.Value is string), $"{i.Id}: {r.Name} har forkert type");
                // ellers kan fortryd ikke kendes fra anvendt (en sletning fortrydes altid fra før-billedet)
                if (r.Value is not null) Assert.NotEqual(r.Value, r.WindowsDefault);
            }
            foreach (var s in i.Services) { Assert.InRange(s.Start, 2, 4); Assert.InRange(s.WindowsDefault, 0, 4); Assert.NotEqual(s.Start, s.WindowsDefault); }
            foreach (var t in i.Tasks) { Assert.StartsWith(@"\", t.Path); Assert.EndsWith(@"\", t.Path); Assert.False(string.IsNullOrWhiteSpace(t.Name)); }
            // apps: enten et Store-id til geninstallation, eller tydeligt markeret som ikke-geninstallerbar
            foreach (var a in i.Apps)
            {
                Assert.False(string.IsNullOrWhiteSpace(a.Title), $"{i.Id}: {a.Name}");
                if (a.StoreId is not null) Assert.Matches("^[0-9A-Z]{12,14}$", a.StoreId);
            }
            Assert.Equal(i.FullyReversible, !i.NotReinstallable.Any());
        }
    }

    [Fact]
    public void Protected_packages_are_never_removed()
    {
        var removals = DebloatCatalog.Items.SelectMany(i => i.Apps).ToList();
        Assert.NotEmpty(removals);
        foreach (var a in removals)
        {
            Assert.False(DebloatCatalog.IsProtected(a.Name), $"{a.Name} er beskyttet");
            foreach (var ex in DebloatCatalog.ProtectedExamples)
                Assert.False(DebloatCatalog.Like(ex, a.Name), $"Fjernelsen '{a.Name}' rammer den beskyttede '{ex}'");
            // et jokertegn må ikke stå alene eller kun foran et generisk Microsoft-præfiks
            Assert.False(a.Name.Trim('*', '.').Length < 4 || a.Name.StartsWith("Microsoft*", StringComparison.OrdinalIgnoreCase), a.Name);
        }
        foreach (var ex in DebloatCatalog.ProtectedExamples) Assert.True(DebloatCatalog.IsProtected(ex), ex);
        // de pakker, opgaven nævner udtrykkeligt
        foreach (var must in new[] { "Microsoft.WindowsStore", "Microsoft.WindowsCalculator", "Microsoft.Windows.Photos", "Microsoft.WindowsNotepad",
                     "Microsoft.WindowsTerminal", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.DesktopAppInstaller", "Microsoft.VCLibs.140.00",
                     "Microsoft.NET.Native.Framework.2.2", "Microsoft.UI.Xaml.2.8" })
            Assert.True(DebloatCatalog.IsProtected(must), must);
    }

    [Fact]
    public void Nothing_touches_update_defender_store_or_network()
    {
        var forbidden = new[] { "WindowsUpdate", "wuauserv", "UsoSvc", "BITS", "Windows Defender", "WinDefend", "SecurityHealth", "WindowsStore",
            "DeliveryOptimization", "DoSvc", "sppsvc", "Tcpip", "Dnscache", "NlaSvc", "hosts", "Firewall", "LicenseManager", "AppXSvc", "ClipSVC", "InstallService" };
        foreach (var i in DebloatCatalog.Items)
        {
            // de eneste undtagelser: dokumenterede politikker, der hverken slår opdateringer eller netværk fra (testes hver for sig nedenfor)
            var text = string.Join(' ', i.Registry.Where(r => !AllowedNetworkAndUpdateValues.Contains(r.Path + "\\" + r.Name)).Select(r => r.Path + "\\" + r.Name)
                .Concat(i.Services.Select(s => s.Name)).Concat(i.Tasks.Select(t => t.Path + t.Name)));
            foreach (var f in forbidden) Assert.DoesNotContain(f, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly HashSet<string> AllowedNetworkAndUpdateValues =
    [
        @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\DeferFeatureUpdates", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\DeferFeatureUpdatesPeriodInDays",
        @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\DeferQualityUpdates", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\DeferQualityUpdatesPeriodInDays",
        @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\DisabledComponents", @"SOFTWARE\Policies\Microsoft\Windows\TCPIP\v6Transition\Teredo_State",
    ];

    [Fact]
    public void Disabling_windows_update_is_never_offered()
    {
        var banned = new[] { "NoAutoUpdate", "DisableWindowsUpdateAccess", "SetDisableUXWUAccess", "AUOptions", "UseWUServer", "WUServer", "DoNotConnectToWindowsUpdateInternetLocations", "ExcludeWUDriversInQualityUpdate" };
        Assert.All(DebloatCatalog.Items.SelectMany(i => i.Registry), r => Assert.DoesNotContain(r.Name, banned));
        var updates = Assert.Single(DebloatCatalog.Items, i => i.Category == "updates");
        Assert.Equal("updates.security_only", updates.Id);
        Assert.Empty(updates.Profiles);   // opdateringspolitik vælges kun med vilje
        Assert.Empty(updates.Services);
        Assert.Empty(updates.Tasks);
        var days = updates.Registry.ToDictionary(r => r.Name, r => (int)r.Value!);
        Assert.Equal(365, days["DeferFeatureUpdatesPeriodInDays"]);
        Assert.Equal(4, days["DeferQualityUpdatesPeriodInDays"]);
        Assert.Equal(4, days.Count);
    }

    [Fact]
    public void Network_items_are_advanced_opt_in_and_keep_ipv6_on()
    {
        var net = DebloatCatalog.Items.Where(i => i.Category == "network").ToList();
        Assert.Equal(2, net.Count);
        Assert.All(net, i => { Assert.Equal(DebloatRisk.Advanced, i.Risk); Assert.Empty(i.Profiles); Assert.True(i.NeedsReboot); });
        // kun bit 0x20 (foretræk IPv4) lægges til; værdien overskrives aldrig, så IPv6 ikke slås fra ved et uheld
        var v6 = Assert.Single(DebloatCatalog.Find("network.prefer_ipv4")!.Registry);
        Assert.True(v6.Bits);
        Assert.Equal(0x20, v6.Value);
        Assert.Contains("[long]$cur.v -bor [long]$op.value", DebloatScript.Engine);
    }

    [Fact]
    public void Power_plan_guids_are_valid_and_only_fleetdecks_copies_can_be_deleted()
    {
        const string guid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
        var plans = DebloatCatalog.Items.Where(i => i.PowerPlan is not null).ToList();
        Assert.Equal(3, plans.Count);
        foreach (var g in plans.SelectMany(i => new[] { i.PowerPlan!.Base, i.PowerPlan.Copy }).OfType<string>()) Assert.Matches(guid, g);
        Assert.Equal(DebloatPower.Balanced, DebloatCatalog.Find("power.plan_balanced")!.PowerPlan!.Base);
        Assert.Null(DebloatCatalog.Find("power.plan_balanced")!.PowerPlan!.Copy);
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", DebloatCatalog.Find("power.plan_high")!.PowerPlan!.Base);
        Assert.Equal("e9a42b02-d5df-448d-aa00-03f14749eb61", DebloatCatalog.Find("power.plan_ultimate")!.PowerPlan!.Base);
        Assert.Equal(DebloatPower.Choices.Values.Order(), plans.Select(i => i.Id).Order());
        // Windows' egne planer står aldrig på sletlisten
        var p = DebloatScript.BuildPlan(DebloatActions.Apply, [DebloatCatalog.Find("power.plan_ultimate")!]);
        Assert.Equal(DebloatPower.FleetdeckCopies, p.FleetPlans);
        Assert.DoesNotContain(DebloatPower.Balanced, p.FleetPlans);
        Assert.DoesNotContain(DebloatPower.High, p.FleetPlans);
        Assert.DoesNotContain(DebloatPower.Ultimate, p.FleetPlans);
        var e = DebloatScript.Engine;
        Assert.Contains("/duplicatescheme $pp.base $pp.copy", e);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(e, "powercfg.exe /delete"));
        Assert.Contains("foreach ($c in @($plan.fleetPlans))", e);
        Assert.False(p.RestorePoint);   // et skift af plan laver ikke et gendannelsespunkt
    }

    [Fact]
    public void Power_settings_use_known_guids_and_wol_is_never_turned_off()
    {
        const string guid = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
        foreach (var w in DebloatCatalog.Items.SelectMany(i => i.PowerSettings))
        {
            Assert.Matches(guid, w.Subgroup); Assert.Matches(guid, w.Setting);
            Assert.True(w.Ac is not null || w.Dc is not null);
        }
        var e = DebloatScript.Engine;
        Assert.DoesNotContain("-WakeOnMagicPacket Disabled", e);
        Assert.DoesNotContain("/devicedisablewake", e);
        Assert.Contains("-WakeOnMagicPacket Enabled -NoRestart", e);
        var wol = DebloatCatalog.Find("power.wol")!;
        Assert.True(wol.WakeOnLan);
        Assert.Contains(DebloatProfiles.Anbefalet, wol.Profiles);
    }

    [Fact]
    public void Windows_features_are_a_known_list_and_editions_are_respected()
    {
        var f = DebloatCatalog.Items.Where(i => i.Category == "features").ToList();
        Assert.Equal(["features.hyperv", "features.legacy_media", "features.netfx3", "features.nfs", "features.sandbox", "features.wsl"], f.Select(i => i.Id).Order());
        Assert.All(f, i => { Assert.NotEmpty(i.Features); Assert.Empty(i.Profiles); });
        foreach (var id in new[] { "features.hyperv", "features.sandbox", "features.nfs" })
            Assert.Equal(["pro", "enterprise"], DebloatCatalog.Find(id)!.Editions);
        var e = DebloatScript.Engine;
        Assert.Contains("Enable-WindowsOptionalFeature -Online -FeatureName $f.name -All -NoRestart", e);
        Assert.Contains("Disable-WindowsOptionalFeature -Online -FeatureName $f.name -NoRestart", e);
        Assert.Contains("$res.RestartNeeded", e);
    }

    [Fact]
    public void Exclusive_groups_hold_one_item_and_stay_out_of_the_profiles()
    {
        Assert.All(DebloatCatalog.Items.Where(i => i.Exclusive is not null), i => Assert.Empty(i.Profiles));
        Assert.Equal(["ads.tips", "power.plan_ultimate"], DebloatCatalog.KeepOnePerGroup(["power.plan_high", "ads.tips", "power.plan_ultimate"]));
        var both = DebloatCatalog.Resolve("", ["power.plan_high", "power.plan_ultimate", "power.sleep_30"], null).Select(i => i.Id).ToList();
        Assert.Equal(["power.plan_ultimate", "power.sleep_30"], both);
    }

    [Fact]
    public void Machines_on_an_old_profile_version_keep_what_they_chose()
    {
        // de tal, profilerne havde, før punkterne fra winutil kom til
        Assert.Equal(14, DebloatCatalog.ForProfile(DebloatProfiles.Let, 1).Count());
        Assert.Equal(45, DebloatCatalog.ForProfile(DebloatProfiles.Anbefalet, 1).Count());
        Assert.Equal(61, DebloatCatalog.ForProfile(DebloatProfiles.Maks, 1).Count());
        Assert.All(DebloatCatalog.ForProfile(DebloatProfiles.Maks, 1), i => Assert.Equal(1, i.ProfileSince));
        // og dem, de har nu (står også i docs/debloat.md)
        Assert.Equal(101, DebloatCatalog.Items.Length);
        Assert.Equal(13, DebloatCatalog.Categories.Length);
        Assert.Equal(14, DebloatCatalog.ForProfile(DebloatProfiles.Let).Count());
        Assert.Equal(54, DebloatCatalog.ForProfile(DebloatProfiles.Anbefalet).Count());
        Assert.Equal(71, DebloatCatalog.ForProfile(DebloatProfiles.Maks).Count());
        var fresh = DebloatCatalog.NewInProfile(DebloatProfiles.Anbefalet, 1).Select(i => i.Id).ToList();
        Assert.Contains("power.wol", fresh);
        Assert.Contains("ux.file_extensions", fresh);
        Assert.Empty(DebloatCatalog.NewInProfile(DebloatProfiles.Anbefalet, DebloatProfiles.Current));
        Assert.Empty(DebloatCatalog.NewInProfile(DebloatProfiles.Let, 1));   // Let er uændret
        Assert.DoesNotContain(DebloatCatalog.Resolve(DebloatProfiles.Anbefalet, null, null, 1), i => i.ProfileSince > 1);
        Assert.Contains(DebloatCatalog.Resolve(DebloatProfiles.Anbefalet, null, null), i => i.Id == "power.wol");
        // nye punkter i en profil er markeret med den version, de kom i
        Assert.All(DebloatCatalog.Items.Where(i => i.ProfileSince > 1), i => { Assert.NotEmpty(i.Profiles); Assert.InRange(i.ProfileSince, 2, DebloatProfiles.Current); });
    }

    [Fact]
    public void Profiles_are_nested_and_onedrive_is_opt_in()
    {
        var let = DebloatCatalog.ForProfile(DebloatProfiles.Let).Select(i => i.Id).ToHashSet();
        var rec = DebloatCatalog.ForProfile(DebloatProfiles.Anbefalet).Select(i => i.Id).ToHashSet();
        var max = DebloatCatalog.ForProfile(DebloatProfiles.Maks).Select(i => i.Id).ToHashSet();
        Assert.Subset(rec, let);
        Assert.Subset(max, rec);
        Assert.True(let.Count < rec.Count && rec.Count < max.Count);
        // Let: kun sikre punkter, ingen apps, tjenester eller opgaver
        Assert.All(DebloatCatalog.ForProfile(DebloatProfiles.Let), i =>
        {
            Assert.Equal(DebloatRisk.Safe, i.Risk);
            Assert.Empty(i.Apps); Assert.Empty(i.Services); Assert.Empty(i.Tasks);
        });
        // Anbefalet: ingen avancerede punkter; men telemetri, reklamer, Copilot/Recall, websøgning og apps er med
        Assert.DoesNotContain(DebloatCatalog.ForProfile(DebloatProfiles.Anbefalet), i => i.Risk == DebloatRisk.Advanced);
        foreach (var id in new[] { "telemetry.diagnostic_data", "ads.start_suggestions", "ai.copilot", "ai.recall", "search.web", "widgets.news", "apps.clipchamp" })
            Assert.Contains(id, rec);
        // Maks tilføjer de avancerede
        Assert.Contains(DebloatCatalog.ForProfile(DebloatProfiles.Maks), i => i.Risk == DebloatRisk.Advanced);
        // OneDrive er aldrig med som standard
        Assert.Empty(DebloatCatalog.Find("extra.onedrive")!.Profiles);
        Assert.Contains(DebloatCatalog.Find("extra.onedrive")!, DebloatCatalog.Items);
    }

    [Fact]
    public void Resolve_combines_profile_include_and_exclude()
    {
        Assert.Empty(DebloatCatalog.Resolve("", null, null));
        Assert.Empty(DebloatCatalog.Resolve(null, ["nope"], null));
        var custom = DebloatCatalog.Resolve("", ["extra.onedrive", "ads.tips"], null).Select(i => i.Id).ToList();
        Assert.Equal(["ads.tips", "extra.onedrive"], custom);   // katalogets rækkefølge
        var rec = DebloatCatalog.Resolve(DebloatProfiles.Anbefalet, ["EXTRA.ONEDRIVE"], ["apps.clipchamp", "telemetry.diagtrack"]).Select(i => i.Id).ToList();
        Assert.Contains("extra.onedrive", rec);
        Assert.DoesNotContain("apps.clipchamp", rec);
        Assert.DoesNotContain("telemetry.diagtrack", rec);
        Assert.Equal(DebloatCatalog.ForProfile(DebloatProfiles.Anbefalet).Count() - 1, rec.Count);
        // fravalg vinder over tilvalg
        Assert.Empty(DebloatCatalog.Resolve("", ["ads.tips"], ["ads.tips"]));
    }

    [Fact]
    public void Like_matches_like_powershell()
    {
        Assert.True(DebloatCatalog.Like("king.com.CandyCrushSaga", "King.com.*"));
        Assert.True(DebloatCatalog.Like("828B5831.HiddenCity", "*.HiddenCity"));
        Assert.False(DebloatCatalog.Like("Microsoft.WindowsStore", "*.HiddenCity"));
        Assert.False(DebloatCatalog.Like("Microsoft.BingNewsX", "Microsoft.BingNews"));
        Assert.True(DebloatCatalog.Like("a.b+c", "a.b+c"));   // ingen regex-tegn slipper igennem
    }
}

public class DebloatScriptTests
{
    private static DebloatItem Item(string id) => DebloatCatalog.Find(id)!;

    /// <summary>
    /// Gemmer motoren som .ps1 til syntakstjek i Windows PowerShell 5.1 (sæt WS_EXPORT_SCRIPTS). Tjek-scriptet har kun punkter, der ikke
    /// indlæser brugerprofiler, så det kan køres på en rigtig pc uden at røre noget.
    /// </summary>
    [Fact]
    public void ExportEngineForSyntaxCheck()
    {
        var dir = Environment.GetEnvironmentVariable("WS_EXPORT_SCRIPTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var bom = new System.Text.UTF8Encoding(true);
        File.WriteAllText(Path.Combine(dir, "debloat-engine.ps1"), DebloatScript.Engine, bom);
        var machineOnly = DebloatCatalog.Items.Where(i => i.Registry.All(r => r.Hive == RegHive.Machine));
        File.WriteAllText(Path.Combine(dir, "debloat-detect.ps1"), DebloatScript.Build(DebloatScript.BuildPlan(DebloatActions.Detect, machineOnly)), new System.Text.UTF8Encoding(false));
    }

    [Fact]
    public void Engine_is_embedded_and_ends_with_report()
    {
        var e = DebloatScript.Engine;
        Assert.Contains(DebloatScript.Marker, e);
        Assert.Contains("reg.exe load", e);
        Assert.Contains("reg.exe unload", e);
        Assert.Contains("Remove-AppxProvisionedPackage", e);
        Assert.Contains("Checkpoint-Computer", e);
        Assert.Contains("} finally {", e);   // indlæste profiler lukkes også ved fejl
        Assert.DoesNotContain("hosts", e, StringComparison.OrdinalIgnoreCase);
    }

    // Fejlene fra en rigtig Windows 11 26H2-pc: pakken var allerede væk fra standardbilledet
    [Theory]
    [InlineData("Den angivne fil blev ikke fundet.")]
    [InlineData("Den angivne sti blev ikke fundet.")]
    [InlineData("The system cannot find the file specified.")]
    [InlineData("The system cannot find the path specified.")]
    [InlineData("Remove-AppxProvisionedPackage failed 0x80070002")]
    [InlineData("Remove-AppxProvisionedPackage failed 0x80070003")]
    [InlineData("Deployment failed 0x80073CF1")]
    public void Deprovisioning_a_missing_package_counts_as_done(string error) =>
        Assert.Matches(DebloatCatalog.AlreadyGonePattern, error);

    [Theory]
    [InlineData("Adgang nægtet. 0x80070005")]
    [InlineData("Access is denied.")]
    [InlineData("The package is in use 0x80073D02")]
    [InlineData("Der opstod en fejl under fjernelsen 0x80004005")]
    public void Real_removal_errors_are_still_reported(string error) =>
        Assert.DoesNotMatch(DebloatCatalog.AlreadyGonePattern, error);

    [Fact]
    public void Engine_only_deprovisions_what_is_still_in_the_image()
    {
        var e = DebloatScript.Engine;
        Assert.Contains("[string]$plan.alreadyGone", e);
        // listen læses igen efter fjernelsen, og filerne skal findes, før en post tæller som "i billedet"
        var reload = e.IndexOf("if ($f[0].Count -gt 0) { ReloadProvisioned }", StringComparison.Ordinal);
        var deprovision = e.IndexOf("Remove-AppxProvisionedPackage -Online -PackageName", StringComparison.Ordinal);
        Assert.InRange(reload, 0, deprovision);
        Assert.Contains("foreach ($pkg in @((Found $a.name)[1]))", e);
        Assert.Contains("(ProvLive $_)", e);
        Assert.Contains("if (-not (IsGone $_)) { Err $it.id \"$($pkg.DisplayName) (standardbillede)", e);
        var plan = DebloatScript.BuildPlan(DebloatActions.Apply, [Item("apps.clipchamp")]);
        Assert.Equal(DebloatCatalog.AlreadyGonePattern, plan.AlreadyGone);
    }

    [Fact]
    public void Script_job_contains_one_line_plan_before_engine()
    {
        var body = DebloatScript.Build(DebloatScript.BuildPlan(DebloatActions.Apply, [Item("ads.tips"), Item("apps.clipchamp")]));
        Assert.StartsWith("$plan = @'\r\n{", body);
        var json = body.Split("\r\n")[1];
        Assert.DoesNotContain("'@", json);
        Assert.All(json, c => Assert.True(c < 128, "planen skal være ASCII"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("apply", root.GetProperty("action").GetString());
        Assert.True(root.GetProperty("restorePoint").GetBoolean());
        Assert.True(root.GetProperty("loadOffline").GetBoolean());
        Assert.Contains("Microsoft.WindowsStore", root.GetProperty("protected").EnumerateArray().Select(x => x.GetString()));
        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["ads.tips", "apps.clipchamp"], items.Select(i => i.GetProperty("id").GetString()));
        var reg = items[0].GetProperty("reg")[0];
        Assert.Equal("HKCU", reg.GetProperty("hive").GetString());
        Assert.Equal(0, reg.GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, reg.GetProperty("before").ValueKind);
        Assert.Equal("Clipchamp.Clipchamp", items[1].GetProperty("appx")[0].GetProperty("name").GetString());
        Assert.EndsWith(DebloatScript.Engine, body);
    }

    [Fact]
    public void Detect_plan_is_read_only_and_skips_offline_profiles()
    {
        var p = DebloatScript.BuildPlan(DebloatActions.Detect, DebloatCatalog.Items);
        Assert.False(p.RestorePoint);
        Assert.False(p.LoadOffline);
        Assert.Equal(DebloatCatalog.Items.Length, p.Items.Count);
        Assert.Throws<ArgumentException>(() => DebloatScript.BuildPlan("format-c", []));
    }

    [Fact]
    public void Undo_plan_carries_before_values_per_profile()
    {
        var item = Item("ads.tips");
        var op = item.Registry[0];
        var snaps = new Dictionary<string, DebloatSnapshot>
        {
            [item.Id] = new()
            {
                Reg =
                {
                    [$"{op.Key}@S-1-5-21-1-2-3-1001"] = new RegBefore { E = true, Ke = true, T = "DWord", V = JsonDocument.Parse("1").RootElement },
                    [$"{op.Key}@DEFAULT"] = new RegBefore { E = false, Ke = false },
                },
            },
        };
        var p = DebloatScript.BuildPlan(DebloatActions.Undo, [item], snaps);
        var before = p.Items[0].Reg[0].Before!;
        Assert.Equal(2, before.Count);
        Assert.True(before["S-1-5-21-1-2-3-1001"].E);
        Assert.False(before["DEFAULT"].E);
        Assert.Null(p.Items[0].Reg[1].Before is { Count: > 0 } ? "x" : null);   // andet op uden billede
        var json = DebloatScript.PlanToJson(p);
        Assert.Contains("\"S-1-5-21-1-2-3-1001\":{\"e\":true,\"ke\":true,\"t\":\"DWord\",\"v\":1}", json);
    }

    [Fact]
    public void Services_and_tasks_carry_before_on_undo()
    {
        var snaps = new Dictionary<string, DebloatSnapshot>
        {
            ["telemetry.diagtrack"] = new() { Svc = { ["DiagTrack"] = 3 } },
            ["telemetry.ceip_tasks"] = new() { Task = { [@"\Microsoft\Windows\Autochk\Proxy"] = false } },
        };
        var p = DebloatScript.BuildPlan(DebloatActions.Undo, [Item("telemetry.diagtrack"), Item("telemetry.ceip_tasks")], snaps);
        Assert.Equal(3, p.Items[0].Svc[0].Before);
        Assert.Equal(2, p.Items[0].Svc[0].Def);
        Assert.False(p.Items[1].Task.Single(t => t.Name == "Proxy").Before);
        Assert.Null(p.Items[1].Task.Single(t => t.Name == "Consolidator").Before);
    }

    private const string Output = """
        Gendannelsespunkt oprettet.
        OK  Tips: slået fra (2 ændret)
        ##FLEETDECK-DEBLOAT## {"v":1,"action":"apply","build":22631,"edition":"Professional","editionGroup":"pro","server":false,"restorePoint":"created","profiles":2,"items":[{"id":"ads.tips","state":"applied","changed":2,"skipped":0,"errors":[],"before":{"reg":[{"k":"hkcu|software\\microsoft\\windows\\currentversion\\contentdeliverymanager|subscribedcontent-338389enabled","h":"S-1-5-21-9","e":true,"ke":true,"t":"DWord","v":1},{"k":"hkcu|x|y","h":"DEFAULT","e":false,"ke":false,"t":null,"v":null}],"svc":[],"task":[],"appx":[]}},{"id":"apps.clipchamp","state":"applied","changed":1,"skipped":0,"errors":[],"before":{"reg":[],"svc":[],"task":[],"appx":["Clipchamp.Clipchamp"]}},{"id":"telemetry.diagtrack","state":"error","changed":0,"skipped":0,"errors":["Adgang nægtet"],"before":{"reg":[],"svc":[{"name":"DiagTrack","start":2}],"task":[],"appx":[]}}]}
        """;

    [Fact]
    public void Undo_plan_carries_power_and_feature_before_values()
    {
        using var doc = JsonDocument.Parse("""{"active":"381b4222-f694-41f0-9685-ff5bb260df2e"}""");
        var snap = new DebloatSnapshot();
        snap.Ext[DebloatScript.PlanKey] = doc.RootElement.Clone();
        var plan = DebloatScript.BuildPlan(DebloatActions.Undo, [Item("power.plan_ultimate")], new Dictionary<string, DebloatSnapshot> { ["power.plan_ultimate"] = snap });
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", plan.Items[0].PowerPlan!.Before!.Value.GetProperty("active").GetString());
        var apply = DebloatScript.BuildPlan(DebloatActions.Apply, [Item("power.plan_ultimate")]);
        Assert.Null(apply.Items[0].PowerPlan!.Before);
        Assert.Equal("Ultimativ ydelse", apply.Items[0].Label);
        Assert.Equal("Strømplan", apply.Items[0].Title);
        Assert.Equal("installeret", DebloatScript.BuildPlan(DebloatActions.Apply, [Item("features.wsl")]).Items[0].Label);
    }

    [Fact]
    public void Switching_power_plan_keeps_the_original_plan_for_undo()
    {
        JsonElement Plan(string g) { using var doc = JsonDocument.Parse($$"""{"active":"{{g}}"}"""); return doc.RootElement.Clone(); }
        DebloatReport Rep(string id, string before) => new()
        {
            Action = "apply", Power = new DebloatPowerInfo { Active = "x", Name = "Plan" },
            Items = [new DebloatItemReport { Id = id, State = "applied", Before = new DebloatBeforeReport { Ext = [new DebloatExtRow { K = DebloatScript.PlanKey, V = Plan(before) }] } }],
        };
        var d = new MachineDebloat { Include = ["power.plan_high"] };
        DebloatService.Record(d, Rep("power.plan_high", "original"), DateTime.UtcNow);
        DebloatService.Record(d, Rep("power.plan_ultimate", "high"), DateTime.UtcNow);
        Assert.Equal(["power.plan_ultimate"], d.Applied);
        Assert.Empty(d.Include);
        var snaps = Json.Des<Dictionary<string, DebloatSnapshot>>(d.BeforeJson);
        Assert.Equal("original", Assert.Single(snaps).Value.Ext[DebloatScript.PlanKey].GetProperty("active").GetString());
        Assert.Equal("Plan", Json.Des<DebloatPowerInfo>(d.PowerJson).Name);
    }

    [Fact]
    public void Report_is_parsed_and_stripped()
    {
        var r = DebloatScript.Parse(Output)!;
        Assert.Equal("apply", r.Action);
        Assert.Equal(22631, r.Build);
        Assert.Equal(3, r.Items.Count);
        Assert.Equal("S-1-5-21-9", r.Items[0].Before.Reg[0].H);
        Assert.Equal(1, r.Items[0].Before.Reg[0].V!.Value.GetInt32());
        Assert.Null(DebloatScript.Parse("ingen rapport her"));
        Assert.Null(DebloatScript.Parse(DebloatScript.Marker + " {ødelagt"));
        var clean = DebloatScript.StripReport(Output);
        Assert.DoesNotContain(DebloatScript.Marker, clean);
        Assert.EndsWith("(2 ændret)", clean);
    }

    [Fact]
    public void Merge_never_overwrites_the_first_snapshot()
    {
        var first = DebloatScript.Parse(Output)!.Items[0].Before;
        var snap = DebloatScript.Merge(null, first, DateTime.UtcNow);
        var later = new DebloatBeforeReport { Reg = [new DebloatRegRow { K = first.Reg[0].K, H = "S-1-5-21-9", E = true, T = "DWord", V = JsonDocument.Parse("0").RootElement }] };
        DebloatScript.Merge(snap, later, DateTime.UtcNow);
        Assert.Equal(1, snap.Reg[$"{first.Reg[0].K}@S-1-5-21-9"].V!.Value.GetInt32());
        Assert.Equal(2, snap.Reg.Count);
    }

    [Fact]
    public void Record_tracks_applied_drift_and_undo()
    {
        var d = new MachineDebloat { MachineId = 1, Enforce = true };
        var now = DateTime.UtcNow;
        DebloatService.Record(d, DebloatScript.Parse(Output)!, now);
        Assert.Equal(["ads.tips", "apps.clipchamp"], d.Applied);
        Assert.Empty(d.Drift);
        Assert.Equal(22631, d.Build);
        Assert.Equal("created", d.RestorePoint);
        var snaps = Json.Des<Dictionary<string, DebloatSnapshot>>(d.BeforeJson);
        Assert.Equal(2, snaps["telemetry.diagtrack"].Svc["DiagTrack"]);   // før-værdien gemmes, selv om punktet fejlede
        Assert.Single(DebloatScript.Reinstallable(DebloatCatalog.Find("apps.clipchamp")!, snaps["apps.clipchamp"]));

        // en Windows-opdatering slår tips til igen
        var scan = new DebloatReport { Action = "detect", Build = 26100, Items = [new() { Id = "ads.tips", State = "not_applied" }, new() { Id = "apps.clipchamp", State = "applied" }] };
        DebloatService.Record(d, scan, now.AddDays(1));
        Assert.Equal(["ads.tips"], d.Drift);
        Assert.Equal(26100, d.Build);
        Assert.NotNull(d.LastScan);

        // fortryd: punktet og dets billede forsvinder
        var undo = new DebloatReport { Action = "undo", Items = [new() { Id = "ads.tips", State = "not_applied" }] };
        DebloatService.Record(d, undo, now.AddDays(2));
        Assert.Equal(["apps.clipchamp"], d.Applied);
        Assert.Empty(d.Drift);
        Assert.False(Json.Des<Dictionary<string, DebloatSnapshot>>(d.BeforeJson).ContainsKey("ads.tips"));
    }

    [Fact]
    public void Drift_alert_respects_mode()
    {
        var now = DateTime.UtcNow;
        var d = new MachineDebloat { Enforce = true, EnforceMode = DebloatEnforce.Alert, Drift = ["ads.tips"], LastScan = now };
        Assert.Equal("debloat_drift", DebloatService.DriftFinding(d, now)!.Key);
        Assert.Null(DebloatService.DriftFinding(new MachineDebloat { Enforce = false, Drift = ["ads.tips"] }, now));
        Assert.Null(DebloatService.DriftFinding(null, now));
        // genanvend: ingen alarm før der er forsøgt; alarm når genanvendelsen ikke hjalp
        d.EnforceMode = DebloatEnforce.Reapply;
        Assert.Null(DebloatService.DriftFinding(d, now));
        d.LastReapply = now.AddMinutes(-5); d.LastApplied = now;
        Assert.NotNull(DebloatService.DriftFinding(d, now));
    }

    [Theory]
    [InlineData("22631.4317", 22631)]
    [InlineData("26100", 26100)]
    [InlineData(null, null)]
    [InlineData("ukendt", null)]
    public void Build_is_read_from_facts(string? s, int? want) => Assert.Equal(want, DebloatService.BuildOf(s));
}

public sealed class DebloatServiceTests : IDisposable
{
    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly AppDb db;
    private readonly DebloatService svc;

    public DebloatServiceTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var live = new Live(new NoHub<UiHub>(), new NoHub<AgentHub>());
        svc = new DebloatService(db, new JobService(db, live), live);
        db.Machines.Add(new Machine { Id = 1, Name = "PC1", Platform = Platforms.Windows, LastSeen = DateTime.UtcNow });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    [Fact]
    public async Task Apply_creates_system_script_job_and_winget_uninstall()
    {
        var m = await db.Machines.FindAsync(1);
        await svc.Configure(1, new DebloatConfig(DebloatProfiles.Let, ["extra.onedrive"], ["ads.tips"], true, DebloatEnforce.Reapply));
        var jobs = await svc.Apply(m!, null, "admin");
        Assert.Equal(2, jobs.Count);
        var script = jobs[0];
        Assert.Equal(JobTypes.Script, script.Type);
        Assert.Equal(RunAs.System, script.RunAs);
        Assert.Equal(DebloatService.Source, script.Source);
        Assert.Equal("admin", script.CreatedBy);
        var prm = Json.Des<JobParams>(script.ParamsJson);
        Assert.Equal(60, prm.TimeoutMin);
        Assert.Contains("\"extra.onedrive\"", prm.Body);
        Assert.DoesNotContain("\"ads.tips\"", prm.Body);
        Assert.Equal(JobTypes.Uninstall, jobs[1].Type);
        Assert.Equal(RunAs.User, jobs[1].RunAs);
        Assert.Equal("Microsoft.OneDrive", Json.Des<JobParams>(jobs[1].ParamsJson).Identifier);
        Assert.True(await svc.Busy(1));
        Assert.False(await svc.Busy(1, script.Id));
    }

    [Fact]
    public async Task Existing_machines_keep_their_profile_until_someone_updates_it()
    {
        // en række fra før profilversioner: kolonnens standardværdi er 1
        db.Database.ExecuteSqlRaw("insert into MachineDebloats(MachineId,Profile,Include,Exclude,Enforce,EnforceMode,Applied,Drift,StateJson,BeforeJson) values(1,'anbefalet','[]','[]',0,'alert','[]','[]','{{}}','{{}}')");
        var d = await db.MachineDebloats.AsNoTracking().SingleAsync(x => x.MachineId == 1);
        Assert.Equal(1, d.ProfileVersion);
        Assert.Equal(45, DebloatService.Selection(d).Count);
        var m = await db.Machines.FindAsync(1);
        var body = Json.Des<JobParams>((await svc.Apply(m!, null, "admin"))[0].ParamsJson).Body;
        Assert.DoesNotContain("\"power.wol\"", body);
        // ændringer i egne valg opdaterer ikke profilen
        var same = await svc.Configure(1, new DebloatConfig(null, ["ads.tips"], null, null, null));
        Assert.Equal(1, same.ProfileVersion);
        var up = await svc.Configure(1, new DebloatConfig(null, null, null, null, null, UpgradeProfile: true));
        Assert.Equal(DebloatProfiles.Current, up.ProfileVersion);
        Assert.Contains(DebloatService.Selection(up), i => i.Id == "power.wol");
    }

    [Fact]
    public async Task Choosing_a_profile_takes_its_current_content()
    {
        var d = await svc.Configure(1, new DebloatConfig(DebloatProfiles.Maks, null, null, null, null));
        Assert.Equal(DebloatProfiles.Current, d.ProfileVersion);
        Assert.Contains(DebloatService.Selection(d), i => i.Id == "consumer.wpbt");
    }

    [Fact]
    public async Task Power_plan_choice_replaces_the_previous_one_and_runs_only_that_item()
    {
        var m = await db.Machines.FindAsync(1);
        await svc.Configure(1, new DebloatConfig(null, ["power.plan_high", "ads.tips"], null, null, null));
        var jobs = await svc.SetPowerPlan(m!, "power.plan_ultimate", "admin");
        var d = await svc.Get(1);
        Assert.Equal(["ads.tips", "power.plan_ultimate"], d.Include);
        var job = Assert.Single(jobs);
        var body = Json.Des<JobParams>(job.ParamsJson).Body!;
        Assert.Contains("\"power.plan_ultimate\"", body);
        Assert.DoesNotContain("\"ads.tips\"", body);
        Assert.Equal(RunAs.System, job.RunAs);
    }

    [Fact]
    public async Task Undo_restores_and_reinstalls_removed_store_apps_only()
    {
        var m = (await db.Machines.FindAsync(1))!;
        var d = new MachineDebloat { MachineId = 1 };
        db.MachineDebloats.Add(d);
        DebloatService.Record(d, new DebloatReport
        {
            Action = "apply",
            Items =
            [
                new() { Id = "apps.clipchamp", State = "applied", Before = new() { Appx = ["Clipchamp.Clipchamp"] } },
                new() { Id = "apps.bing_news", State = "applied" },   // fandtes ikke på pc'en: intet at geninstallere
                new() { Id = "apps.people", State = "applied", Before = new() { Appx = ["Microsoft.People"] } },   // kan ikke hentes igen
            ],
        }, DateTime.UtcNow);
        await db.SaveChangesAsync();
        var jobs = await svc.Undo(m, null, "admin");
        Assert.Equal(JobTypes.Script, jobs[0].Type);
        var installs = jobs.Skip(1).ToList();
        var install = Assert.Single(installs);
        Assert.Equal(JobTypes.Install, install.Type);
        Assert.Equal(RunAs.User, install.RunAs);
        var p = Json.Des<JobParams>(install.ParamsJson);
        Assert.Equal(PackageTypes.MsStore, p.PackageType);
        Assert.Equal("9P1J8S7CCWWT", p.Identifier);
        Assert.Contains("\"action\":\"undo\"", Json.Des<JobParams>(jobs[0].ParamsJson).Body);
    }

    [Fact]
    public async Task Undo_with_nothing_applied_does_nothing_and_validation_rejects_unknowns()
    {
        Assert.Empty(await svc.Undo((await db.Machines.FindAsync(1))!, null, "admin"));
        Assert.NotNull(DebloatService.Validate(new DebloatConfig("ekstrem", null, null, null, null)));
        Assert.NotNull(DebloatService.Validate(new DebloatConfig(null, ["findes.ikke"], null, null, null)));
        Assert.NotNull(DebloatService.Validate(new DebloatConfig(null, null, null, true, "slet-alt")));
        Assert.Null(DebloatService.Validate(new DebloatConfig(DebloatProfiles.Maks, ["extra.onedrive"], [], true, DebloatEnforce.Alert)));
    }

    [Fact]
    public async Task Result_with_drift_reapplies_once_when_enforced()
    {
        var m = (await db.Machines.FindAsync(1))!;
        await svc.Configure(1, new DebloatConfig(DebloatProfiles.Let, null, null, true, DebloatEnforce.Reapply));
        var d = await db.MachineDebloats.SingleAsync();
        d.Applied = ["ads.tips"];
        await db.SaveChangesAsync();
        var scan = await svc.Scan(m, DebloatService.EnforcerName);
        var output = "OK\n" + DebloatScript.Marker + " " + JsonSerializer.Serialize(new { v = 1, action = "detect", build = 26100, items = new[] { new { id = "ads.tips", state = "not_applied" } } });
        scan.Status = JobStatus.Success;
        var clean = await svc.HandleResult(m, scan, output);
        Assert.Equal("OK", clean);
        var reapply = await db.Jobs.Where(j => j.Id != scan.Id).ToListAsync();
        var job = Assert.Single(reapply);
        Assert.Equal(DebloatService.EnforcerName, job.CreatedBy);
        Assert.Contains("\"ads.tips\"", Json.Des<JobParams>(job.ParamsJson).Body);
        // samme dag igen: ingen ny genanvendelse (det bliver en alarm i stedet)
        job.Status = JobStatus.Success;
        await db.SaveChangesAsync();
        await svc.HandleResult(m, scan, output);
        Assert.Equal(2, await db.Jobs.CountAsync());
        Assert.NotNull(DebloatService.DriftFinding(await db.MachineDebloats.SingleAsync(), DateTime.UtcNow));
    }

    [Fact]
    public async Task Tick_scans_enforced_online_machines_once_a_day()
    {
        await svc.Configure(1, new DebloatConfig(DebloatProfiles.Anbefalet, null, null, true, null));
        await svc.Tick(DateTime.UtcNow);
        Assert.Equal(1, await db.Jobs.CountAsync());
        await svc.Tick(DateTime.UtcNow);   // kører allerede
        Assert.Equal(1, await db.Jobs.CountAsync());
        var d = await db.MachineDebloats.SingleAsync();
        (await db.Jobs.SingleAsync()).Status = JobStatus.Success;
        d.LastScan = DateTime.UtcNow; d.Build = 22631;
        await db.SaveChangesAsync();
        await svc.Tick(DateTime.UtcNow);
        Assert.Equal(1, await db.Jobs.CountAsync());
        // ny Windows-build: tjek med det samme
        var m = await db.Machines.SingleAsync();
        m.FactsJson = Json.Ser(new SystemFacts { Build = "26100.1" });
        await db.SaveChangesAsync();
        await svc.Tick(DateTime.UtcNow);
        Assert.Equal(2, await db.Jobs.CountAsync());
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

public class DebloatEndpointTests
{
    [Fact]
    public void Every_debloat_endpoint_requires_admin()
    {
        var b = WebApplication.CreateBuilder();
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite("Data Source=:memory:"));
        b.Services.AddSignalR();
        b.Services.AddSingleton<Live>();
        b.Services.AddScoped<JobService>();
        b.Services.AddScoped<AuditService>();
        b.Services.AddScoped<DebloatService>();
        b.Services.AddAuthorization();
        var app = b.Build();
        app.MapDebloatEndpoints();
        var eps = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.Equal(7, eps.Count);   // scan|apply|undo deler ét endepunkt
        foreach (var e in eps)
        {
            Assert.StartsWith("/api/debloat", e.RoutePattern.RawText);
            var auth = e.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.Contains(auth, a => a.Policy == "admin");
            Assert.Null(e.Metadata.GetMetadata<IAllowAnonymous>());
        }
    }

    [Fact]
    public void Fleet_category_states_and_per_profile_status()
    {
        var d = new MachineDebloat { Profile = DebloatProfiles.Let };
        DebloatService.Record(d, new DebloatReport
        {
            Action = "apply",
            Items = DebloatCatalog.ForProfile(DebloatProfiles.Let).Select(i => new DebloatItemReport
                { Id = i.Id, State = "applied", Hives = [new() { H = "S-1-5-21-9", N = "bruger", Ok = true }, new() { H = "DEFAULT", Ok = true }] }).ToList(),
        }, DateTime.UtcNow);
        d.Drift = ["ads.tips"];
        var cats = DebloatEndpoints.CategoryStates(d);
        Assert.Equal("drift", cats["ads"]);
        Assert.Equal("ok", cats["telemetry"]);
        Assert.Equal("off", cats["apps"]);
        Assert.Equal("bruger", Json.Des<Dictionary<string, DebloatItemState>>(d.StateJson)["ads.tips"].Hives[0].N);
    }

    [Fact]
    public void Catalog_view_exposes_reversibility()
    {
        var json = JsonSerializer.Serialize(DebloatEndpoints.CatalogView(), Json.Web);
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(DebloatCatalog.Items.Length, items.Count);
        var people = items.Single(i => i.GetProperty("id").GetString() == "apps.people");
        Assert.False(people.GetProperty("fullyReversible").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("profiles").GetArrayLength());
    }
}
