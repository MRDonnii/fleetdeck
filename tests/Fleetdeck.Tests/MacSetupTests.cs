using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Opsætningsvinduet på en Mac, hvor agenten allerede er installeret: tilstanden øverst, udvidelserne og hvornår nøglen kræves.</summary>
public class MacSetupTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private static TrayStatus Installed(Action<TrayStatus>? f = null)
    {
        var s = new TrayStatus { Machine = "MACBOOK", Server = "http://192.168.1.5:8585", Connected = true, Enrolled = true, Profile = "Kontor",
            LastContact = Now.AddSeconds(-20), Written = Now, MacAccess = new MacAccessInfo { FullDiskAccess = true } };
        f?.Invoke(s);
        return s;
    }

    private static readonly ExtensionDef[] BuiltIn =
    [
        new("a", "A", "", "gives a", Mac: new("brew", "formula:a")),
        new("winonly", "W", "", "", Windows: new("winget", "x")),
        new("b", "B", "", "gives b", Mac: new("brew", "formula:b")),
        new("c", "C", "", "gives c", Mac: new("brew", "formula:c")),
    ];

    [Fact]
    public void SummaryShowsServerMachineProfileConnectionAndFullDiskAccess()
    {
        var rows = MacSetup.Summary(Installed(), Now).ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("http://192.168.1.5:8585", rows["Server"]);
        Assert.Equal("MACBOOK", rows["Maskine"]);
        Assert.Equal("Kontor", rows["Profil"]);
        Assert.StartsWith("Forbundet", rows["Forbindelse"]);
        Assert.Equal("Givet ✓", rows["Fuld diskadgang"]);
        Assert.StartsWith("Tilmeldt ✓", rows["Tilmelding"]);
        Assert.Equal(("Forbundet", 'o'), MacSetup.Headline(Installed(), Now));

        var none = MacSetup.Summary(Installed(s => { s.Profile = null; s.MacAccess = null; }), Now).ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("Ingen profil", none["Profil"]);
        Assert.Equal("Kunne ikke afgøres", none["Fuld diskadgang"]);
    }

    [Fact]
    public void SummaryAndHeadlineShowProblems()
    {
        var off = Installed(s => { s.Connected = false; s.Error = "Serveren svarer ikke"; });
        Assert.Contains("Serveren svarer ikke", MacSetup.Summary(off, Now).Single(r => r.Label == "Forbindelse").Value);
        Assert.Equal(("Ikke forbundet", 'b'), MacSetup.Headline(off, Now));
        Assert.Equal('w', MacSetup.Headline(Installed(s => s.MacAccess = new MacAccessInfo { FullDiskAccess = false }), Now).Kind);
        Assert.Equal("Mangler", MacSetup.Summary(Installed(s => s.MacAccess = new MacAccessInfo { FullDiskAccess = false }), Now).Single(r => r.Label == "Fuld diskadgang").Value);
        Assert.Equal('b', MacSetup.Headline(Installed(s => s.Written = Now.AddMinutes(-10)), Now).Kind);
        Assert.Equal(("Tjenesten kører ikke", 'b'), MacSetup.Headline(null, Now));
        Assert.Single(MacSetup.Summary(null, Now));
    }

    [Fact]
    public void TheStatusFileNeverCarriesTheKeyOnlyAFlag()
    {
        var status = System.Text.Json.JsonSerializer.Serialize(Installed(), TrayCommands.Json);
        Assert.Contains("\"enrolled\":true", status);
        Assert.DoesNotContain("key", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeyIsOnlyNeededForANewServerOrProfile()
    {
        var s = Installed();
        Assert.False(MacSetup.NeedsKey(s, "192.168.1.5", profileChanged: false));          // samme server, skrevet kort
        Assert.False(MacSetup.NeedsKey(s, "http://192.168.1.5:8585/", profileChanged: false));
        Assert.True(MacSetup.NeedsKey(s, "192.168.1.6", profileChanged: false));
        Assert.True(MacSetup.NeedsKey(s, "192.168.1.5", profileChanged: true));
        Assert.True(MacSetup.NeedsKey(null, "192.168.1.5", profileChanged: false));        // tjenesten kører ikke: installér igen
        Assert.True(MacSetup.NeedsKey(Installed(x => { x.Enrolled = false; x.Connected = false; x.LastContact = null; }), "192.168.1.5", false));
        // ældre agent uden "enrolled", men forbundet: tilmeldt
        Assert.True(MacSetup.IsEnrolled(Installed(x => x.Enrolled = false)));
        Assert.False(MacSetup.ServerChanged("http://192.168.1.5:8585", ""));
    }

    [Fact]
    public void ProfileChangeComparesNamesWithNoProfileAsEmpty()
    {
        Assert.False(MacSetup.ProfileChanged("Kontor", "Kontor"));
        Assert.True(MacSetup.ProfileChanged("Kontor", "Hjem"));
        Assert.True(MacSetup.ProfileChanged("Kontor", null));
        Assert.False(MacSetup.ProfileChanged(null, ""));
        Assert.True(MacSetup.ProfileChanged("", "Kontor"));
    }

    [Fact]
    public void ExtensionRowsReflectTheRealInstalledState()
    {
        var s = Installed(x => x.Extensions =
        [
            new ExtensionView { Id = "b", Name = "B", Installed = true },
            new ExtensionView { Id = "c", Name = "C", Pending = true },
            new ExtensionView { Id = "new", Name = "Ny", Gives = "fra serveren" },
        ]);
        var rows = MacSetup.ExtensionRows(s, BuiltIn);
        Assert.Equal(["a", "b", "c", "new"], rows.Select(r => r.Id));   // kun Mac-udvidelser, i den indbyggede rækkefølge
        Assert.True(rows[0].CanInstall);
        Assert.False(rows[0].Installed);
        Assert.True(rows[1].Installed); Assert.False(rows[1].CanInstall); Assert.Equal("Installeret", rows[1].Note);
        Assert.True(rows[2].Pending); Assert.False(rows[2].CanInstall); Assert.Equal("Installeres…", rows[2].Note);
        Assert.Equal("gives a", rows[0].Note);

        // ingen status (ny installation): de indbyggede, intet installeret
        Assert.All(MacSetup.ExtensionRows(null, BuiltIn), r => Assert.True(r.CanInstall));
    }

    [Fact]
    public void OnlyNewlyEnabledExtensionsAreInstalled()
    {
        var rows = MacSetup.ExtensionRows(Installed(x => x.Extensions = [new ExtensionView { Id = "b", Installed = true }, new ExtensionView { Id = "c", Pending = true }]), BuiltIn);
        Assert.Equal(["a"], MacSetup.ToInstall(rows, ["a", "b", "c"]));
        Assert.Empty(MacSetup.ToInstall(rows, ["b"]));
    }

    [Fact]
    public void ExtensionStateIsRefreshedOftenWhileOneIsPending()
    {
        var idle = Installed();
        Assert.False(MacSetup.RefreshExtensions(idle, Now.AddMinutes(-5), Now));
        Assert.True(MacSetup.RefreshExtensions(idle, Now.AddMinutes(-11), Now));
        Assert.True(MacSetup.RefreshExtensions(idle, DateTime.MinValue, Now));
        var pending = Installed(x => x.Extensions = [new ExtensionView { Id = "a", Pending = true }]);
        Assert.True(MacSetup.RefreshExtensions(pending, Now.AddSeconds(-30), Now));
        Assert.False(MacSetup.RefreshExtensions(pending, Now.AddSeconds(-10), Now));
    }

    [Fact]
    public void ReinstallOnAnInstalledMacSendsAnExplicitExtensionList()
    {
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.Contains("--extensions ''", MacInstall.InstallScript("/tmp/a", sha, "http://s", "k", 0, [], null, exactExtensions: true));
        Assert.DoesNotContain("--extensions", MacInstall.InstallScript("/tmp/a", sha, "http://s", "k", 0, [], null));   // første installation: uændret
        Assert.Contains("--extensions 'a'", MacInstall.InstallScript("/tmp/a", sha, "http://s", "k", 0, ["a"], null, exactExtensions: true));
    }
}
