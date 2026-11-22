using System.Security.Cryptography;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

public class SigningTests
{
    private static (ECDsa Key, string Pem) NewKey()
    {
        var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (k, k.ExportSubjectPublicKeyInfoPem());
    }

    private static (string File, string Hash, string Sig) SignedFile(ECDsa key, byte[] content)
    {
        var f = Path.GetTempFileName();
        File.WriteAllBytes(f, content);
        var h = SHA256.HashData(content);
        return (f, Convert.ToHexStringLower(h), AgentSigning.Sign(key, h));
    }

    [Fact]
    public void AcceptsFileSignedWithPinnedKey()
    {
        var (key, pem) = NewKey();
        var (f, h, s) = SignedFile(key, [1, 2, 3, 4]);
        Assert.True(AgentSigning.Check(f, pem, h, s).Ok);
        File.Delete(f);
    }

    [Fact]
    public void RejectsChangedFile()
    {
        var (key, pem) = NewKey();
        var (f, h, s) = SignedFile(key, [1, 2, 3, 4]);
        File.WriteAllBytes(f, [9, 9, 9]);
        Assert.False(AgentSigning.Check(f, pem, h, s).Ok);
        Assert.False(AgentSigning.Check(f, pem, null, s).Ok);   // også uden kontrolsum fra serveren
        File.Delete(f);
    }

    [Fact]
    public void RejectsOtherKeyOrMissingSignature()
    {
        var (key, _) = NewKey();
        var (_, otherPem) = NewKey();
        var (f, h, s) = SignedFile(key, [5, 6, 7]);
        Assert.False(AgentSigning.Check(f, otherPem, h, s).Ok);
        var (_, pem) = (key, key.ExportSubjectPublicKeyInfoPem());
        Assert.False(AgentSigning.Check(f, pem, h, null).Ok);
        Assert.False(AgentSigning.Check(f, pem, h, "ikke-base64!").Ok);
        File.Delete(f);
    }

    [Fact]
    public void FingerprintIsStable()
    {
        var (_, pem) = NewKey();
        Assert.Equal(AgentSigning.Fingerprint(pem), AgentSigning.Fingerprint(pem + "\n"));
        Assert.Equal(16, AgentSigning.Fingerprint(pem).Length);
    }
}
