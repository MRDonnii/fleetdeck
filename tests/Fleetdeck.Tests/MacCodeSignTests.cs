using System.Security.Cryptography.X509Certificates;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

public class MacCodeSignTests
{
    [Fact]
    public void IdentityIsSelfSignedCodeSigningCertWithKey()
    {
        var pw = MacCodeSign.NewPassword();
        var (pfx, sha1) = MacCodeSign.CreateIdentity(pw);
        using var c = X509CertificateLoader.LoadPkcs12(pfx, pw);
        Assert.True(c.HasPrivateKey);
        Assert.Equal("CN=" + MacCodeSign.CommonName, c.Subject);
        Assert.Equal(c.Subject, c.Issuer);
        Assert.Equal(sha1, c.Thumbprint);
        Assert.Matches("^[0-9A-F]{40}$", sha1);
        var eku = c.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.3");
        Assert.False(c.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.True(c.NotAfter > DateTime.UtcNow.AddYears(20));
    }

    [Fact]
    public void PasswordsAreRandomHexSafeForTheShell()
    {
        var a = MacCodeSign.NewPassword();
        Assert.Matches("^[0-9a-f]{48}$", a);
        Assert.NotEqual(a, MacCodeSign.NewPassword());
    }

    [Fact]
    public void CommandsQuotePathsAndUseRootsHome()
    {
        var sign = MacCodeSign.SignCmd("/usr/local/fleetdeck/fleetdeck-agent.new", "ABCDEF0123456789ABCDEF0123456789ABCDEF01");
        Assert.Equal("HOME=/var/root codesign -f -s ABCDEF0123456789ABCDEF0123456789ABCDEF01 --keychain '/usr/local/fleetdeck/signing.keychain-db' " +
                     "--identifier com.fleetdeck.agent --timestamp=none '/usr/local/fleetdeck/fleetdeck-agent.new'", sign);
        Assert.Contains("-T /usr/bin/codesign", MacCodeSign.ImportCmd("/tmp/x.p12", "pw"));
        Assert.Contains("-f pkcs12 -P pw", MacCodeSign.ImportCmd("/tmp/x.p12", "pw"));
        Assert.StartsWith("HOME=/var/root security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k pw ", MacCodeSign.PartitionCmd("pw"));
        Assert.Contains("set-keychain-settings '/usr/local/fleetdeck/signing.keychain-db'", MacCodeSign.CreateKeychainCmd("pw"));
        Assert.Equal("codesign -v 'a b' 2>/dev/null || codesign -f -s - --identifier com.fleetdeck.agent 'a b'", MacCodeSign.AdhocCmd("a b"));
        Assert.Equal("'it'\\''s'", MacCodeSign.Q("it's"));
    }

    [Fact]
    public void FindsIdentityEvenWhenNotTrusted()
    {
        const string output = """
            Policy: Code Signing
              Matching identities
              1) 1111111111111111111111111111111111111111 "Someone Else"
              2) 4e7d0a7bd2a3b6f0c5f2a9e4c1d8b7a6e5f4d3c2 "Fleetdeck Agent (local)" (CSSMERR_TP_NOT_TRUSTED)
                 2 identities found

              Valid identities only
                 0 valid identities found
            """;
        Assert.Equal("4E7D0A7BD2A3B6F0C5F2A9E4C1D8B7A6E5F4D3C2", MacCodeSign.FindIdentity(output));
        Assert.Null(MacCodeSign.FindIdentity("     0 identities found"));
    }

    [Fact]
    public void DescribesStableAndAdhocSignatures()
    {
        var stable = MacCodeSign.Describe("""
            Executable=/usr/local/fleetdeck/fleetdeck-agent
            Identifier=com.fleetdeck.agent
            Format=Mach-O thin (arm64)
            CodeDirectory v=20400 size=1234 flags=0x0(none) hashes=30+2 location=embedded
            Authority=Fleetdeck Agent (local)
            Signed Time=17 Nov 2026 at 10.00.00
            TeamIdentifier=not set
            """);
        Assert.True(stable.Stable);
        Assert.Equal("com.fleetdeck.agent", stable.Identifier);

        var adhoc = MacCodeSign.Describe("""
            Executable=/usr/local/fleetdeck/fleetdeck-agent
            Identifier=fleetdeck-agent
            CodeDirectory v=20400 size=1234 flags=0x20002(adhoc,linker-signed) hashes=30+0 location=embedded
            Signature=adhoc
            TeamIdentifier=not set
            """);
        Assert.False(adhoc.Stable);
        Assert.Equal("ad hoc", adhoc.Text);

        Assert.False(MacCodeSign.Describe("/x: code object is not signed at all").Stable);
        // vores certifikat, men forkert identifier: TCC-kravet ville ikke passe
        Assert.False(MacCodeSign.Describe("Identifier=fleetdeck-agent\nAuthority=Fleetdeck Agent (local)\n").Stable);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaa", "aaaaaaaaaaaa bbbbbbbbbbbb\n", "bbbbbbbbbbbb")]   // vores signerede fil: meld serverens version
    [InlineData("cccccccccccc", "aaaaaaaaaaaa bbbbbbbbbbbb", "cccccccccccc")]     // filen er skiftet siden: dens egen hash
    [InlineData("aaaaaaaaaaaa", null, "aaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaa", "rod", "aaaaaaaaaaaa")]
    public void ReportsServerVersionOnlyForOwnSignedFile(string current, string? origin, string expected) =>
        Assert.Equal(expected, MacCodeSign.ReportedVersion(current, origin));

    [Fact]
    public void FullDiskAccessFindings()
    {
        Assert.Empty(HealthService.MacAccessFindings(null));
        Assert.Empty(HealthService.MacAccessFindings(new MacAccessInfo { FullDiskAccess = true, StableSignature = true }));
        Assert.Empty(HealthService.MacAccessFindings(new MacAccessInfo { FullDiskAccess = null, StableSignature = true }));

        var f = HealthService.MacAccessFindings(new MacAccessInfo { FullDiskAccess = false, NeededForIpadBackup = true, StableSignature = true }).Single();
        Assert.Equal(("fulldisk", "warning", "Fuld diskadgang mangler"), (f.Key, f.Severity, f.Title));
        Assert.Equal("info", HealthService.MacAccessFindings(new MacAccessInfo { FullDiskAccess = false, StableSignature = true }).Single().Severity);
        Assert.Contains("ad hoc", HealthService.MacAccessFindings(new MacAccessInfo { FullDiskAccess = false, Signature = "ad hoc" }).Single().Detail);

        var s = HealthService.MacAccessFindings(new MacAccessInfo { FullDiskAccess = true, StableSignature = false, Signature = "ad hoc" }).Single();
        Assert.Equal(("macsign", "info"), (s.Key, s.Severity));

        foreach (var k in new[] { "fulldisk", "macsign" })
        {
            var a = HelperCatalog.AdviceFor(k, "warning", Platforms.Mac);
            Assert.NotNull(a);
            Assert.Null(a!.FixId);
        }
        Assert.Contains("Fuld diskadgang", HelperCatalog.AdviceFor("fulldisk", "warning", Platforms.Mac)!.What);
    }
}
