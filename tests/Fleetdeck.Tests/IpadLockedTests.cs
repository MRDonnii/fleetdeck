using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

public class LockedProfileTests
{
    private static Ipad Child() => new()
    {
        Id = 3, Name = "Test", Kind = IpadKinds.Child, ClientId = "fdabc23", HiddenApps = ["com.google.ios.youtube", "com.zhiliaoapp.musically"], AgeRatingApps = 9,
    };

    private static Ipad Family() => new()
    {
        Id = 4, Name = "Stue", Kind = IpadKinds.Family, ClientId = "fdxyz45", AllowAppInstall = true, AllowInAppPurchases = true, AllowAccountChanges = true,
    };

    /// <summary>Læs en plist til .NET-værdier (dict → Dictionary, array → List) – og fejl, hvis XML'en ikke er velformet.</summary>
    private static Dictionary<string, object> Parse(string xml)
    {
        using var r = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var doc = XDocument.Load(r);
        Assert.Equal("plist", doc.Root!.Name.LocalName);
        return (Dictionary<string, object>)Value(doc.Root.Elements().Single());
    }

    private static object Value(XElement e) => e.Name.LocalName switch
    {
        "dict" => e.Elements().Chunk(2).ToDictionary(kv => kv[0].Value, kv => Value(kv[1])),
        "array" => e.Elements().Select(Value).ToList(),
        "string" => e.Value,
        "integer" => int.Parse(e.Value),
        "true" => true,
        "false" => false,
        _ => throw new InvalidDataException(e.Name.LocalName),
    };

    private static List<Dictionary<string, object>> Payloads(Dictionary<string, object> top) =>
        ((List<object>)top["PayloadContent"]).Cast<Dictionary<string, object>>().ToList();

    private static Dictionary<string, object>? Payload(Dictionary<string, object> top, string type) =>
        Payloads(top).FirstOrDefault(p => (string)p["PayloadType"] == type);

    [Fact]
    public void Child_profile_has_dns_lock_restrictions_and_removal_password()
    {
        var top = Parse(LockedProfile.Build(Child(), "dns.example.org", "abcd-efgh", 3));
        Assert.Equal("dk.fleetdeck.ipad.fdabc23.locked", top["PayloadIdentifier"]);
        Assert.Equal("Configuration", top["PayloadType"]);
        Assert.Equal(1, top["PayloadVersion"]);
        Assert.Equal(true, top["PayloadRemovalDisallowed"]);
        Assert.Contains("(låst v3)", (string)top["PayloadDisplayName"]);

        var dns = Payload(top, "com.apple.dnsSettings.managed")!;
        Assert.Equal(true, dns["ProhibitDisablement"]);
        var settings = (Dictionary<string, object>)dns["DNSSettings"];
        Assert.Equal("HTTPS", settings["DNSProtocol"]);
        Assert.Equal("https://dns.example.org/dns-query/fdabc23", settings["ServerURL"]);

        var rs = Payload(top, "com.apple.applicationaccess")!;
        Assert.Equal(false, rs["allowUIAppInstallation"]);
        Assert.Equal(false, rs["allowInAppPurchases"]);
        Assert.Equal(false, rs["allowAccountModification"]);
        Assert.Equal(false, rs["allowEraseContentAndSettings"]);
        Assert.Equal(false, rs["allowVPNCreation"]);
        Assert.Equal(false, rs["allowUIConfigurationProfileInstallation"]);
        Assert.Equal(200, rs["ratingApps"]);
        Assert.False(rs.ContainsKey("allowAppInstallation"));   // ville også spærre Apple Configurator
        Assert.False(rs.ContainsKey("ratingRegion"));
        Assert.Equal(new List<object> { "com.google.ios.youtube", "com.zhiliaoapp.musically" }, rs["blockedAppBundleIDs"]);

        var rm = Payload(top, "com.apple.profileRemovalPassword")!;
        Assert.Equal("abcd-efgh", rm["RemovalPassword"]);

        // alle payloads har id under profilens, unikt UUID og version 1
        var all = Payloads(top);
        Assert.All(all, p => Assert.StartsWith("dk.fleetdeck.ipad.fdabc23.locked.", (string)p["PayloadIdentifier"]));
        Assert.Equal(all.Count + 1, all.Select(p => (string)p["PayloadUUID"]).Append((string)top["PayloadUUID"]).Distinct().Count());
    }

