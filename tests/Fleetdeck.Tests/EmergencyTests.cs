using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Tests;

/// <summary>Nødkortet: kun administratorer, kun med den rigtige adgangskode, og hvert forsøg logges.</summary>
public class EmergencyAccessTests
{
    private readonly List<User> users = [];
    private readonly List<(string User, string Action, string Detail)> log = [];
    private readonly User admin, viewer, parent;

    public EmergencyAccessTests()
    {
        admin = Add(1, "admin1", Roles.Admin);
        viewer = Add(2, "viewer1", Roles.Viewer);
        parent = Add(3, "parent1", Roles.Parent);
    }

    private User Add(int id, string name, string role)
    {
        var u = new User { Id = id, Username = name, PasswordHash = Crypto.HashPassword("rigtig-kode"), Role = role };
        users.Add(u);
        return u;
    }

    private static ClaimsPrincipal As(User u, string? role = null) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Name, u.Username), new Claim(ClaimTypes.Role, role ?? u.Role)], "test"));

    private Task<EmergencyAccess.Denial?> Verify(ClaimsPrincipal p, string? pw) => EmergencyAccess.Verify(p, pw, "test",
        id => Task.FromResult(users.FirstOrDefault(u => u.Id == id)),
        (u, a, d) => { log.Add((u, a, d)); return Task.CompletedTask; });
    private List<(string User, string Action, string Detail)> Log() => log;

    [Fact]
    public async Task Admin_with_correct_password_is_allowed_and_logged()
    {
        Assert.Null(await Verify(As(admin), "rigtig-kode"));
        var a = Assert.Single(Log());
        Assert.Equal(("admin1", EmergencyAccess.AuditReveal), (a.User, a.Action));
    }

    [Theory]
    [InlineData("forkert", 403)]
    [InlineData("", 400)]
    [InlineData(null, 400)]
    [InlineData("RIGTIG-KODE", 403)]
    public async Task Admin_with_wrong_or_missing_password_is_denied_and_logged(string? pw, int status)
    {
        Assert.Equal(status, (await Verify(As(admin), pw))?.Status);
        var a = Assert.Single(Log());
        Assert.Equal(EmergencyAccess.AuditDenied, a.Action);
        Assert.DoesNotContain("rigtig", a.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_admins_are_denied_even_with_correct_password()
    {
        Assert.Equal(403, (await Verify(As(viewer), "rigtig-kode"))?.Status);
        Assert.Equal(403, (await Verify(As(parent), "rigtig-kode"))?.Status);
        Assert.All(Log(), a => Assert.Equal(EmergencyAccess.AuditDenied, a.Action));
    }

    [Fact]
    public async Task Role_comes_from_the_database_not_the_cookie()
    {
        // en gammel cookie med administratorrollen giver ikke adgang, når brugeren er sat ned til læser
        Assert.Equal(403, (await Verify(As(viewer, Roles.Admin), "rigtig-kode"))?.Status);
        // og en slettet bruger får heller ikke adgang
        var gone = new User { Id = 999, Username = "gone", Role = Roles.Admin, PasswordHash = Crypto.HashPassword("rigtig-kode") };
        Assert.Equal(403, (await Verify(As(gone), "rigtig-kode"))?.Status);
        Assert.Equal(403, (await Verify(new ClaimsPrincipal(new ClaimsIdentity()), "rigtig-kode"))?.Status);
    }

    [Fact]
    public async Task Changed_password_takes_effect_immediately()
    {
        admin.PasswordHash = Crypto.HashPassword("ny-kode");
        Assert.Equal(403, (await Verify(As(admin), "rigtig-kode"))?.Status);
        Assert.Null(await Verify(As(admin), "ny-kode"));
    }

    [Fact]
    public void Check_rejects_malformed_hash() =>
        Assert.Equal(403, EmergencyAccess.Check(new User { Role = Roles.Admin, PasswordHash = "noget-andet" }, "x")?.Status);
}

public class EmergencyExportTests
{
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=Test supervision", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Fact]
    public void P12_round_trips_with_chosen_password_only()
    {
        using var c = SelfSigned();
        var p12 = EmergencyExport.Pkcs12(c, "lang-nok-kode");
        using var back = X509CertificateLoader.LoadPkcs12(p12, "lang-nok-kode");
        Assert.True(back.HasPrivateKey);
        Assert.Equal(EmergencyExport.Fingerprint(c), EmergencyExport.Fingerprint(back));
        Assert.ThrowsAny<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(p12, "forkert-kode"));
    }

    [Fact]
    public void P12_requires_private_key_and_long_password()
    {
        using var c = SelfSigned();
        using var pub = X509CertificateLoader.LoadCertificate(c.RawData);
        Assert.Throws<InvalidOperationException>(() => EmergencyExport.Pkcs12(pub, "lang-nok-kode"));
        Assert.NotNull(EmergencyExport.ValidateP12Password("kort"));
        Assert.NotNull(EmergencyExport.ValidateP12Password(null));
        Assert.Null(EmergencyExport.ValidateP12Password("lang-nok-kode"));
    }

    [Fact]
    public void Fingerprint_is_colon_separated_sha256()
    {
        using var c = SelfSigned();
        var fp = EmergencyExport.Fingerprint(c);
        Assert.Equal(32, fp.Split(':').Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(c.RawData)), fp.Replace(":", ""));
    }

    [Fact]
    public void Tunnel_id_is_read_without_the_secret()
    {
        var id = Guid.NewGuid();
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"a":"acct","t":"{{id}}","s":"c2VjcmV0"}""")).TrimEnd('=');
        Assert.Equal(id.ToString(), EmergencyExport.TunnelId(token));
        Assert.Null(EmergencyExport.TunnelId("ikke-en-nøgle"));
        Assert.Null(EmergencyExport.TunnelId(null));
        Assert.Null(EmergencyExport.TunnelId(Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"t":"ikke-guid"}"""))));
    }
}
