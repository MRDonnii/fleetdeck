using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Tests;

public class ReserveDnsTests
{
    private const string Token = "eyJhIjoiMDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWYiLCJ0IjoiMTExMTExMTEtMjIyMi0zMzMzLTQ0NDQtNTU1NTU1NTU1NTU1IiwicyI6Ik1USXpORFUyTnpnNU1BPT0ifQ==";
    private const string Nonce = "Zm9vYmFyYmF6cXV4cXV1eGZvb2Jhcg";
    private static readonly ReserveDnsConfig Cfg = new(9, "br0", "192.168.1.53", ReserveDns.DefaultContainer, ReserveDns.DefaultTunnelContainer, "", "");

    // ------------------------------------------------------------------ opsætning og scripts

    [Fact] public void DefaultConfigIsValid() => Assert.Null(ReserveDns.Validate(Cfg));

    [Theory]
    [InlineData("host", "", true)]
    [InlineData("br0", "", true)]          // macvlan uden IP stoppes af scriptet (driveren kendes kun på maskinen)
    [InlineData("fleetdeck-net", "", true)]
    [InlineData("bridge", "", false)]      // intet navneopslag på standard-bridge
    [InlineData("host", "192.168.1.53", false)]
    [InlineData("br0;reboot", "", false)]
    [InlineData("br0", "192.168.1.1'", false)]
    [InlineData("none", "", false)]
    public void NetworkAndIpAreValidated(string net, string ip, bool ok) => Assert.Equal(ok, ReserveDns.Validate(Cfg with { Network = net, Ip = ip }) is null);

    [Fact] public void ContainersMustDiffer() => Assert.NotNull(ReserveDns.Validate(Cfg with { TunnelContainer = Cfg.Container.ToUpperInvariant() }));

    [Theory]
    [InlineData("", true)]
    [InlineData("http://192.168.1.53:8080", true)]
    [InlineData("https://dns-reserve.lan", true)]
    [InlineData("ftp://x", false)]
    [InlineData("192.168.1.53", false)]
    [InlineData("http://x/'$(id)'", false)]
    public void ApiUrlIsValidated(string url, bool ok) => Assert.Equal(ok, ReserveDns.Validate(Cfg with { ApiUrl = url }) is null);

    [Theory]
    [InlineData("http://192.168.1.15", 80)]
    [InlineData("http://192.168.1.15:3000/", 3000)]
    [InlineData("https://adguard.lan", 80)]
    [InlineData("", 80)]
    public void PortFollowsThePrimary(string url, int port) => Assert.Equal(port, ReserveDns.PortOf(url));

    [Fact] public void ServiceUsesTheSharedName() => Assert.Equal("http://fleetdeck-dns:3000", ReserveDns.Service(3000));

    [Theory]
    [InlineData("http://192.168.1.15", "192.168.1.15")]
    [InlineData("http://adguard.lan:3000", "adguard.lan")]
    [InlineData("", null)]
    [InlineData("nonsense", null)]
    public void OriginHostIsTakenFromTheAdGuardUrl(string url, string? host) => Assert.Equal(host, ReserveDns.OriginHost(url));

    [Fact] public void ApiUrlIsDerived()
    {
        Assert.Equal("http://192.168.1.53:80", ReserveDnsSettings.ApiUrl(Cfg, "192.168.1.20", 80));
        Assert.Equal("http://192.168.1.20:3000", ReserveDnsSettings.ApiUrl(Cfg with { Network = "host", Ip = "" }, "192.168.1.20", 3000));
        Assert.Equal("http://x:1", ReserveDnsSettings.ApiUrl(Cfg with { ApiUrl = "http://x:1/" }, "192.168.1.20", 80));
        Assert.Null(ReserveDnsSettings.ApiUrl(Cfg with { Network = "host", Ip = "" }, null, 80));
    }

