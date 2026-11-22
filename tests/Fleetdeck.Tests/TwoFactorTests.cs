using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;

namespace Fleetdeck.Tests;

/// <summary>TOTP mod testvektorerne i RFC 6238 (bilag B) og Base32 mod RFC 4648.</summary>
public class TotpTests
{
    private static readonly byte[] Sha1Key = Encoding.ASCII.GetBytes("12345678901234567890");
    private static readonly byte[] Sha256Key = Encoding.ASCII.GetBytes("12345678901234567890123456789012");
    private static readonly byte[] Sha512Key = Encoding.ASCII.GetBytes("1234567890123456789012345678901234567890123456789012345678901234");

    [Theory]
    [InlineData(59L, "94287082", "46119246", "90693936")]
    [InlineData(1111111109L, "07081804", "68084774", "25091201")]
    [InlineData(1111111111L, "14050471", "67062674", "99943326")]
    [InlineData(1234567890L, "89005924", "91819424", "93441116")]
    [InlineData(2000000000L, "69279037", "90698825", "38618901")]
    [InlineData(20000000000L, "65353130", "77737706", "47863826")]
    public void Rfc6238_vectors(long unix, string sha1, string sha256, string sha512)
    {
        var step = Totp.Step(DateTimeOffset.FromUnixTimeSeconds(unix));
        Assert.Equal(sha1, Totp.Code(Sha1Key, step, 8, "SHA1"));
        Assert.Equal(sha256, Totp.Code(Sha256Key, step, 8, "SHA256"));
        Assert.Equal(sha512, Totp.Code(Sha512Key, step, 8, "SHA512"));
        // 6 cifre = de sidste 6 af den 8-cifrede kode (samme trunkering)
        Assert.Equal(sha1[2..], Totp.Code(Sha1Key, step));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_rfc4648_vectors(string plain, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(plain)));
        Assert.Equal(plain, Encoding.ASCII.GetString(Base32.Decode(encoded)!));
        Assert.Equal(plain, Encoding.ASCII.GetString(Base32.Decode(encoded.ToLowerInvariant() + "===")!));
    }

    [Fact]
    public void Base32_rejects_invalid_characters() => Assert.Null(Base32.Decode("MZ1W"));

    [Fact]
    public void Window_allows_one_step_of_clock_skew_each_way_but_not_two()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var s = Totp.Step(now);
        Assert.Equal(s, Totp.Match(Sha1Key, Totp.Code(Sha1Key, s), now, 0));
        Assert.Equal(s - 1, Totp.Match(Sha1Key, Totp.Code(Sha1Key, s - 1), now, 0));
        Assert.Equal(s + 1, Totp.Match(Sha1Key, Totp.Code(Sha1Key, s + 1), now, 0));
        Assert.Null(Totp.Match(Sha1Key, Totp.Code(Sha1Key, s - 2), now, 0));
        Assert.Null(Totp.Match(Sha1Key, Totp.Code(Sha1Key, s + 2), now, 0));
    }

    [Fact]
    public void Used_step_cannot_be_replayed()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var s = Totp.Step(now);
        var code = Totp.Code(Sha1Key, s);
        Assert.Null(Totp.Match(Sha1Key, code, now, lastStep: s));
        // og en ældre kode inden for vinduet kan heller ikke bruges efter en nyere
        Assert.Null(Totp.Match(Sha1Key, Totp.Code(Sha1Key, s - 1), now, lastStep: s));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_codes_never_match(string? code) =>
        Assert.Null(Totp.Match(Sha1Key, code, DateTimeOffset.UtcNow, 0));

    [Fact]
    public void Code_with_spaces_is_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        var c = Totp.Code(Sha1Key, Totp.Step(now));
        Assert.NotNull(Totp.Match(Sha1Key, $"{c[..3]} {c[3..]}", now, 0));
    }

    [Fact]
    public void Otpauth_uri_contains_secret_and_issuer()
    {
        var uri = Totp.Uri("Fleetdeck", "anna b", "ABCDEF");
        Assert.StartsWith("otpauth://totp/Fleetdeck:anna%20b?secret=ABCDEF&issuer=Fleetdeck", uri);
    }
}