    [Fact]
    public void Family_profile_without_dns_host_has_only_what_is_set()
    {
        var p = Family();
        var top = Parse(LockedProfile.Build(p, "", "pw", 1));
        Assert.Null(Payload(top, "com.apple.dnsSettings.managed"));
        Assert.Null(Payload(top, "com.apple.applicationaccess"));   // intet at begrænse
        Assert.NotNull(Payload(top, "com.apple.profileRemovalPassword"));

        p.AgeRatingApps = 16; p.HiddenApps = ["com.roblox.robloxmobile"];
        var rs = Payload(Parse(LockedProfile.Build(p, null, "pw", 2)), "com.apple.applicationaccess")!;
        Assert.Equal(416, rs["ratingApps"]);
        Assert.False(rs.ContainsKey("allowEraseContentAndSettings"));   // kun børne-iPads får de faste børneregler
        Assert.False(rs.ContainsKey("allowUIAppInstallation"));
        Assert.False(rs.ContainsKey("allowInAppPurchases"));
    }

    [Fact]
    public void Text_is_xml_escaped()
    {
        var p = Child(); p.Name = "Emma & <Bo> \"1\"\u0001";
        var xml = LockedProfile.Build(p, "dns.example.org", "a<b>&c\"d'", 1);
        Assert.DoesNotContain("<Bo>", xml);
        var top = Parse(xml);
        Assert.Equal("Fleetdeck – Emma & <Bo> \"1\" (låst v1)", top["PayloadDisplayName"]);
        Assert.Equal("a<b>&c\"d'", Payload(top, "com.apple.profileRemovalPassword")!["RemovalPassword"]);
    }

    [Fact]
    public void Same_input_gives_same_profile_and_version_only_bumps_on_change()
    {
        var p = Child();
        Assert.Equal(LockedProfile.Build(p, "h", "pw", 1), LockedProfile.Build(p, "h", "pw", 1));
        Assert.True(LockedProfile.Track(p, "h", "pw"));            // første gang: kun fingeraftryk
        Assert.Equal(1, p.LockedProfileVersion);
        Assert.False(LockedProfile.Track(p, "h", "pw"));
        p.HiddenApps = [.. p.HiddenApps, "com.burbn.instagram"];
        Assert.True(LockedProfile.Track(p, "h", "pw"));
        Assert.Equal(2, p.LockedProfileVersion);
        Assert.True(LockedProfile.Track(p, "h", "nyt-pw"));        // ny fjernelseskode
        Assert.True(LockedProfile.Track(p, "anden.host", "nyt-pw")); // ny DNS-adresse
        Assert.Equal(4, p.LockedProfileVersion);
        p.YtDailyMin = 30;                                           // AdGuard-regler er ikke en del af profilen
        Assert.False(LockedProfile.Track(p, "anden.host", "nyt-pw"));
    }

    [Fact]
    public void Installed_version_is_read_from_the_profile_name()
    {
        var p = Child();
        var id = LockedProfile.Identifier(p);
        Assert.Equal(4, LockedProfile.InstalledVersion(p, [new() { Identifier = "x" }, new() { Identifier = id, Name = "Fleetdeck – Test (låst v4)", Version = 1 }]));
        Assert.Equal(0, LockedProfile.InstalledVersion(p, [new() { Identifier = id, Name = "noget andet" }]));
        Assert.Null(LockedProfile.InstalledVersion(p, [new() { Identifier = LockedProfile.DnsLinkIdentifier(p) }]));
    }

    [Fact]
    public void Bundle_ids_are_validated_case_preserved()
    {
        Assert.True(LockedProfile.IsBundleId("com.facebook.Facebook"));
        Assert.True(LockedProfile.IsBundleId("tv.twitch"));
        Assert.False(LockedProfile.IsBundleId("youtube"));
        Assert.False(LockedProfile.IsBundleId("com.evil app"));
        Assert.False(LockedProfile.IsBundleId("com..x"));
        Assert.Equal(["com.facebook.Facebook", "com.facebook.facebook"], LockedProfile.CleanApps([" com.facebook.Facebook ", "com.facebook.facebook", "com.facebook.Facebook", "nope"]));
        Assert.Null(LockedProfile.RatingValue(12));   // 12+ og 17+ findes ikke længere
        Assert.Equal(618, LockedProfile.RatingValue(18));
    }
}