    [Fact] public void BootstrapYamlHasLoginDohAndSchema()
    {
        var hash = ReserveDnsSettings.Hash("hemmelig-kode");
        Assert.True(BCrypt.Net.BCrypt.Verify("hemmelig-kode", hash));
        var y = ReserveDns.BootstrapYaml(3000, hash, null);
        Assert.Contains("address: 0.0.0.0:3000", y);
        Assert.Contains("insecure_enabled: true", y);
        Assert.Contains($"password: \"{hash}\"", y);
        Assert.Contains("- name: fleetdeck", y);
        Assert.Contains("- 0.0.0.0", y);
        Assert.Contains($"schema_version: {ReserveDns.SchemaVersion}", y);
        Assert.DoesNotContain("hemmelig-kode", y);
        Assert.Contains("- 192.168.1.20", ReserveDns.BootstrapYaml(80, hash, "192.168.1.20"));   // host-netværk: DNS kun på maskinens IP
        Assert.Contains("- 0.0.0.0", ReserveDns.BootstrapYaml(80, hash, "192.168.1.20; x"));
        Assert.Throws<ArgumentException>(() => ReserveDns.BootstrapYaml(80, "ikke en hash", null));
        Assert.Throws<ArgumentException>(() => ReserveDns.BootstrapYaml(0, hash, null));
    }