/// <summary>Gendannelseskoder, huskede enheder, tilmelding og spærring.</summary>
public class TwoFactorServiceTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock clock = new(DateTimeOffset.UtcNow);
    private readonly TwoFactorService tf;
    public TwoFactorServiceTests() => tf = new TwoFactorService(new EphemeralDataProtectionProvider(), clock);

    private User Enrolled(out byte[] key, int id = 1)
    {
        var u = new User { Id = id, Username = "anna", Role = Roles.Admin, PasswordHash = Crypto.HashPassword("hemmelig-kode") };
        var (secret, _, token) = tf.StartEnroll(u);
        key = Base32.Decode(secret)!;
        Assert.NotNull(tf.ConfirmEnroll(u, token, Totp.Code(key, Totp.Step(clock.Now))));
        clock.Now = clock.Now.AddSeconds(Totp.Period);   // næste kode er en ny
        return u;
    }

    private string Current(byte[] key) => Totp.Code(key, Totp.Step(clock.Now));

    [Fact]
    public void Enrollment_requires_a_valid_code_and_stores_the_secret_encrypted()
    {
        var u = new User { Id = 7, Username = "bo", Role = Roles.Viewer };
        var (secret, uri, token) = tf.StartEnroll(u);
        Assert.Contains(secret, uri);
        var good = Totp.Code(Base32.Decode(secret)!, Totp.Step(clock.Now));
        Assert.Null(tf.ConfirmEnroll(u, token, good == "000000" ? "111111" : "000000"));
        Assert.False(u.TotpEnabled);
        var stamp = u.SecurityStamp;
        var codes = tf.ConfirmEnroll(u, token, good);
        Assert.NotNull(codes);
        Assert.Equal(RecoveryCodes.Count, codes!.Count);
        Assert.True(u.TotpEnabled);
        Assert.DoesNotContain(secret, u.TotpSecret);          // krypteret med DataProtection
        Assert.NotEqual(stamp, u.SecurityStamp);              // andre sessioner logges ud
        Assert.All(u.RecoveryCodes, h => Assert.DoesNotContain(codes[0], h));
    }

    [Fact]
    public void Enrollment_token_is_bound_to_the_user()
    {
        var a = new User { Id = 1, Username = "a" };
        var b = new User { Id = 2, Username = "b" };
        var (secret, _, token) = tf.StartEnroll(a);
        Assert.Null(tf.ConfirmEnroll(b, token, Totp.Code(Base32.Decode(secret)!, Totp.Step(clock.Now))));
        Assert.Null(tf.ConfirmEnroll(a, token + "x", Totp.Code(Base32.Decode(secret)!, Totp.Step(clock.Now))));
    }

    [Fact]
    public void Totp_code_verifies_once()
    {
        var u = Enrolled(out var key);
        var code = Current(key);
        Assert.Equal(CodeKind.Totp, tf.Verify(u, code));
        Assert.Equal(CodeKind.Invalid, tf.Verify(u, code));
    }

    [Fact]
    public void Recovery_code_is_single_use()
    {
        var u = new User { Id = 1, Username = "anna" };
        var (secret, _, token) = tf.StartEnroll(u);
        var codes = tf.ConfirmEnroll(u, token, Totp.Code(Base32.Decode(secret)!, Totp.Step(clock.Now)))!;
        Assert.Equal(CodeKind.Recovery, tf.Verify(u, codes[3]));
        Assert.Equal(RecoveryCodes.Count - 1, u.RecoveryCodes.Count);
        Assert.Equal(CodeKind.Invalid, tf.Verify(u, codes[3]));
        // stavet anderledes (store bogstaver, uden bindestreg) virker også
        Assert.Equal(CodeKind.Recovery, tf.Verify(u, codes[4].ToUpperInvariant().Replace("-", "")));
        Assert.Equal(CodeKind.Invalid, tf.Verify(u, "abcde-fghjk"));
    }

    [Fact]
    public void New_recovery_codes_replace_the_old_ones()
    {
        var u = new User { Id = 1, Username = "anna" };
        var (secret, _, token) = tf.StartEnroll(u);
        var old = tf.ConfirmEnroll(u, token, Totp.Code(Base32.Decode(secret)!, Totp.Step(clock.Now)))!;
        var fresh = tf.NewRecoveryCodes(u);
        Assert.Equal(CodeKind.Invalid, tf.Verify(u, old[0]));
        Assert.Equal(CodeKind.Recovery, tf.Verify(u, fresh[0]));
    }

    [Fact]
    public void Recovery_codes_are_unique_and_well_formed()
    {
        var codes = RecoveryCodes.Generate();
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[0-9a-hjkmnp-tv-z]{5}-[0-9a-hjkmnp-tv-z]{5}$", c));
    }

    [Fact]
    public void Remembered_device_is_trusted_for_the_same_user_only()
    {
        var u = Enrolled(out _);
        var cookie = tf.TrustValue(u);
        Assert.True(tf.IsTrusted(u, cookie));
        var other = Enrolled(out _, id: 2);
        Assert.False(tf.IsTrusted(other, cookie));
        Assert.False(tf.IsTrusted(u, cookie[..^2] + "AA"));
        Assert.False(tf.IsTrusted(u, null));
    }

    [Fact]
    public void Remembered_device_is_forgotten_when_the_password_changes()
    {
        var u = Enrolled(out _);
        var cookie = tf.TrustValue(u);
        TwoFactorService.RotateStamp(u);   // det, skift af adgangskode gør
        Assert.False(tf.IsTrusted(u, cookie));
    }

    [Fact]
    public void Remembered_device_is_forgotten_when_2fa_is_reset()
    {
        var u = Enrolled(out var _);
        var cookie = tf.TrustValue(u);
        TwoFactorService.Disable(u);       // brugerens "slå fra" eller administratorens nulstilling
        Assert.False(u.TotpEnabled);
        Assert.Empty(u.RecoveryCodes);
        Assert.False(tf.IsTrusted(u, cookie));
        // og den gamle cookie virker heller ikke efter en ny tilmelding
        var (secret, _, token) = tf.StartEnroll(u);
        tf.ConfirmEnroll(u, token, Totp.Code(Base32.Decode(secret)!, Totp.Step(clock.Now)));
        Assert.True(u.TotpEnabled);
        Assert.False(tf.IsTrusted(u, cookie));
    }

    [Fact]
    public void Login_ticket_is_bound_to_user_and_stamp()
    {
        var u = Enrolled(out _);
        var t = tf.ReadTicket(tf.LoginTicket(u));
        Assert.Equal((u.Id, u.SecurityStamp), t);
        Assert.Null(tf.ReadTicket("noget-andet"));
        Assert.Null(tf.ReadTicket(tf.TrustValue(u)));   // andre formål kan ikke bruges som billet
    }

    [Fact]
    public void Lost_server_keys_leave_recovery_codes_working()
    {
        var u = new User { Id = 1, Username = "anna" };
        var (secret, _, token) = tf.StartEnroll(u);
        var codes = tf.ConfirmEnroll(u, token, Totp.Code(Base32.Decode(secret)!, Totp.Step(clock.Now)))!;
        var other = new TwoFactorService(new EphemeralDataProtectionProvider(), clock);   // nye nøgler
        Assert.Null(other.Secret(u));
        Assert.Equal(CodeKind.Recovery, other.Verify(u, codes[0]));
    }
}

