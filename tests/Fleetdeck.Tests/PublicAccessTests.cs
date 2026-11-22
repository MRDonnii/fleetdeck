using System.Diagnostics;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

public class PublicAccessTests
{
    // samme form som en rigtig Cloudflare-nøgle (base64 af {"a":…,"t":…,"s":…}), men opdigtet
    private const string Token = "eyJhIjoiMDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWYiLCJ0IjoiMTExMTExMTEtMjIyMi0zMzMzLTQ0NDQtNTU1NTU1NTU1NTU1IiwicyI6Ik1USXpORFUyTnpnNU1BPT0ifQ==";
    private const string Nonce = "Zm9vYmFyYmF6cXV4cXV1eGZvb2Jhcg";
    private static readonly TunnelConfig Cfg = new(7, "br0", "192.168.1.240", "fleetdeck-tunnel");

    [Theory]
    [InlineData("br0", true)]
    [InlineData("host", true)]
    [InlineData("bridge", true)]
    [InlineData("my_net.1-a", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("none", false)]
    [InlineData("-br0", false)]
    [InlineData("br0 --privileged", false)]
    [InlineData("br0;reboot", false)]
    [InlineData("$(id)", false)]
    [InlineData("br0'", false)]
    [InlineData("br0\n", false)]
    public void NetworkIsValidatedStrictly(string net, bool ok) => Assert.Equal(ok, PublicAccess.ValidateNetwork(net) is null);

    [Fact] public void NetworkNameHasAMaximumLength() => Assert.NotNull(PublicAccess.ValidateNetwork(new string('a', 65)));

    [Theory]
    [InlineData("", true)]
    [InlineData("192.168.1.53", true)]
    [InlineData("192.168.1.250", true)]
    [InlineData("172.16.5.1", true)]
    [InlineData("10.0.0", false)]
    [InlineData("192.168.1.256", false)]
    [InlineData("010.0.0.1", false)]
    [InlineData("192.168.1.0", false)]
    [InlineData("192.168.1.255", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("192.168.1.1 ", false)]
    [InlineData("192.168.1.1/24", false)]
    [InlineData("192.168.1.1;reboot", false)]
    [InlineData("192.168.1.1\n", false)]
    [InlineData("::1", false)]
    [InlineData("0x0a.0.0.1", false)]
    public void IpIsValidatedStrictly(string ip, bool ok) => Assert.Equal(ok, PublicAccess.ValidateIp(ip) is null);

    [Theory]
    [InlineData("fleetdeck-tunnel", true)]
    [InlineData("cf.tunnel_1", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData(".x", false)]
    [InlineData("a b", false)]
    [InlineData("x'y", false)]
    [InlineData("x$(id)", false)]
    [InlineData("x/y", false)]
    public void ContainerNameIsValidatedStrictly(string name, bool ok) => Assert.Equal(ok, PublicAccess.ValidateContainer(name) is null);

    [Fact] public void ContainerNameHasAMaximumLength() => Assert.NotNull(PublicAccess.ValidateContainer(new string('a', 64)));

    [Fact] public void FixedIpOnlyOnMacvlanNetworks()
    {
        Assert.Null(PublicAccess.Validate(Cfg));
        Assert.Null(PublicAccess.Validate(Cfg with { Network = "host", Ip = "" }));
        Assert.NotNull(PublicAccess.Validate(Cfg with { Network = "host" }));
        Assert.NotNull(PublicAccess.Validate(Cfg with { Network = "bridge" }));
    }

    [Fact] public void JobsRefuseInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => PublicAccess.StartJob(Cfg with { Network = "br0;reboot" }, Nonce));
        Assert.Throws<ArgumentException>(() => PublicAccess.StartJob(Cfg with { Ip = "192.168.1.1'" }, Nonce));
        Assert.Throws<ArgumentException>(() => PublicAccess.StartJob(Cfg, "nonce'; reboot; '"));
        Assert.Throws<ArgumentException>(() => PublicAccess.StopJob(Cfg with { Container = "x y" }));
        Assert.Throws<ArgumentException>(() => PublicAccess.SuggestJob(Cfg with { Network = "$(id)" }));
    }

    [Fact] public void TokenIsNeverPartOfTheJob()
    {
        // opsætningen, jobbet bygges af, har slet ikke nøglen
        Assert.DoesNotContain(typeof(TunnelConfig).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        foreach (var p in new[] { PublicAccess.StartJob(Cfg, Nonce), PublicAccess.StopJob(Cfg), PublicAccess.SuggestJob(Cfg) })
        {
            var json = Json.Ser(p);
            Assert.DoesNotContain(Token, json);
            Assert.DoesNotContain(Token[..20], json);
            Assert.DoesNotContain("--token", json);
            Assert.DoesNotContain("eyJ", json);
        }
        var start = PublicAccess.StartJob(Cfg, Nonce).Body!;
        // nøglen hentes af scriptet med engangskoden og gives videre som miljøvariabel (uden værdi på kommandolinjen)
        Assert.Contains(Nonce, start);
        Assert.Contains(PublicAccess.TokenPath, start);
        Assert.Contains("-e TUNNEL_TOKEN \"$IMAGE\"", start);
        Assert.Contains("--restart unless-stopped", start);
        Assert.Contains("'192.168.1.240'", start);
    }

    [Fact] public void ImageIsPinned() =>
        Assert.Matches(@"^cloudflare/cloudflared:\d{4}\.\d{1,2}\.\d+$", PublicAccess.Image);

    [Theory]
    [InlineData("sudo cloudflared service install " + Token)]
    [InlineData("cloudflared tunnel run --token " + Token)]
    [InlineData("  " + Token + "\n")]
    public void TokenIsTakenOutOfThePastedCommand(string pasted) => Assert.Equal(Token, PublicAccess.CleanToken(pasted));

    [Theory]
    [InlineData("")]
    [InlineData("hej")]
    [InlineData("eyJ'; reboot")]
    public void InvalidTokensAreRefused(string pasted) => Assert.Null(PublicAccess.CleanToken(pasted));

    [Fact] public void OutputIsMasked()
    {
        var masked = PublicAccess.Mask($"docker run -e TUNNEL_TOKEN={Token}\nok", Token);
        Assert.DoesNotContain(Token[..20], masked);
        Assert.Contains("***", masked);
        // også en nøgle, Fleetdeck ikke kender (fx en anden tunnel), og en nøgle uden "eyJ"
        Assert.DoesNotContain("eyJhIjoiOTk5", PublicAccess.Mask("x eyJhIjoiOTk5OTk5OTk5OTk5OTk5OTk5OTk5OTk5OTk5OTk5OTk5 y"));
        Assert.Equal("a *** b", PublicAccess.Mask("a QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo b", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo"));
    }

    [Theory]
    [InlineData("Leder …\n✓ 192.168.1.245 svarer ikke\nFORSLAG=192.168.1.245\n", "192.168.1.245")]
    [InlineData("FORSLAG=192.168.1.999\n", null)]
    [InlineData("FORSLAG=192.168.1.5;reboot\n", null)]
    [InlineData("✗ Fandt ingen", null)]
    public void SuggestedIpIsReadFromTheOutput(string output, string? ip) => Assert.Equal(ip, PublicAccess.SuggestedIp(output));

    [Fact] public void ScriptsAreValidShell()
    {
        if (!File.Exists("/bin/bash")) return;   // Windows
        foreach (var p in new[] { PublicAccess.StartJob(Cfg, Nonce), PublicAccess.StartJob(Cfg with { Network = "host", Ip = "" }, Nonce), PublicAccess.StopJob(Cfg), PublicAccess.SuggestJob(Cfg) })
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
}