public class CfgutilTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void List_output_is_parsed()
    {
        var list = Cfgutil.ParseList(J("""
            {"Command":"list","Type":"CommandOutput","Devices":["0x1A2B3C4D5E6F"],
             "Output":{"0x1A2B3C4D5E6F":{"ECID":"0x1A2B3C4D5E6F","UDID":"00008101-000A","name":"Emmas iPad","deviceType":"iPad13,18","locationID":337920512}}}
            """));
        var d = Assert.Single(list);
        Assert.Equal("0x1A2B3C4D5E6F", d.Ecid);
        Assert.Equal("Emmas iPad", d.Name);
        Assert.Equal("iPad13,18", d.DeviceType);
        Assert.Empty(Cfgutil.ParseList(J("""{"Command":"list","Type":"CommandOutput","Devices":[],"Output":{}}""")));
    }

    [Fact]
    public void Get_output_skips_errors_and_reads_typed_values()
    {
        var d = Cfgutil.ParseGet(J("""
            {"Command":"get","Type":"CommandOutput","Devices":["0x1a2b3c4d5e6f"],
             "Output":{"0x1A2B3C4D5E6F":{"isSupervised":true,"isPaired":"yes","batteryCurrentCapacity":76,"firmwareVersion":"26.1","serialNumber":"SER1",
               "bootedState":"Booted","configurationProfiles":[{"displayName":"Fleetdeck – Emma (låst v2)","identifier":"dk.fleetdeck.ipad.fd1.locked","version":1}]},
               "Errors":{"0x1A2B3C4D5E6F":{"IMEI":{"Code":-402653163,"Domain":"com.apple.configurator.MobileDeviceKit.amd.error","Message":"The value is missing."}}}}}
            """), "0x1a2b3c4d5e6f")!;
        Assert.Equal("0x1A2B3C4D5E6F", d.Ecid);
        Assert.True(d.Supervised);
        Assert.True(d.Paired);
        Assert.Equal(76, d.Battery);
        Assert.Equal("26.1", d.Firmware);
        Assert.Equal("Booted", d.BootedState);
        var pr = Assert.Single(d.Profiles!);
        Assert.Equal("dk.fleetdeck.ipad.fd1.locked", pr.Identifier);
        Assert.Equal(1, pr.Version);
    }

    [Fact]
    public void Errors_are_found_even_with_noise_around_the_json()
    {
        var j = Cfgutil.ExtractJson("""
            warning: something
            {"Type":"Error","Command":"install-profile","Message":"The profile could not be installed.","FailureReason":"A profile with this identifier is already installed.","Code":118,"Domain":"ConfigurationUtilityKit.error"}
            """)!.Value;
        var e = Cfgutil.Error(j)!.Value;
        Assert.Equal(Cfgutil.ProfileAlreadyInstalled, e.Code);
        Assert.Contains("already installed", e.Message);
        Assert.Null(Cfgutil.Error(J("""{"Type":"CommandOutput","Output":{}}""")));
        Assert.Null(Cfgutil.ExtractJson("ingen json her"));
    }

    [Fact]
    public void Ecids_are_normalised()
    {
        Assert.Equal("0x1A2B", Cfgutil.NormEcid(" 0x001a2b "));
        Assert.Equal("0x1A2B", Cfgutil.NormEcid("1A2B"));
        Assert.Equal("", Cfgutil.NormEcid("0xZZ"));
        Assert.Equal("", Cfgutil.NormEcid(null));
        var merged = Cfgutil.Merge(new IosDeviceInfo { Ecid = "0x1", Serial = "S", Supervised = true }, new IosDeviceInfo { Ecid = "0x1", Firmware = "26.1" });
        Assert.Equal("S", merged.Serial);
        Assert.True(merged.Supervised);
        Assert.Equal("26.1", merged.Firmware);
    }
}
