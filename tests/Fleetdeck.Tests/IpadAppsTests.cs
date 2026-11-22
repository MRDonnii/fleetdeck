using System.Text.Json;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

public class AppStoreLookupTests
{
    private const string Sample = """
    {"resultCount":1,"results":[{"kind":"software","trackId":544007664,"trackName":"YouTube","bundleId":"com.google.ios.youtube","version":"20.45.3",
      "artworkUrl60":"https://is1-ssl.mzstatic.com/a/60x60bb.jpg","artworkUrl512":"https://is1-ssl.mzstatic.com/a/512x512bb.jpg",
      "trackViewUrl":"https://apps.apple.com/dk/app/youtube/id544007664?uo=4","currentVersionReleaseDate":"2026-11-10T08:00:00Z"}]}
    """;

    [Fact]
    public void Parses_found_app()
    {
        var i = AppStore.ParseLookup(Sample, "com.google.ios.youtube");
        Assert.True(i.Found);
        Assert.Equal(544007664, i.TrackId);
        Assert.Equal("YouTube", i.Name);
        Assert.Equal("20.45.3", i.Version);
        Assert.Equal("https://is1-ssl.mzstatic.com/a/512x512bb.jpg", i.IconUrl);
        Assert.StartsWith("https://apps.apple.com/dk/", i.StoreUrl);
        Assert.Equal(new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc), i.Released);
    }

    [Fact]
    public void Missing_app_is_not_found()
    {
        Assert.False(AppStore.ParseLookup("""{"resultCount":0,"results":[]}""", "dk.example.intern").Found);
        Assert.False(AppStore.ParseLookup("""{"errorMessage":"Invalid value"}""", "x.y").Found);
    }

    [Fact]
    public void Picks_matching_bundle_and_falls_back_to_small_icon_and_https()
    {
        var json = """
        {"resultCount":2,"results":[{"bundleId":"com.other","trackName":"Other","version":"1.0"},
          {"bundleId":"COM.Example.App","trackName":"Example","version":"2.1","artworkUrl100":"http://is1.mzstatic.com/100.jpg"}]}
        """;
        var i = AppStore.ParseLookup(json, "com.example.app");
        Assert.Equal("Example", i.Name);
        Assert.Equal("https://is1.mzstatic.com/100.jpg", i.IconUrl);
    }

    [Fact]
    public void Invalid_json_throws() => Assert.ThrowsAny<JsonException>(() => AppStore.ParseLookup("<html>", "a.b"));

    [Fact]
    public void Lookup_url_is_danish_store_and_escaped() =>
        Assert.Equal("https://itunes.apple.com/lookup?bundleId=com.a%26b&country=dk", AppStore.LookupUrl("com.a&b"));

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("1.9.9", "1.10", -1)]
    [InlineData("2", "10", -1)]
    [InlineData("20.45.3", "20.44.12", 1)]
    [InlineData("1.2 beta", "1.2", 0)]
    [InlineData("1.2.3b", "1.2.3", 0)]
    public void Compares_versions(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(AppStore.CompareVersions(a, b)));

    [Theory]
    [InlineData("1.0", "1.1", true)]
    [InlineData("1.1", "1.1", false)]
    [InlineData("1.2", "1.1", false)]     // TestFlight/nyere end butikken
    [InlineData(null, "1.1", false)]
    [InlineData("1.0", null, false)]
    [InlineData("abc", "1.1", false)]
    [InlineData("1.0", "", false)]
    public void Outdated_only_when_store_is_newer(string? installed, string? store, bool expected) => Assert.Equal(expected, AppStore.IsOutdated(installed, store));
}

