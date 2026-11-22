using Fleetdeck.Agent.Windows;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>AMD Chipset Software i den dybe scanning: version fra AMD's sider, adressen, svaret fra AMD's server og resultatet.</summary>
public class AmdChipsetTests
{
    private const string Page = """
        <a href="https://drivers.amd.com/drivers/amd_chipset_software_8.09.28.1124.exe" class="btn">Download</a>
        <a href="/en/resources/support-articles/release-notes/RN-RYZEN-CHIPSET-8-09-28-1124.html">Release notes</a>
        <a href="/en/resources/support-articles/release-notes/RN-RYZEN-CHIPSET-7-01-08-129.html">Tidligere</a>
        <a href="https://drivers.amd.com/drivers/amd_chipset_software_6.10.17.152.exe">Gammel</a>
        """;

    [Fact]
    public void Candidates_take_versions_from_links_and_release_notes_newest_first()
    {
        var c = AmdChipset.Candidates([Page, "<a href=\"RN-RYZEN-CHIPSET-8-10-03-001.html\">nyere</a>"]);
        Assert.Equal(["8.10.03.001", "8.09.28.1124", "7.01.08.129", "6.10.17.152"], c);
    }

    [Fact]
    public void Candidates_ignore_pages_without_chipset_links()
    {
        Assert.Empty(AmdChipset.Candidates(["<html>ingen links</html>", ""]));
        Assert.Empty(AmdChipset.Candidates(["amd_chipset_software_latest.exe"]));
    }

    [Theory]
    [InlineData("8.09.28.1124", "https://drivers.amd.com/drivers/amd_chipset_software_8.09.28.1124.exe")]
    [InlineData("7.01.08.129", "https://drivers.amd.com/drivers/amd_chipset_software_7.01.08.129.exe")]
    [InlineData("8.09/../../x", null)]
    [InlineData("8", null)]
    [InlineData("", null)]
    public void Package_url_only_for_real_versions(string v, string? url) => Assert.Equal(url, AmdChipset.PackageUrl(v));

    [Fact]
    public void Package_must_come_from_amds_server_and_look_like_a_package()
    {
        var ok = new Uri("https://drivers.amd.com/drivers/amd_chipset_software_8.09.28.1124.exe");
        Assert.True(AmdChipset.PackageLooksRight(ok, "application/octet-stream", 81_551_288));
        // uden henviser omdirigerer AMD til en fejlside på www.amd.com
        Assert.False(AmdChipset.PackageLooksRight(new Uri("https://www.amd.com/en/resources/support-articles/faqs/Download-Incomplete.html"), "text/html", 150_000));
        Assert.False(AmdChipset.PackageLooksRight(ok, "text/html", 81_551_288));
        Assert.False(AmdChipset.PackageLooksRight(ok, "application/octet-stream", 2_000));
        Assert.False(AmdChipset.PackageLooksRight(ok, "application/octet-stream", null));
        Assert.False(AmdChipset.PackageLooksRight(new Uri("http://drivers.amd.com/drivers/amd_chipset_software_8.09.28.1124.exe"), null, 81_551_288));
        Assert.False(AmdChipset.PackageLooksRight(new Uri("https://drivers.amd.com.example.net/x.exe"), null, 81_551_288));
        Assert.False(AmdChipset.PackageLooksRight(null, null, 81_551_288));
    }

    [Fact]
    public void Installed_version_comes_from_the_program_list()
    {
        var sw = new List<SoftwareEntry>
        {
            new() { Name = "AMD Software", Version = "26.10.1" },
            new() { Name = "AMD Chipset Software", Version = "6.10.17.152" },
        };
        Assert.Equal("6.10.17.152", AmdChipset.Installed(sw));
        Assert.Null(AmdChipset.Installed([new() { Name = "Firefox", Version = "131.0" }]));
    }

