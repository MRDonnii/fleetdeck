using System.Net;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fleetdeck.Tests;

/// <summary>Afviste anmodninger om adgang (tekst og grund) og "samme net" (IPv6-præfiks og Cloudflare hjemmefra).</summary>
public class AccessRefusalTests
{
    [Theory]
    [InlineData("10.20.30.10", false)] [InlineData("192.168.1.10", false)] [InlineData("172.20.0.5", false)] [InlineData("169.254.3.4", false)]
    [InlineData("100.100.1.1", false)] [InlineData("127.0.0.1", false)] [InlineData("::1", false)] [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)] [InlineData("fleetdeck", false)] [InlineData("fleetdeck.local", false)] [InlineData("nas.lan", false)]
    [InlineData("server.home", false)] [InlineData("x.home.arpa", false)] [InlineData("localhost", false)]
    [InlineData("203.0.113.9", true)] [InlineData("8.8.8.8", true)] [InlineData("172.32.0.1", true)] [InlineData("2001:db8::5", true)]
    [InlineData("fleet.eksempel.dk", true)] [InlineData("FLEET.Eksempel.DK.", true)]
    public void Offentlig_eller_hjemme_adresse(string host, bool isPublic) => Assert.Equal(isPublic, AccessRefusal.IsPublicHost(host));

    [Theory]
    [InlineData("http://192.168.1.10:8585", "192.168.1.10")] [InlineData("https://fleet.eksempel.dk/", "fleet.eksempel.dk")]
    [InlineData("fleet.eksempel.dk:8585", "fleet.eksempel.dk")] [InlineData("http://[2001:db8::5]:8585", "2001:db8::5")]
    [InlineData("", null)] [InlineData(null, null)] [InlineData("unix:/run/fleetdeck.sock", null)]
    public void Værten_i_adressen(string? url, string? host) => Assert.Equal(host, AccessRefusal.HostOf(url));

    [Fact]
    public void Grunden_bestemmer_teksten()
    {
        // grunden fra serveren: hjemmenet-hjælpen med adressen, uanset om navnet ligner et hjemmenavn
        Assert.Equal(AccessRefusal.OutsideText("fleet.eksempel.dk"), AccessRefusal.Explain(AccessRefusal.Outside, "https://fleet.eksempel.dk"));
        Assert.Contains("fleet.eksempel.dk", AccessRefusal.Explain(AccessRefusal.Outside, "https://fleet.eksempel.dk"));
        Assert.Contains("192.168.1.10:8585", AccessRefusal.OutsideText("x"));
        // ældre servere (3.41–3.44) sender ingen grund: en offentlig adresse giver samme hjælp
        Assert.Equal(AccessRefusal.OutsideText("fleet.eksempel.dk"), AccessRefusal.Explain(null, "https://fleet.eksempel.dk"));
        Assert.Equal(AccessRefusal.OutsideText("203.0.113.9"), AccessRefusal.Explain(null, "http://203.0.113.9:8585"));
        // hjemmeadresse uden grund (slået fra, blokeret): den generelle tekst
        Assert.Equal(AccessRefusal.Generic, AccessRefusal.Explain(null, "http://192.168.1.10:8585"));
        Assert.Equal(AccessRefusal.Generic, AccessRefusal.Explain(null, "http://fleetdeck.local:8585"));
        Assert.Equal(AccessRefusal.Generic, AccessRefusal.Explain(null, null));
        // "off" (slået fra eller blokeret): sig, at det er slået fra, og hvor administratoren slår det til – også på en offentlig adresse
        Assert.Equal(AccessRefusal.OffText, AccessRefusal.Explain(AccessRefusal.Off, "http://192.168.1.10:8585"));
        Assert.Equal(AccessRefusal.OffText, AccessRefusal.Explain(AccessRefusal.Off, "https://fleet.eksempel.dk"));
        Assert.Contains("slået fra", AccessRefusal.OffText);
        Assert.Contains("Anmod om adgang", AccessRefusal.OffText);
        Assert.Equal(AccessRefusal.OffText, AccessRequestClient.CreateError(HttpStatusCode.Forbidden, "off", "http://192.168.1.10:8585"));
        // en ukendt grund fra en nyere server: aldrig gæt
        Assert.Equal(AccessRefusal.Generic, AccessRefusal.Explain("noget-nyt", "https://fleet.eksempel.dk"));
        Assert.Equal(AccessRefusal.Generic, AccessRequestClient.CreateError(HttpStatusCode.Forbidden));
        Assert.Equal(AccessRefusal.OutsideText("fleet.eksempel.dk"), AccessRequestClient.CreateError(HttpStatusCode.Forbidden, "outside", "https://fleet.eksempel.dk"));
    }

    [Theory]
    [InlineData("""{"title":"Forbidden","status":403,"detail":"x","reason":"outside"}""", "outside")]
    [InlineData("""{"title":"Forbidden","status":403,"detail":"x"}""", null)]
    [InlineData("""{"reason":5}""", null)] [InlineData("[]", null)] [InlineData("ikke json", null)] [InlineData("", null)]
    public void Grunden_læses_fra_fejlsvaret(string body, string? reason) => Assert.Equal(reason, AccessRefusal.ReasonOf(body));

    // ---------- samme net ----------

    private static readonly IPNetwork Own = IPNetwork.Parse("2001:db8:1:2::/64");
    private static readonly IPAddress HomePublic = IPAddress.Parse("198.51.100.7");
    private static readonly HomeNetwork.Facts Facts = new([Own], HomePublic);

    private static HomeNetwork.Call Direct(string ip) => new(IPAddress.Parse(ip), IPAddress.Parse(ip), false, null, false);
    private static HomeNetwork.Call Tunnel(string peer, string cf, string? client = null) =>
        new(IPAddress.Parse(peer), IPAddress.Parse(client ?? peer), true, cf, false);

    [Theory]
    [InlineData("192.168.1.20", true)] [InlineData("::ffff:192.168.1.20", true)] [InlineData("fd00::20", true)] [InlineData("fe80::1234", true)]
    [InlineData("2001:db8:1:2::abcd", true)]      // global IPv6 i serverens eget præfiks
    [InlineData("2001:db8:1:3::abcd", false)]     // naboens præfiks
    [InlineData("203.0.113.9", false)] [InlineData("::ffff:203.0.113.9", false)] [InlineData("198.51.100.7", false)]
    public void Direkte_kald(string ip, bool home) => Assert.Equal(home, HomeNetwork.IsHome(Direct(ip), Facts));

    [Fact]
    public void Gennem_tunnelen_hjemmefra()
    {
        // cloudflared i containeren, klienten bag samme router (serverens egen offentlige IP)
        Assert.True(HomeNetwork.IsHome(Tunnel("127.0.0.1", "198.51.100.7"), Facts));
        Assert.True(HomeNetwork.IsHome(Tunnel("::ffff:127.0.0.1", "::ffff:198.51.100.7"), Facts));
        Assert.True(HomeNetwork.IsHome(Tunnel("10.20.30.5", "2001:db8:1:2::77"), Facts));
        // betroet proxy har læst X-Forwarded-For: klienten er den samme som Cloudflares
        Assert.True(HomeNetwork.IsHome(Tunnel("127.0.0.1", "198.51.100.7", client: "198.51.100.7"), Facts));
        // unix-socket (null) regnes som maskinen selv
        Assert.True(HomeNetwork.IsHome(new(null, null, true, "198.51.100.7", false), Facts));
        // nøgler "kun fra hjemmenettet" stoler ikke på tunnelen
        Assert.False(HomeNetwork.IsHome(Tunnel("127.0.0.1", "198.51.100.7"), Facts, trustTunnel: false));
    }

    [Fact]
    public void Gennem_tunnelen_udefra_eller_forfalsket()
    {
        Assert.False(HomeNetwork.IsHome(Tunnel("127.0.0.1", "203.0.113.9"), Facts));            // en anden internetforbindelse
        Assert.False(HomeNetwork.IsHome(Tunnel("127.0.0.1", "2001:db8:9::1"), Facts));          // et andet IPv6-net
        Assert.False(HomeNetwork.IsHome(Tunnel("127.0.0.1", "192.168.1.20"), Facts));           // Cloudflare sender aldrig en privat IP
        Assert.False(HomeNetwork.IsHome(Tunnel("127.0.0.1", "ikke-en-ip"), Facts));
        Assert.False(HomeNetwork.IsHome(new(IPAddress.Loopback, IPAddress.Loopback, true, null, false), Facts));   // kun CF-Ray
        // offentlig TCP-modpart med egne CF-headere: aldrig stol på dem
        Assert.False(HomeNetwork.IsHome(Tunnel("203.0.113.9", "198.51.100.7"), Facts));
        // klient udefra gennem en betroet proxy, der har sat X-Forwarded-For – headeren passer ikke med den rigtige klient
        Assert.False(HomeNetwork.IsHome(Tunnel("172.18.0.3", "198.51.100.7", client: "203.0.113.9"), Facts));
        // opslaget af den offentlige IP fejlede: som før (udefra)
        Assert.False(HomeNetwork.IsHome(Tunnel("127.0.0.1", "198.51.100.7"), new([Own], null)));
        Assert.False(HomeNetwork.IsHome(Tunnel("127.0.0.1", "198.51.100.7"), HomeNetwork.Facts.None));
    }

    [Fact]
    public void Den_offentlige_MDM_adresse_er_aldrig_hjemme()
    {
        Assert.False(HomeNetwork.IsHome(new(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.20"), false, null, true), Facts));
        Assert.False(HomeNetwork.IsHome(new(IPAddress.Loopback, IPAddress.Loopback, true, "198.51.100.7", true), Facts));
    }

    [Fact]
    public void Serverens_egne_IPv6_præfikser()
    {
        var list = HomeNetwork.GlobalV6Prefixes([
            (IPAddress.Parse("2001:db8:1:2::10"), 64),
            (IPAddress.Parse("2001:db8:1:2::11"), 64),       // samme præfiks: én gang
            (IPAddress.Parse("2001:db8:5::1"), 128),         // én adresse
            (IPAddress.Parse("2001:db8::1"), 32),            // for bredt
            (IPAddress.Parse("2001:db8:7:8:9::1"), 80),
            (IPAddress.Parse("fd00::1"), 64),                // ULA: privat i forvejen
            (IPAddress.Parse("fe80::1"), 64),
            (IPAddress.Parse("::ffff:192.168.1.2"), 120),
            (IPAddress.Parse("192.168.1.2"), 24),
        ]);
        Assert.Equal(["2001:db8:1:2::/64", "2001:db8:7:8:9::/80"], list.Select(n => n.ToString()));
    }

    [Theory]
    [InlineData("fl=1\nh=1.1.1.1\nip=198.51.100.7\nts=1\n", "198.51.100.7")]
    [InlineData("ip=2001:db8::1\n", null)] [InlineData("ip=10.20.30.1\n", null)] [InlineData("", null)] [InlineData(null, null)]
    public void Cloudflares_trace(string? body, string? ip) => Assert.Equal(ip, HomeNetwork.ParseTrace(body)?.ToString());

    [Fact]
    public async Task Peer_før_X_Forwarded_For_bruges_til_tunnelen()
    {
        // X-Forwarded-For fra cloudflared (betroet) har gjort klienten til den offentlige IP; TCP-modparten var 127.0.0.1
        var c = new DefaultHttpContext();
        c.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        c.Items[HomeNetwork.PeerKey] = IPAddress.Loopback;
        c.Request.Headers["CF-Connecting-IP"] = "203.0.113.9";
        var call = HomeNetwork.Read(c);
        Assert.Equal(IPAddress.Loopback, call.Peer);
        Assert.True(call.ViaCloudflare);
        Assert.True(HomeNetwork.IsHome(call, new([], IPAddress.Parse("203.0.113.9"))));
        // uden netværk i testen (ingen IHttpClientFactory): den offentlige IP er ukendt → udefra som før
        Assert.False(await ApiKeyGuard.FromHomeNetwork(c));
    }
}