public class LockoutTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Four_failures_do_not_lock()
    {
        var u = new User();
        for (var i = 0; i < 4; i++) Lockout.Fail(u, T0);
        Assert.Null(Lockout.Remaining(u, T0));
    }

    [Fact]
    public void Fifth_failure_locks_and_backoff_doubles_up_to_the_cap()
    {
        var u = new User();
        for (var i = 0; i < 5; i++) Lockout.Fail(u, T0);
        Assert.Equal(TimeSpan.FromMinutes(1), Lockout.Remaining(u, T0));
        Assert.Null(Lockout.Remaining(u, T0.AddMinutes(1)));
        Lockout.Fail(u, T0);
        Assert.Equal(TimeSpan.FromMinutes(2), Lockout.Remaining(u, T0));
        Lockout.Fail(u, T0);
        Assert.Equal(TimeSpan.FromMinutes(4), Lockout.Remaining(u, T0));
        for (var i = 0; i < 50; i++) Lockout.Fail(u, T0);
        Assert.Equal(Lockout.Max, Lockout.Remaining(u, T0));
    }

    [Fact]
    public void Success_resets_the_counter()
    {
        var u = new User();
        for (var i = 0; i < 6; i++) Lockout.Fail(u, T0);
        Lockout.Success(u);
        Assert.Equal(0, u.FailedLogins);
        Assert.Null(Lockout.Remaining(u, T0));
        Lockout.Fail(u, T0);
        Assert.Null(Lockout.Remaining(u, T0));
    }

    [Fact]
    public void Message_rounds_up_to_whole_minutes() =>
        Assert.Contains("om 2 min", Lockout.Message(TimeSpan.FromSeconds(61)));
}

