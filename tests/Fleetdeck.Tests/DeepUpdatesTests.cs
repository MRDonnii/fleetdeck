using Fleetdeck.Agent.Windows;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Den dybe opdateringsscanning (Windows-agenten): versionsammenligning, id'ernes præfikser og AMD-driveren fra programlisten.</summary>
public class DeepUpdatesTests
{
    [Theory]
    [InlineData("26.11.2", "26.10.1", true)]
    [InlineData("26.10.1", "26.11.2", false)]
    [InlineData("16.0.20625.20108", "16.0.20527.20194", true)]
    [InlineData("16.0.20625.20108", "16.0.20625.20108", false)]
    [InlineData("26.11", "26.11.0", false)]
    [InlineData("26.11.1", "26.11", true)]
    [InlineData("", "1.0", false)]
    [InlineData("ukendt", "1.0", false)]
    public void Newer_compares_numbers_part_by_part(string a, string b, bool newer) => Assert.Equal(newer, DeepUpdates.Newer(a, b));

    [Fact]
    public void Deep_ids_are_recognised_and_winget_ids_are_not()
    {
        Assert.True(DeepUpdates.IsDeep("wu:0b3a1c7e-1111-2222-3333-444455556666"));
        Assert.True(DeepUpdates.IsDeep(DeepUpdates.OfficeId));
        Assert.True(DeepUpdates.IsDeep(DeepUpdates.AmdId));
        Assert.False(DeepUpdates.IsDeep("Mozilla.Firefox"));
        Assert.False(DeepUpdates.IsDeep("gpu:nvidia"));   // kun de kilder, agenten selv kan håndtere
    }

    [Fact]
    public async Task No_amd_software_means_no_amd_line_and_no_network_call()
    {
        var software = new List<SoftwareEntry> { new() { Name = "AMD Chipset Software", Version = "7.01" }, new() { Name = "Firefox", Version = "131.0" } };
        Assert.Null(await DeepUpdates.Amd(software, CancellationToken.None));
    }

    [Fact]
    public void Amd_details_give_version_full_package_and_size()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            [{"externalbuildversion":"26.11.2","fullbuild":"https://www2.ati.com/drivers/whql-amd-software-adrenalin-edition-26.11.2-win11-c.exe","fullbuildfilesize":"1678.32180023193"}]
            """);
        var b = DeepUpdates.ParseAmdDetails(doc.RootElement)!;
        Assert.Equal("26.11.2", b.Version);
        Assert.EndsWith("26.11.2-win11-c.exe", b.Url);
        Assert.Equal(1678.32, b.SizeMb, 2);
        using var empty = System.Text.Json.JsonDocument.Parse("[]");
        Assert.Null(DeepUpdates.ParseAmdDetails(empty.RootElement));
    }

    [Theory]
    [InlineData("https://www2.ati.com/drivers/x.exe", true)]
    [InlineData("https://drivers.amd.com/drivers/x.exe", true)]
    [InlineData("http://www2.ati.com/drivers/x.exe", false)]
    [InlineData("https://www2.ati.com.evil.example/x.exe", false)]
    [InlineData("https://evil.example/www2.ati.com/x.exe", false)]
    [InlineData("", false)]
    public void Only_amds_own_https_servers_are_trusted(string url, bool ok) => Assert.Equal(ok, DeepUpdates.TrustedAmdUrl(url));

    [Theory]
    [InlineData("Valid|CN=Advanced Micro Devices, O=Advanced Micro Devices, S=California, C=US", true)]
    [InlineData("Valid|CN=Advanced Micro Devices Inc., O=\"Advanced Micro Devices, Inc.\", L=Santa Clara, C=US", true)]
    [InlineData("HashMismatch|CN=Advanced Micro Devices, O=Advanced Micro Devices, C=US", false)]
    [InlineData("NotSigned|", false)]
    [InlineData("Valid|CN=Evil, O=Advanced Micro Devices Fan Club, C=US", false)]
    [InlineData("Valid|CN=Evil, O=Evil Ltd, C=US", false)]
    public void Only_a_valid_amd_signature_is_accepted(string sig, bool ok) => Assert.Equal(ok, DeepUpdates.SignedByAmd(sig));

    [Fact]
    public void Amd_setup_result_codes_follow_amds_guide()
    {
        var ok = DeepUpdates.AmdResult(0, "ResultCode = 0\n[Details]\nPackage Name = Display Driver\nErrorCode = 0\n");
        Assert.True(ok.Ok); Assert.False(ok.Reboot);
        var reboot = DeepUpdates.AmdResult(0, "ResultCode = 0\n[Details]\nPackage Name = Display Driver\nErrorCode = 3\n");
        Assert.True(reboot.Ok); Assert.True(reboot.Reboot); Assert.Contains("Genstart", reboot.Text);
        var fail = DeepUpdates.AmdResult(1, "ResultCode = 1\n[Details]\nPackage Name = Display Driver\nErrorCode = 0\n[Details]\nPackage Name = AMD Software\nErrorCode = 1\n");
        Assert.False(fail.Ok); Assert.Contains("AMD Software", fail.Text); Assert.DoesNotContain("Display Driver", fail.Text);
        Assert.True(DeepUpdates.AmdResult(2, null).Reboot);
        Assert.False(DeepUpdates.AmdResult(null, null).Ok);
    }

    [Fact]
    public void A_new_agent_version_sends_inventory_once()
    {
        var f = Path.Combine(Path.GetTempPath(), $"fd-invver-{Guid.NewGuid():N}.txt");
        try
        {
            Assert.True(InventoryVersion.Changed(f, "087922147032"));    // aldrig sendt
            File.WriteAllText(f, "087922147032");
            Assert.False(InventoryVersion.Changed(f, "087922147032"));   // samme version: vent på den normale tid
            Assert.True(InventoryVersion.Changed(f, "1a2b3c4d5e6f"));    // ny version: send nu
            Assert.False(InventoryVersion.Changed(f, "dev"));
        }
        finally { File.Delete(f); }
    }
}