    [Fact] public void SecretsAreMasked()
    {
        var hash = ReserveDnsSettings.Hash("x");
        var secrets = ReserveDnsSettings.Secrets(Token, ReserveDns.BootstrapYaml(80, hash, null));
        Assert.StartsWith($"TUNNEL_TOKEN={Token}\nAG_YAML=", secrets);
        var b64 = secrets.Split('\n')[1]["AG_YAML=".Length..];
        Assert.Contains(hash, Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        var masked = ReserveDns.Mask(PublicAccess.Mask(secrets + "\nlog " + hash, Token));
        Assert.DoesNotContain(Token, masked);
        Assert.DoesNotContain(hash, masked);
        Assert.DoesNotContain(b64, masked);
    }

    [Fact] public void StartJobHasNoSecretsAndPinsImages()
    {
        var body = ReserveDns.StartJob(Cfg, Nonce, 80).Body!;
        Assert.DoesNotContain(Token, body);
        Assert.DoesNotContain("$2a$", body);
        Assert.Contains($"IMAGE='{ReserveDns.Image}'", body);
        Assert.Contains($"CFIMAGE='{PublicAccess.Image}'", body);
        Assert.Matches(@"adguard/adguardhome:v\d+\.\d+\.\d+$", ReserveDns.Image);
        Assert.Contains("--network-alias \"$ALIAS\"", body);
        Assert.Contains("--add-host \"$ALIAS:$IP\"", body);
        Assert.Contains("--add-host \"$ALIAS:127.0.0.1\"", body);
        Assert.Contains("-e TUNNEL_TOKEN \"$CFIMAGE\" tunnel --no-autoupdate run", body);
        Assert.Contains(ReserveDns.SecretsPath, body);
        Assert.Contains("PORT=80", body);
    }

    [Fact] public void StartJobRejectsBadInput()
    {
        Assert.Throws<ArgumentException>(() => ReserveDns.StartJob(Cfg with { Network = "br0;reboot" }, Nonce, 80));
        Assert.Throws<ArgumentException>(() => ReserveDns.StartJob(Cfg with { Container = "x'y" }, Nonce, 80));
        Assert.Throws<ArgumentException>(() => ReserveDns.StartJob(Cfg with { DataDir = "/mnt/../etc" }, Nonce, 80));
        Assert.Throws<ArgumentException>(() => ReserveDns.StartJob(Cfg, "nonce'; reboot; '", 80));
        Assert.Throws<ArgumentException>(() => ReserveDns.StartJob(Cfg, Nonce, 70000));
        Assert.Throws<ArgumentException>(() => ReserveDns.StopJob(Cfg with { TunnelContainer = "$(id)" }));
    }

    [Fact] public void PrimaryTunnelPointsTheSharedNameAtTheMainAdGuard()
    {
        var c = new TunnelConfig(7, "br0", "192.168.1.240", "fleetdeck-tunnel");
        var body = PublicAccess.StartJob(c, Nonce, "192.168.1.15").Body!;
        Assert.Contains("ORIGIN='192.168.1.15'", body);
        Assert.Contains("--add-host \"$ALIAS:$OIP\"", body);
        Assert.Contains("ORIGIN=''", PublicAccess.StartJob(c, Nonce).Body!);
        Assert.Throws<ArgumentException>(() => PublicAccess.StartJob(c, Nonce, "x'; reboot"));
    }

    [Fact] public void ScriptsAreValidShell()
    {
        if (!File.Exists("/bin/bash")) return;   // Windows
        var jobs = new[]
        {
            ReserveDns.StartJob(Cfg, Nonce, 80), ReserveDns.StartJob(Cfg with { Network = "host", Ip = "" }, Nonce, 3000),
            ReserveDns.StartJob(Cfg with { Network = "fleetdeck", Ip = "", DataDir = "/srv/dns" }, Nonce, 80), ReserveDns.StopJob(Cfg),
            PublicAccess.StartJob(new TunnelConfig(7, "br0", "", "fleetdeck-tunnel"), Nonce, "adguard.lan"),
        };
        foreach (var p in jobs)
        {
            var f = Path.GetTempFileName();
            try
            {
                File.WriteAllText(f, p.Body);
                using var proc = Process.Start(new ProcessStartInfo("/bin/bash", ["-n", f]) { RedirectStandardError = true })!;
                var err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Assert.True(proc.ExitCode == 0, err);
            }
            finally { File.Delete(f); }
        }
    }

    [Fact] public void AlarmOnlyAfterGracePeriodAndOnTheReserveMachine()
    {
        var now = DateTime.UtcNow;
        ReserveDnsHealth H(bool up, DateTime? down) => new(9, up, now, null, down, "timeout", null, null, 0, null, true, null);
        Assert.Null(ReserveDnsStatus.Alarm(H(true, null), now));
        Assert.Null(ReserveDnsStatus.Alarm(H(false, now.AddMinutes(-1)), now));
        Assert.NotNull(ReserveDnsStatus.Alarm(H(false, now.AddMinutes(-4)), now));
        var old = ReserveDnsStatus.Current;
        try
        {
            ReserveDnsStatus.Current = H(false, now.AddMinutes(-10));
            Assert.Equal(ReserveDns.AlertKey, ReserveDnsStatus.Finding(9, now)?.Key);
            Assert.Null(ReserveDnsStatus.Finding(8, now));
        }
        finally { ReserveDnsStatus.Current = old; }
    }

    // ------------------------------------------------------------------ synkronisering

    private static IpadDesired Ipad(int id, params string[] services) => new(IpadPolicy.ClientName(id), [.. services], [], true, true, true);
    private static JsonObject Body(int id, params string[] services) => AdGuardClient.ClientBody(Ipad(id, services), $"ipad{id}");

    private static JsonObject O(string json) => JsonNode.Parse(json)!.AsObject();

    private const string DefaultFiltering = """{"enabled":true,"interval":24,"filters":[{"url":"https://x.test/list.txt","name":"Liste","enabled":true,"id":1,"rules_count":10}],"whitelist_filters":[]}""";

    private static AgSnapshot Snap(string[]? rules = null, JsonObject[]? clients = null, JsonObject? filtering = null, JsonObject? dns = null,
        JsonArray? rewrites = null, JsonObject? blocked = null, bool safeBrowsing = true) =>
        new(dns ?? O("""{"upstream_dns":["https://dns.quad9.net/dns-query"],"ratelimit":0,"protection_enabled":true,"default_local_ptr_upstreams":["192.168.1.1"]}"""),
            WithRules(filtering ?? O(DefaultFiltering), rules ?? ["||ads.test^"]),
            blocked ?? O("""{"ids":["tiktok"],"schedule":{"time_zone":"Local"}}"""),
            new JsonArray([.. (clients ?? []).Select(c => (JsonNode)c.DeepClone())]),
            rewrites ?? new JsonArray(O("""{"domain":"nas.lan","answer":"192.168.1.10","enabled":true}""")),
            O("""{"allowed_clients":[],"disallowed_clients":[],"blocked_hosts":["version.bind"]}"""),
            safeBrowsing, false,
            O("""{"enabled":true,"bing":true,"duckduckgo":true,"ecosia":true,"google":true,"pixabay":true,"yandex":true,"youtube":false}"""),
            O("""{"enabled":true,"interval":7776000000,"anonymize_client_ip":false,"ignored":[],"ignored_enabled":false}"""),
            O("""{"enabled":true,"interval":86400000,"ignored":[],"ignored_enabled":false}"""));

    private static JsonObject WithRules(JsonObject f, string[] rules) { f["user_rules"] = new JsonArray([.. rules.Select(r => (JsonNode)r)]); return f; }

    /// <summary>Det, AdGuard viser for en klient: alt det, der blev sendt, plus felter Fleetdeck ikke sender.</summary>
    private static JsonObject AsShown(JsonObject body)
    {
        var c = (JsonObject)body.DeepClone();
        c["safesearch_enabled"] = false;
        c["blocked_services_schedule"] = O("""{"time_zone":"Local"}""");
        c.Remove("upstreams");   // tomme lister udelades
        return c;
    }

    private static List<string> Paths(List<SyncOp> ops) => ops.Select(o => o.Path).ToList();

    [Fact] public void InSyncGivesNoCalls()
    {
        var ours = new List<string> { "||blocked.test^$client='fd-ipad-1'" };
        var rules = AdGuardClient.ReplaceSection(["||ads.test^"], ours).ToArray();
        var phone = O("""{"name":"Telefon","ids":["192.168.1.30"],"use_global_settings":true,"tags":[]}""");
        var primary = Snap(rules, [phone, AsShown(Body(1, "youtube"))]);
        // reserven viser felterne i en anden rækkefølge og har ekstra felter
        var replica = Snap(rules, [AsShown(Body(1, "youtube")), O("""{"name":"Telefon","ids":["192.168.1.30"],"use_global_settings":true,"filtering_enabled":false}""")]);
        Assert.Empty(ReserveDnsSync.Plan(primary, replica, [Body(1, "youtube")], ours));
    }

    [Fact] public void IpadClientsFollowDesiredStateNotThePrimary()
    {
        // hoved-AdGuard har endnu ikke fået den nye tilstand (fx fordi den er nede): reserven får den ønskede alligevel
        var primary = Snap(clients: [AsShown(Body(1))]);
        var replica = Snap(clients: [AsShown(Body(1))]);
        var ops = ReserveDnsSync.Plan(primary, replica, [Body(1, "youtube")], []);
        var op = Assert.Single(ops, o => o.Path == "/control/clients/update");
        Assert.Equal("fd-ipad-1", op.Body!["name"]!.GetValue<string>());
        Assert.Equal("youtube", op.Body!["data"]!["blocked_services"]![0]!.GetValue<string>());
    }

    [Fact] public void ClientsAreDeletedThenUpdatedThenAdded()
    {
        var primary = Snap(clients: [O("""{"name":"Ny","ids":["192.168.1.31"]}"""), O("""{"name":"Ændret","ids":["192.168.1.32"],"filtering_enabled":true}""")]);
        var replica = Snap(clients: [O("""{"name":"Gammel","ids":["192.168.1.31"]}"""), O("""{"name":"Ændret","ids":["192.168.1.32"],"filtering_enabled":false}"""), AsShown(Body(5))]);
        var ops = ReserveDnsSync.Plan(primary, replica, [Body(1)], []).Where(o => o.Path.StartsWith("/control/clients/")).ToList();
        Assert.Equal(["/control/clients/delete", "/control/clients/delete", "/control/clients/update", "/control/clients/add", "/control/clients/add"], Paths(ops));
        Assert.Equal(["Gammel", "fd-ipad-5"], ops.Take(2).Select(o => o.Body!["name"]!.GetValue<string>()));
        Assert.Equal(["Ny", "fd-ipad-1"], ops.Skip(3).Select(o => o.Body!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    [Fact] public void PrimaryDownOnlyIpadStateIsApplied()
    {
        var ours = new List<string> { "||new.test^$client='fd-ipad-1'" };
        var replica = Snap(["||own.test^", AdGuardClient.Begin, "||old.test^", AdGuardClient.End], [O("""{"name":"Telefon","ids":["192.168.1.30"]}"""), AsShown(Body(7))],
            dns: O("""{"upstream_dns":["1.1.1.1"]}"""), safeBrowsing: false);
        var ops = ReserveDnsSync.Plan(null, replica, [Body(1)], ours);
        // reservens egne indstillinger og andre klienter bliver stående; kun iPad-klienter og iPad-reglerne ændres
        Assert.Equal(["/control/filtering/set_rules", "/control/clients/delete", "/control/clients/add"], Paths(ops));
        Assert.Equal(["||own.test^", AdGuardClient.Begin, ours[0], AdGuardClient.End], ops[0].Body!["rules"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("fd-ipad-7", ops[1].Body!["name"]!.GetValue<string>());
    }

    [Fact] public void UserRulesAreCopiedWithTheIpadSection()
    {
        var ours = new List<string> { "||x.test^" };
        var primary = Snap(["||mine.test^", "@@||ok.test^"]);
        var replica = Snap(["||stale.test^"]);
        var op = Assert.Single(ReserveDnsSync.Plan(primary, replica, [], ours), o => o.Path == "/control/filtering/set_rules");
        Assert.Equal(["||mine.test^", "@@||ok.test^", AdGuardClient.Begin, "||x.test^", AdGuardClient.End], op.Body!["rules"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact] public void FilterListsAreAddedRemovedAndToggled()
    {
        var primary = Snap(filtering: O("""
            {"enabled":true,"interval":24,"filters":[
              {"url":"https://a.test/1.txt","name":"A","enabled":true},
              {"url":"https://b.test/2.txt","name":"B","enabled":false},
              {"url":"https://c.test/3.txt","name":"C nyt navn","enabled":true},
              {"url":"/opt/adguardhome/lokal.txt","name":"Lokal","enabled":true}],
             "whitelist_filters":[{"url":"https://w.test/ok.txt","name":"W","enabled":true}]}
            """));
        var replica = Snap(filtering: O("""
            {"enabled":true,"interval":12,"filters":[
              {"url":"https://c.test/3.txt","name":"C","enabled":true},
              {"url":"https://old.test/x.txt","name":"Gammel","enabled":true}],
             "whitelist_filters":[]}
            """));
        var ops = ReserveDnsSync.Plan(primary, replica, [], []);
        var f = ops.Where(o => o.Path.StartsWith("/control/filtering/") && o.Path != "/control/filtering/set_rules").ToList();
        Assert.Equal([
            ("/control/filtering/config", ""),
            ("/control/filtering/remove_url", "https://old.test/x.txt"),
            ("/control/filtering/add_url", "https://a.test/1.txt"),
            ("/control/filtering/add_url", "https://b.test/2.txt"),
            ("/control/filtering/set_url", "https://b.test/2.txt"),
            ("/control/filtering/set_url", "https://c.test/3.txt"),
            ("/control/filtering/add_url", "https://w.test/ok.txt"),
        ], f.Select(o => (o.Path, o.Body!["url"]?.GetValue<string>() ?? "")));
        Assert.False(f[4].Body!["data"]!["enabled"]!.GetValue<bool>());
        Assert.True(f[6].Body!["whitelist"]!.GetValue<bool>());
        Assert.DoesNotContain(f, o => o.Body!["url"]?.GetValue<string>() == "/opt/adguardhome/lokal.txt");
    }

    [Fact] public void DnsSettingsSkipReadOnlyFields()
    {
        var primary = Snap(dns: O("""{"upstream_dns":["192.168.1.1"],"ratelimit":0,"default_local_ptr_upstreams":["x"],"protection_disabled_until":"2026-01-01T00:00:00Z","upstream_dns_file":"/x"}"""));
        var replica = Snap(dns: O("""{"upstream_dns":["1.1.1.1"],"ratelimit":20}"""));
        var op = Assert.Single(ReserveDnsSync.Plan(primary, replica, [], []), o => o.Path == "/control/dns_config");
        var b = op.Body!.AsObject();
        Assert.Equal(["ratelimit", "upstream_dns"], b.Select(kv => kv.Key).Order(StringComparer.Ordinal));
        Assert.Equal(0, b["ratelimit"]!.GetValue<int>());
    }

    [Fact] public void GlobalSettingsAreCopied()
    {
        var primary = Snap(blocked: O("""{"ids":["tiktok","snapchat"],"schedule":{"time_zone":"Local","mon":{"start":0,"end":3600000}}}"""),
            rewrites: new JsonArray(O("""{"domain":"nas.lan","answer":"192.168.1.11","enabled":true}""")));
        var replica = Snap(blocked: O("""{"ids":["snapchat","tiktok"],"schedule":{"time_zone":"Local"}}"""), safeBrowsing: false);
        var ops = ReserveDnsSync.Plan(primary, replica, [], []);
        Assert.Contains("/control/blocked_services/update", Paths(ops));      // skemaet er forskelligt (ikke rækkefølgen af ids)
        Assert.Contains("/control/safebrowsing/enable", Paths(ops));
        Assert.Equal(["/control/rewrite/delete", "/control/rewrite/add"], Paths(ops).Where(p => p.StartsWith("/control/rewrite/")));
        Assert.DoesNotContain("/control/access/set", Paths(ops));
        Assert.DoesNotContain("/control/safesearch/settings", Paths(ops));
        // reservens snapshot bliver ikke ændret af planen (kroppene er kopier)
        var body = ops.First(o => o.Path == "/control/blocked_services/update").Body!;
        body["ids"] = new JsonArray();
        Assert.Equal(2, primary.BlockedServices["ids"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("""{"a":false}""", """{}""", true)]
    [InlineData("""{"a":[]}""", """{"b":1}""", true)]
    [InlineData("""{"a":["x","y"]}""", """{"a":["y","x"]}""", true)]
    [InlineData("""{"a":1}""", """{"a":1.0}""", true)]
    [InlineData("""{"a":true}""", """{}""", false)]
    [InlineData("""{"a":{"b":"c"}}""", """{"a":{"b":"d"}}""", false)]
    [InlineData("""{"a":["x"]}""", """{"a":["x","y"]}""", false)]
    public void CoversComparesLikeAdGuardShowsIt(string expected, string actual, bool ok) =>
        Assert.Equal(ok, ReserveDnsSync.Covers(JsonNode.Parse(expected), JsonNode.Parse(actual)));

    [Theory]
    [InlineData("fd1e563a71c186d9", true)]    // 16 hextegn: AdGuard gemmer det som fd:1e:56:…
    [InlineData("a1b2c3d4e5f6", true)]        // 12 hextegn
    [InlineData("fdx8cbi2y9nbkad6", false)]
    [InlineData("fdg23456789abcde", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Client_ids_that_AdGuard_would_read_as_a_mac_are_detected(string? id, bool expected) =>
        Assert.Equal(expected, Fleetdeck.Server.Services.AdGuardClient.LooksLikeMac(id));
}