/// <summary>Politikken (valgfri / påkrævet for administratorer / for alle) og håndhævelsen i autorisationen.</summary>
public class TwoFactorPolicyTests
{
    private static ClaimsPrincipal Session(string role, bool tfa) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, role), new Claim(TwoFactorPolicy.Claim, tfa ? "1" : "0")], "test"));

    [Theory]
    [InlineData("optional", "admin", false)]
    [InlineData("optional", "parent", false)]
    [InlineData("admins", "admin", true)]
    [InlineData("admins", "viewer", false)]
    [InlineData("admins", "parent", false)]
    [InlineData("all", "admin", true)]
    [InlineData("all", "viewer", true)]
    [InlineData("all", "parent", true)]
    public void Required_for_matrix(string policy, string role, bool required) =>
        Assert.Equal(required, TwoFactorPolicy.RequiredFor(policy, role));

    [Theory]
    [InlineData(true, false, "optional", LoginStep.Code)]
    [InlineData(true, true, "optional", LoginStep.SignIn)]
    [InlineData(true, false, "all", LoginStep.Code)]
    [InlineData(false, false, "optional", LoginStep.SignIn)]
    [InlineData(false, false, "admins", LoginStep.SignInMustEnroll)]
    [InlineData(false, true, "all", LoginStep.SignInMustEnroll)]
    public void Login_step_for_admin(bool enabled, bool trusted, string policy, LoginStep step) =>
        Assert.Equal(step, TwoFactorPolicy.Next(enabled, trusted, Roles.Admin, policy));

    [Fact]
    public void Viewer_without_2fa_signs_in_directly_when_only_admins_are_required() =>
        Assert.Equal(LoginStep.SignIn, TwoFactorPolicy.Next(false, false, Roles.Viewer, TwoFactorPolicy.Admins));

    [Fact]
    public void Session_without_required_2fa_is_not_satisfied()
    {
        Assert.False(TwoFactorPolicy.Satisfied(Session(Roles.Admin, false), TwoFactorPolicy.Admins));
        Assert.True(TwoFactorPolicy.Satisfied(Session(Roles.Admin, true), TwoFactorPolicy.Admins));
        Assert.True(TwoFactorPolicy.Satisfied(Session(Roles.Viewer, false), TwoFactorPolicy.Admins));
        Assert.False(TwoFactorPolicy.Satisfied(Session(Roles.Parent, false), TwoFactorPolicy.All));
        Assert.True(TwoFactorPolicy.Satisfied(Session(Roles.Admin, false), TwoFactorPolicy.Optional));
        Assert.False(TwoFactorPolicy.Satisfied(new ClaimsPrincipal(new ClaimsIdentity()), TwoFactorPolicy.Optional));
    }

    [Fact]
    public void Old_sessions_without_the_claim_count_as_without_2fa() =>
        Assert.False(TwoFactorPolicy.Satisfied(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, Roles.Admin)], "test")), TwoFactorPolicy.Admins));

    [Fact]
    public async Task Authorization_requirement_follows_the_current_policy()
    {
        var before = TwoFactorPolicy.Current;
        try
        {
            async Task<bool> Allowed(ClaimsPrincipal p)
            {
                var req = new TwoFactorRequirement();
                var ctx = new AuthorizationHandlerContext([req], p, null);
                await ((IAuthorizationHandler)req).HandleAsync(ctx);
                return ctx.HasSucceeded;
            }
            TwoFactorPolicy.Current = TwoFactorPolicy.Optional;
            Assert.True(await Allowed(Session(Roles.Admin, false)));
            TwoFactorPolicy.Current = TwoFactorPolicy.Admins;
            Assert.False(await Allowed(Session(Roles.Admin, false)));
            Assert.True(await Allowed(Session(Roles.Admin, true)));
            Assert.True(await Allowed(Session(Roles.Parent, false)));
            TwoFactorPolicy.Current = "noget-ugyldigt";
            Assert.Equal(TwoFactorPolicy.Optional, TwoFactorPolicy.Current);
        }
        finally { TwoFactorPolicy.Current = before; }
    }
}

