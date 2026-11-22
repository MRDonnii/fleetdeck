using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Tests;

public class MachineNameTests
{
    [Theory]
    [InlineData("  Kontor  ", "Kontor")]
    [InlineData("Stue   pc", "Stue pc")]
    [InlineData("Ærø-Bærbar", "Ærø-Bærbar")]
    public void CleansName(string raw, string expected)
    {
        Assert.Null(MachineNames.Validate(raw, out var clean));
        Assert.Equal(expected, clean);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\u0000b")]
    public void RejectsEmptyOrInvalid(string? raw) => Assert.NotNull(MachineNames.Validate(raw, out _));

    [Fact]
    public void LengthLimit()
    {
        Assert.Null(MachineNames.Validate(new string('x', 64), out _));
        Assert.NotNull(MachineNames.Validate(new string('x', 65), out _));
    }

    [Fact]
    public void TakenIgnoresCaseAndSelf()
    {
        var all = new[] { (1, "Kontor"), (2, "STUE-PC") };
        Assert.True(MachineNames.Taken(all, 2, "kontor"));
        Assert.True(MachineNames.Taken(all, 1, "stue-pc"));
        Assert.False(MachineNames.Taken(all, 1, "KONTOR"));   // sit eget navn med andre bogstaver
        Assert.False(MachineNames.Taken(all, 1, "Køkken"));
    }

    // maskinerne i "databasen": værtsnavn og evt. visningsnavn
    private static readonly Machine[] Fleet =
        [new() { Id = 1, Name = "DESKTOP-ABC123" }, new() { Id = 2, Name = "STUE-PC" }, new() { Id = 3, Name = "PI4", DisplayName = "Kiosk" }];

    private static (Machine M, IEnumerable<(int, string)> All) Pick(int id)
    {
        var copy = Fleet.Select(x => new Machine { Id = x.Id, Name = x.Name, DisplayName = x.DisplayName }).ToList();
        return (copy.Single(x => x.Id == id), copy.Select(x => (x.Id, x.Label)).ToList());
    }

    [Fact]
    public void RenameKeepsHostname()
    {
        var (m, all) = Pick(1);
        var r = MachineNames.Apply(m, all, "  Kontor  ");
        Assert.Equal(200, r.Status);
        Assert.Equal("DESKTOP-ABC123", r.Old);
        Assert.Equal("DESKTOP-ABC123", m.Name);   // agentens identitet er uændret
        Assert.Equal("Kontor", m.DisplayName);
        Assert.Equal("Kontor", m.Label);
    }

    [Theory]
    [InlineData("stue-pc", 409)]   // en andens værtsnavn
    [InlineData("KIOSK", 409)]     // en andens visningsnavn
    [InlineData(" ", 400)]
    public void RenameRejects(string name, int status)
    {
        var (m, all) = Pick(1);
        Assert.Equal(status, MachineNames.Apply(m, all, name).Status);
        Assert.Null(m.DisplayName);
    }

    [Fact]
    public void HostnameResetsDisplayName()
    {
        var (m, all) = Pick(3);
        var r = MachineNames.Apply(m, all, "PI4");
        Assert.Equal(200, r.Status);
        Assert.Equal("Kiosk", r.Old);
        Assert.Null(m.DisplayName);
        Assert.Equal("PI4", m.Label);
    }

    [Fact]
    public void SameNameDifferentCaseIsKept()
    {
        var (m, all) = Pick(2);
        Assert.Equal(200, MachineNames.Apply(m, all, "Stue-pc").Status);
        Assert.Equal("Stue-pc", m.DisplayName);
    }
}