public class IpadAppParsingTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Cfgutil_array_of_apps()
    {
        var apps = CfgutilApps.Parse(J("""
        {"Command":"get","Type":"CommandOutput","Devices":["0x1A2B"],"Output":{"0x1A2B":{"name":"iPad","installedApps":[
          {"bundleIdentifier":"com.google.ios.youtube","displayName":"YouTube","bundleShortVersion":"20.44.12","bundleVersion":"20.44.12.1"},
          {"bundleIdentifier":"com.roblox.robloxmobile","itunesName":"Roblox","bundleVersion":"2.650"},
          {"displayName":"uden id"}]}}}
        """), "0x1a2b")!;
        Assert.Equal(2, apps.Count);
        var r = apps.Single(a => a.BundleId == "com.roblox.robloxmobile");
        Assert.Equal("Roblox", r.Name);
        Assert.Equal("2.650", r.Version);   // ingen kort version: bundle-versionen bruges
        var y = apps.Single(a => a.BundleId == "com.google.ios.youtube");
        Assert.Equal("20.44.12", y.Version);
        Assert.Equal("20.44.12.1", y.Build);
    }

    [Fact]
    public void Cfgutil_dictionary_keyed_by_bundle_id_and_other_ecid_format()
    {
        var apps = CfgutilApps.Parse(J("""
        {"Output":{"0x00001A2B":{"installedApps":{"com.netflix.Netflix":{"CFBundleDisplayName":"Netflix","CFBundleShortVersionString":"17.1.0"}}}}}
        """), "0x1A2B")!;
        var n = Assert.Single(apps);
        Assert.Equal("com.netflix.Netflix", n.BundleId);
        Assert.Equal("17.1.0", n.Version);
    }

    [Fact]
    public void Cfgutil_without_app_list_is_null()
    {
        Assert.Null(CfgutilApps.Parse(J("""{"Output":{"0x1A2B":{"name":"iPad"}}}"""), "0x1A2B"));
        Assert.Null(CfgutilApps.Parse(J("""{"Type":"Error","Message":"x"}"""), "0x1A2B"));
    }

    [Fact]
    public void Mdm_installed_application_list()
    {
        var report = new Dictionary<string, object>
        {
            ["Status"] = "Acknowledged",
            ["InstalledApplicationList"] = new List<object>
            {
                new Dictionary<string, object> { ["Identifier"] = "com.zhiliaoapp.musically", ["Name"] = "TikTok", ["ShortVersion"] = "42.1.0", ["Version"] = "421000" },
                new Dictionary<string, object> { ["Identifier"] = "dk.example", ["Version"] = "3" },
                new Dictionary<string, object> { ["Name"] = "ingen id" },
            },
        };
        var apps = AppStore.MdmApps(report);
        Assert.Equal(2, apps.Count);
        Assert.Equal("42.1.0", apps[0].Version);
        Assert.Equal("421000", apps[0].Build);
        Assert.Equal("3", apps[1].Version);
        Assert.Empty(AppStore.MdmApps(new Dictionary<string, object>()));
    }

    [Fact]
    public void Mdm_command_is_installed_application_list()
    {
        var b = AppStore.MdmListCommand();
        Assert.Equal("InstalledApplicationList", b.RequestType);
        var d = Plist.ParseDict(b.Xml);
        Assert.Equal(b.Uuid, d.Str("CommandUUID"));
        var cmd = d.Dict("Command")!;
        Assert.Equal("InstalledApplicationList", cmd.Str("RequestType"));
        Assert.False(cmd.Bool("ManagedAppsOnly"));
    }

    private static readonly (string Id, string Name, string[] Domains)[] Services =
    [
        ("youtube", "YouTube", ["youtube.com", "youtubekids.com"]), ("tiktok", "TikTok", ["tiktok.com"]), ("roblox", "Roblox", ["roblox.com"]),
        ("minecraft", "Minecraft", ["minecraft.net", "mojang.com"]), ("google", "Google", ["google.com"]),
    ];

    [Theory]
    [InlineData("com.google.ios.youtubekids", "YouTube Kids", "youtube")]       // kendt app
    [InlineData("com.zhiliaoapp.musically", "TikTok", "tiktok")]
    [InlineData("com.example.tiktokclone", "TikTok – Videoer og musik", "tiktok")]   // samme navn
    [InlineData("com.mojang.somegame", "Noget andet", "minecraft")]            // appens domæne i tjenestens regler
    [InlineData("com.google.ios.maps", "Google Maps", null)]                    // Google-domænet siger intet om appen
    [InlineData("com.netflix.Netflix", "Netflix", null)]                       // ikke i AdGuard her
    public void Matches_adguard_service(string bundle, string name, string? expected) => Assert.Equal(expected, AppStore.MatchService(bundle, name, Services));
}