/// <summary>CSRF: cookie-godkendte, usikre /api-kald kræver X-Fleetdeck-headeren.</summary>
public class CsrfTests
{
    private static async Task<(int Status, bool Passed)> Run(string method, string path, bool cookie, bool header, string? origin = "http://ond.example")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Host = new HostString("fleetdeck.local", 8585);
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        if (cookie) ctx.Request.Headers.Cookie = "ws_session=abc; andet=1";
        if (header) ctx.Request.Headers[Csrf.Header] = "1";
        ctx.Response.Body = new MemoryStream();
        var passed = false;
        await Csrf.Middleware(ctx, _ => { passed = true; return Task.CompletedTask; });
        return (ctx.Response.StatusCode, passed);
    }

    [Theory]
    [InlineData("POST", "/api/installers")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("DELETE", "/api/users/3")]
    [InlineData("POST", "/api/settings/rotate-key")]
    public async Task Cookie_request_without_header_is_rejected(string method, string path)
    {
        var (status, passed) = await Run(method, path, cookie: true, header: false);
        Assert.False(passed);
        Assert.Equal(403, status);
    }

    [Fact]
    public async Task Cookie_request_with_header_passes() =>
        Assert.True((await Run("POST", "/api/settings/rotate-key", cookie: true, header: true)).Passed);

    [Fact]
    public async Task Safe_methods_pass() =>
        Assert.True((await Run("GET", "/api/machines", cookie: true, header: false)).Passed);

    [Fact]
    public async Task Agent_calls_without_cookie_are_not_affected() =>
        Assert.True((await Run("POST", "/api/agent/checkin", cookie: false, header: false)).Passed);

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/2fa")]
    [InlineData("/api/account/2fa/disable")]
    public async Task Login_and_account_always_need_the_header(string path)
    {
        Assert.False((await Run("POST", path, cookie: false, header: false)).Passed);
        Assert.True((await Run("POST", path, cookie: false, header: true)).Passed);
    }

    [Fact]
    public async Task Server_login_without_origin_is_allowed_but_cross_site_is_not()
    {
        Assert.True((await Run("POST", "/api/auth/login", cookie: false, header: false, origin: null)).Passed);              // fx Kontrolrums auto-login
        Assert.True((await Run("POST", "/api/auth/login", cookie: false, header: false, origin: "http://fleetdeck.local:8585")).Passed);
        Assert.False((await Run("POST", "/api/auth/login", cookie: false, header: false, origin: "http://ond.example")).Passed);
        Assert.False((await Run("POST", "/api/auth/2fa", cookie: false, header: false, origin: null)).Passed);
    }

    [Fact]
    public async Task Non_api_paths_are_not_affected() =>
        Assert.True((await Run("POST", "/hubs/ui/negotiate", cookie: true, header: false)).Passed);
}

/// <summary>Betroede proxyer: X-Forwarded-For læses kun fra dem; X-Forwarded-Prefix (Kontrolrum) virker som før.</summary>
public class TrustedProxyTests
{
    private static TrustedProxies.Snapshot Snap(string text) => new(text, TrustedProxies.Parse(text, out _)!);