    [Fact]
    public void Offer_only_when_newer_or_when_the_entry_vanished_but_the_folder_is_left()
    {
        var up = AmdChipset.Offer("6.10.17.152", false, "8.09.28.1124");
        Assert.NotNull(up);
        Assert.Equal(AmdChipset.Id, up!.Id);
        Assert.Equal("6.10.17.152", up.Current);
        Assert.Equal("8.09.28.1124", up.Available);
        Assert.Null(AmdChipset.Offer("8.09.28.1124", false, "8.09.28.1124"));
        Assert.Null(AmdChipset.Offer("8.10.1.1", false, "8.09.28.1124"));
        // posten forsvandt efter en fejlet installation som SYSTEM, men AMD's mappe er der stadig
        Assert.Equal(AmdChipset.Unknown, AmdChipset.Offer(null, true, "8.09.28.1124")!.Current);
        Assert.Null(AmdChipset.Offer(null, false, "8.09.28.1124"));
        Assert.Null(AmdChipset.Offer("6.10.17.152", false, "ikke-en-version"));
    }

    [Fact]
    public async Task No_chipset_software_means_no_line_and_no_network_call()
    {
        // ingen AMD Chipset Software og (på testmaskinen) ingen AMD-mappe: klienten må aldrig blive brugt
        using var http = new HttpClient(new Throwing());
        Assert.Null(await AmdChipset.Check([new() { Name = "AMD Software", Version = "26.10.1" }], http, CancellationToken.None));
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("netværket må ikke bruges");
    }

    [Fact]
    public void Chipset_id_is_a_deep_id()
    {
        Assert.True(DeepUpdates.IsDeep(AmdChipset.Id));
        Assert.False(DeepUpdates.IsDeep("chipset:intel"));
    }

    [Theory]
    [InlineData("6.10.17.152", false)]
    [InlineData("7.01.08.129", true)]
    [InlineData("8.09.28.1124", true)]
    [InlineData(AmdChipset.Unknown, false)]
    public void No_way_back_from_seven(string v, bool noWayBack) => Assert.Equal(noWayBack, AmdChipset.NoWayBack(v));

    [Theory]
    [InlineData("True", true)]
    [InlineData("True\r\n", true)]
    [InlineData("noget støj\nTrue", true)]
    [InlineData("False", false)]
    [InlineData("", false)]
    [InlineData("True\nFalse", false)]
    public void Admin_answer_is_the_last_line(string output, bool admin) => Assert.Equal(admin, AmdChipset.IsAdminAnswer(output));

    [Fact]
    public void Result_trusts_the_program_list_over_the_exit_code()
    {
        var ok = AmdChipset.Result(0, false, "6.10.17.152", "8.09.28.1124", "8.09.28.1124");
        Assert.True(ok.Ok);
        Assert.True(ok.Reboot);
        Assert.Contains("8.09.28.1124 er installeret", ok.Text);

        // en anden kode, men posten viser den nye version: lykkedes
        Assert.True(AmdChipset.Result(3010, false, "6.10.17.152", "8.09.28.1124", "8.09.28.1124").Ok);

        // kode 0, men posten er uændret
        var same = AmdChipset.Result(0, false, "6.10.17.152", "6.10.17.152", "8.09.28.1124");
        Assert.False(same.Ok);
        Assert.Contains("stadig 6.10.17.152", same.Text);

        // rullet tilbage (som ved /S som SYSTEM) og posten forsvundet
        var back = AmdChipset.Result(2, false, "6.10.17.152", null, "8.09.28.1124");
        Assert.False(back.Ok);
        Assert.Contains("rullede tilbage", back.Text);
        Assert.Contains("står ikke i programlisten", back.Text);

        Assert.Contains("ikke færdig i tide", AmdChipset.Result(null, true, "6.10.17.152", "6.10.17.152", "8.09.28.1124").Text);
        Assert.Contains("kode 1603", AmdChipset.Result(1603, false, null, null, "8.09.28.1124").Text);
    }
}
