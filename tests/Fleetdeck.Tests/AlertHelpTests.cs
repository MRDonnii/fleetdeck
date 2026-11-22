using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Bedre alarmtekster og de nye rettelser "Anvend debloat igen" og "Start tjenesten".</summary>
public class AlertHelpTests
{
    [Fact]
    public void Drift_with_one_app_names_it_in_the_title_and_says_when()
    {
        var applied = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var f = DebloatService.DriftFinding(new MachineDebloat { Enforce = true, EnforceMode = DebloatEnforce.Alert, Drift = ["apps.office_hub"], LastApplied = applied }, DateTime.UtcNow)!;
        Assert.Equal("debloat_drift", f.Key);
        Assert.Equal("Microsoft 365-appen er kommet tilbage", f.Title);
        Assert.Equal("Fleetdeck fjernede den 18/11. Windows har lagt den ind igen – typisk efter en Windows-opdatering.", f.Detail);
    }

    [Fact]
    public void Drift_with_several_items_counts_them()
    {
        var f = DebloatService.DriftFinding(new MachineDebloat { Enforce = true, EnforceMode = DebloatEnforce.Alert, Drift = ["apps.office_hub", "ux.file_extensions"] }, DateTime.UtcNow)!;
        Assert.Equal("2 debloat-punkter er vendt tilbage", f.Title);
        Assert.StartsWith("Microsoft 365-appen, Filtypenavne.", f.Detail);
        Assert.Contains("ændrede dem", f.Detail);
    }

    [Fact]
    public void One_stopped_service_is_named_with_its_consequence()
    {
        var f = ServiceStart.Finding(["cups.service"], Platforms.Linux)!;
        Assert.Equal(("services", "Tjenesten cups kører ikke"), (f.Key, f.Title));
        Assert.Contains("printe", f.Detail);
        var many = ServiceStart.Finding(["cups.service", "kiosk.service (kiosk)", "Unraid-paritet: 3 fejl ved seneste tjek"], Platforms.Linux)!;
        Assert.Equal("2 tjenester er stoppet", many.Title);
        Assert.StartsWith("cups, kiosk (bruger kiosk).", many.Detail);
        Assert.EndsWith("Unraid-paritet: 3 fejl ved seneste tjek.", many.Detail);
        var unraid = ServiceStart.Finding(["Unraid-array: STOPPED"], Platforms.Linux)!;
        Assert.Equal("Unraid-arrayet er ikke startet", unraid.Title);
        Assert.StartsWith("Unraid-", unraid.Detail);
        Assert.Null(ServiceStart.Finding([], Platforms.Linux));
    }

    [Fact]
    public void Service_start_only_accepts_safe_names_from_the_health_data()
    {
        string[] failed = ["cups.service", "evil; rm -rf /.service", "x.service$(reboot)", "kiosk.service (kiosk)", "bad.service (root;id)", "sshd (OpenRC)", "ntpd (runit)", "Unraid-array: STOPPED"];
        var units = ServiceStart.Startable(failed, Platforms.Linux);
        Assert.Equal(["cups.service", "kiosk.service", "sshd", "ntpd"], units.Select(u => u.Name));
        Assert.Equal(["systemd", "user", "openrc", "runit"], units.Select(u => u.Init));
        var script = ServiceStart.Script(units, Platforms.Linux)!;
        Assert.Contains("systemctl start 'cups.service'", script);
        Assert.Contains("systemctl --user -M 'kiosk@' start 'kiosk.service'", script);
        Assert.Contains("rc-service 'sshd' restart", script);
        Assert.DoesNotContain("rm -rf", script);
        Assert.DoesNotContain("reboot", script);
        Assert.Null(ServiceStart.Script(ServiceStart.Startable(["Unraid-array: STOPPED"], Platforms.Linux), Platforms.Linux));
        Assert.Empty(ServiceStart.Startable(["cups.service"], Platforms.Mac));

        var win = ServiceStart.Script(ServiceStart.Startable(["Spooler", "x'; Stop-Computer; '"], Platforms.Windows), Platforms.Windows)!;
        Assert.Contains("Start-Service -Name 'Spooler'", win);
        Assert.DoesNotContain("Stop-Computer", win);
    }

    [Fact]
    public void Service_start_takes_at_most_the_services_the_alert_names()
    {
        var failed = Enumerable.Range(1, 12).Select(i => $"s{i}.service").ToList();
        Assert.Equal(ServiceStart.Max, ServiceStart.Startable(failed, Platforms.Linux).Count);
        Assert.Contains("…", ServiceStart.Finding(failed, Platforms.Linux)!.Detail);
    }

    [Fact]
    public void Important_title_names_a_single_update()
    {
        Assert.Equal("Vigtig opdatering venter: Google Chrome 131", HealthService.ImportantTitle(["Google Chrome 131"]));
        Assert.Equal("3 vigtige opdateringer venter", HealthService.ImportantTitle(["a", "b", "c"]));
    }

    [Fact]
    public void Advice_steps_join_into_what_for_older_views()
    {
        var a = HelperCatalog.AdviceFor("debloat_drift", "warning", Platforms.Windows)!;
        Assert.Equal("debloat_reapply", a.FixId);
        Assert.StartsWith("Tryk \"Ret det\"", a.Steps[0]);
        Assert.Equal(string.Join(" ", a.Steps), a.What);
        Assert.Null(HelperCatalog.AdviceFor("debloat_drift", "warning", Platforms.Linux)!.FixId);
    }
}