    private static async Task<HttpContext> Run(string remote, string? xff, Func<TrustedProxies.Snapshot> source, string? prefix = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        ctx.Request.Path = "/open/fleetdeck/api/auth/login";
        if (xff is not null) ctx.Request.Headers["X-Forwarded-For"] = xff;
        if (prefix is not null) ctx.Request.Headers["X-Forwarded-Prefix"] = prefix;
        var mw = ProxyHeaders.Create(_ => Task.CompletedTask, NullLoggerFactory.Instance, source);
        await mw(ctx);
        return ctx;
    }

    private static readonly TrustedProxies.Snapshot Default = Snap("127.0.0.0/8, ::1, 172.16.0.0/12");

    [Fact]
    public async Task Untrusted_sender_cannot_fake_its_ip() =>
        Assert.Equal("192.168.1.5", (await Run("192.168.1.5", "1.2.3.4", () => Default)).Connection.RemoteIpAddress!.ToString());

    [Fact]
    public async Task Trusted_proxy_supplies_the_client_ip() =>
        Assert.Equal("1.2.3.4", (await Run("127.0.0.1", "1.2.3.4", () => Default)).Connection.RemoteIpAddress!.ToString());

    [Fact]
    public async Task Client_supplied_entries_before_the_proxy_are_ignored() =>
        // klienten sendte selv "9.9.9.9"; proxyen tilføjede den rigtige adresse bagerst
        Assert.Equal("1.2.3.4", (await Run("127.0.0.1", "9.9.9.9, 1.2.3.4", () => Default)).Connection.RemoteIpAddress!.ToString());

    [Fact]
    public async Task Chain_of_trusted_proxies_is_followed() =>
        // cloudflared (Docker) → Kontrolrum (loopback) → Fleetdeck
        Assert.Equal("1.2.3.4", (await Run("127.0.0.1", "1.2.3.4, 172.17.0.3", () => Default)).Connection.RemoteIpAddress!.ToString());

    [Fact]
    public async Task Ipv4_mapped_loopback_is_trusted() =>
        Assert.Equal("1.2.3.4", (await Run("::ffff:127.0.0.1", "1.2.3.4", () => Default)).Connection.RemoteIpAddress!.ToString());

    [Fact]
    public async Task List_can_change_while_running()
    {
        var current = Default;
        Assert.Equal("192.168.1.5", (await Run("192.168.1.5", "1.2.3.4", () => current)).Connection.RemoteIpAddress!.ToString());
        current = Snap("192.168.1.5");
        Assert.Equal("1.2.3.4", (await Run("192.168.1.5", "1.2.3.4", () => current)).Connection.RemoteIpAddress!.ToString());
    }

    [Fact]
    public async Task Empty_list_trusts_nobody() =>
        Assert.Equal("127.0.0.1", (await Run("127.0.0.1", "1.2.3.4", () => Snap(""))).Connection.RemoteIpAddress!.ToString());

    [Fact]
    public async Task Path_prefix_still_works_from_an_untrusted_proxy()
    {
        // Kontrolrum på en anden maskine: understien skal stadig virke (login-siden under /open/fleetdeck/)
        var ctx = await Run("192.168.1.20", "1.2.3.4", () => Default, prefix: "/open/fleetdeck");
        Assert.Equal("/open/fleetdeck", ctx.Request.PathBase.Value);
        Assert.Equal("192.168.1.20", ctx.Connection.RemoteIpAddress!.ToString());
    }

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("192.168.1.0/24, 192.168.1.5", true)]
    [InlineData("192.168.1.0/24;fd00::/8\n::1", true)]
    [InlineData("kontrolrum.local", false)]
    [InlineData("192.168.1.0/33", false)]
    public void Parse_accepts_addresses_and_networks_only(string text, bool ok) =>
        Assert.Equal(ok, TrustedProxies.Parse(text, out _) is not null);

    [Fact]
    public void IsTrusted_uses_the_given_list()
    {
        Assert.True(TrustedProxies.IsTrusted(IPAddress.Parse("172.17.0.2"), Default));
        Assert.False(TrustedProxies.IsTrusted(IPAddress.Parse("192.168.1.2"), Default));
        Assert.False(TrustedProxies.IsTrusted(null, Default));
    }
}
