using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>SD-kort/eMMC (fx en Raspberry Pi) og virtuelle enheder kan ikke give SMART – så foreslås værktøjet aldrig.</summary>
public class DiskSmartPiTests
{
    [Theory]
    [InlineData("mmcblk0", false)]
    [InlineData("/dev/mmcblk1", false)]
    [InlineData("zram0", false)]
    [InlineData("loop3", false)]
    [InlineData("md0", false)]
    [InlineData("dm-1", false)]
    [InlineData("sr0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("sda", true)]
    [InlineData("nvme0n1", true)]
    [InlineData("/dev/sdb", true)]
    public void CanHaveSmart(string? name, bool expected) => Assert.Equal(expected, DiskSmart.CanHaveSmart(name));

    [Fact]
    public void TheDiskToolsHelperSkipsSdOnlyMachinesAndQuietsIdleServices()
    {
        var s = Fleetdeck.Server.Services.HelperCatalog.DiskToolsLinux;
        Assert.Contains("grep -Ev '^(mmcblk|zram|loop|ram|md|dm-|nbd|sr|fd|zd)'", s);
        Assert.Contains("intet at installere", s);
        Assert.Contains("systemctl mask \"$u\"", s);
        Assert.Contains("nvmf-autoconnect.service", s);
        Assert.Contains("smartctl --scan", s);
    }
}
