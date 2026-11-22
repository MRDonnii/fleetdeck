using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleetdeck.Tests;

/// <summary>Signerede jobs: kanonisk signering, manipulation, gentagelse, udløb, TOFU og selvopdatering.</summary>
public class SignedCommandsTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Store : ITrustStore
    {
        public string? JobKey { get; set; }
        public string? TlsPin { get; set; }
    }

    private static (ECDsa Key, string Pem) NewKey()
    {
        var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (k, k.ExportSubjectPublicKeyInfoPem());
    }

    private static SignedCommands Commands(int machine = 7, string nonce = "n-0123456789abcdef", DateTime? issued = null) => new()
    {
        MachineId = machine, Nonce = nonce, Issued = issued ?? Now, Expires = (issued ?? Now) + CommandSigning.Lifetime,
        Jobs = [new AgentJob { Id = 42, Type = JobTypes.Script, Title = "Test", Params = new JobParams { Body = "echo ok" } }],
        AgentVersion = "abcdef123456", AgentSha256 = new string('a', 64), ServerUrl = "http://fleetdeck.local:8585",
        KeepRunning = ["svc"], Drives = [new DriveSpec { Key = "d1", Host = "nas", Share = "data", Name = "Data" }],
    };

    private static CheckinResponse Response(CommandEnvelope? env) => new()
    {
        Signed = env,
        // de usignerede felter (til ældre agenter): her med noget andet end i det signerede indhold
        Jobs = [new AgentJob { Id = 99, Type = JobTypes.Script, Params = new JobParams { Body = "evil" } }],
        AgentVersion = "000000000000", ServerUrl = "http://evil.example", KeepRunning = ["evil"],
    };

    [Fact]
    public void SignedPayloadVerifies()
    {
        var (key, pem) = NewKey();
        var env = CommandSigning.Sign(key, pem, Commands());
        var v = CommandSigning.Verify(pem, env, 7, "n-0123456789abcdef", Now);
        Assert.True(v.Ok, v.Why);
        Assert.Equal("echo ok", v.Commands!.Jobs.Single().Params.Body);
        Assert.Equal(AgentSigning.Fingerprint(pem), env.KeyId);
    }

    [Fact]
    public void SignatureCoversExactBytesNotReserialization()
    {
        // En nyere server kan have felter, en ældre agent ikke kender: signaturen gælder de rå bytes, så den holder alligevel
        var (key, pem) = NewKey();
        var json = JsonSerializer.Serialize(Commands(), CommandSigning.Json);
        var payload = json.Insert(1, "\"futureField\":{\"x\":[1,2,3]},");
        var env = new CommandEnvelope { Payload = payload, Signature = Convert.ToBase64String(key.SignData(CommandSigning.Message(payload), HashAlgorithmName.SHA256)) };
        var v = CommandSigning.Verify(pem, env, 7, "n-0123456789abcdef", Now);
        Assert.True(v.Ok, v.Why);
        Assert.Single(v.Commands!.Jobs);
    }

    [Fact]
    public void TamperedPayloadOrSignatureIsRejected()
    {
        var (key, pem) = NewKey();
        var env = CommandSigning.Sign(key, pem, Commands());
        var tampered = new CommandEnvelope { Payload = env.Payload.Replace("echo ok", "rm -rf /"), Signature = env.Signature };
        Assert.False(CommandSigning.Verify(pem, tampered, 7, "n-0123456789abcdef", Now).Ok);
        var sig = Convert.FromBase64String(env.Signature); sig[5] ^= 1;
        Assert.False(CommandSigning.Verify(pem, new CommandEnvelope { Payload = env.Payload, Signature = Convert.ToBase64String(sig) }, 7, "n-0123456789abcdef", Now).Ok);
        Assert.False(CommandSigning.Verify(pem, new CommandEnvelope { Payload = env.Payload, Signature = "ikke base64!" }, 7, "n-0123456789abcdef", Now).Ok);
        Assert.False(CommandSigning.Verify(pem, null, 7, "n-0123456789abcdef", Now).Ok);
    }

    [Fact]
    public void OtherKeyIsRejected()
    {
        var (key, pem) = NewKey();
        var (_, otherPem) = NewKey();
        Assert.False(CommandSigning.Verify(otherPem, CommandSigning.Sign(key, pem, Commands()), 7, "n-0123456789abcdef", Now).Ok);
    }

    [Fact]
    public void DomainSeparatesFromFileSignatures()
    {
        // En signatur over et check-in-svar må aldrig kunne bruges som signatur over en "agentfil" med samme indhold
        var (key, pem) = NewKey();
        var env = CommandSigning.Sign(key, pem, Commands());
        var f = Path.GetTempFileName();
        File.WriteAllText(f, env.Payload);
        Assert.False(AgentSigning.Check(f, pem, null, env.Signature).Ok);
        File.Delete(f);
    }

    [Fact]
    public void ReplayWithOtherNonceIsRejected()
    {
        var (key, pem) = NewKey();
        var old = CommandSigning.Sign(key, pem, Commands(nonce: "gammel-nonce-0001"));
        var v = CommandSigning.Verify(pem, old, 7, "ny-nonce-00000002", Now);
        Assert.False(v.Ok);
        Assert.Contains("gentaget", v.Why);
    }

    [Fact]
    public void OtherMachineIsRejected()
    {
        var (key, pem) = NewKey();
        Assert.False(CommandSigning.Verify(pem, CommandSigning.Sign(key, pem, Commands(machine: 8)), 7, "n-0123456789abcdef", Now).Ok);
    }

    [Fact]
    public void ExpiryIsCheckedWithoutNonce()
    {
        var (key, pem) = NewKey();
        var env = CommandSigning.Sign(key, pem, Commands());
        Assert.True(CommandSigning.Verify(pem, env, 7, null, Now + CommandSigning.Lifetime).Ok);
        var late = CommandSigning.Verify(pem, env, 7, null, Now + CommandSigning.Lifetime + CommandSigning.Skew + TimeSpan.FromSeconds(1));
        Assert.False(late.Ok);
        Assert.Contains("udløbet", late.Why);
    }

    [Fact]
    public void FreshNonceSurvivesWrongClockOnAgent()
    {
        // fx en Raspberry Pi uden batteriur: svaret bærer agentens egen nonce og er dermed beviseligt nyt
        var (key, pem) = NewKey();
        var env = CommandSigning.Sign(key, pem, Commands());
        Assert.True(CommandSigning.Verify(pem, env, 7, "n-0123456789abcdef", Now.AddDays(3)).Ok);
        Assert.True(CommandSigning.Verify(pem, env, 7, "n-0123456789abcdef", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ok);
    }

    [Fact]
    public void TokenIdComesFromToken()
    {
        Assert.Equal(12, CommandSigning.TokenId("12.abcdef"));
        Assert.Equal(0, CommandSigning.TokenId("abc"));
        Assert.Equal(0, CommandSigning.TokenId(null));
    }

    [Fact]
    public void UnpinnedAgentWithOldServerWorksAsBefore()
    {
        var store = new Store();
        var r = Response(null);
        var o = CommandGate.Apply(r, store, 7, "n-0123456789abcdef", Now);
        Assert.True(o.Trusted);
        Assert.False(o.Pinned);
        Assert.Null(store.JobKey);
        Assert.Equal(99, r.Jobs.Single().Id);   // uændret: de usignerede jobs bruges som før
    }

    [Fact]
    public void FirstSignedResponsePinsKeyAndUsesOnlySignedContent()
    {
        var (key, pem) = NewKey();
        var store = new Store();
        var r = Response(CommandSigning.Sign(key, pem, Commands()));
        var o = CommandGate.Apply(r, store, 7, "n-0123456789abcdef", Now);
        Assert.True(o.Trusted);
        Assert.True(o.Pinned);
        Assert.True(o.ConfigChanged);
        Assert.Equal(pem, store.JobKey);
        Assert.Equal(42, r.Jobs.Single().Id);
        Assert.Equal("http://fleetdeck.local:8585", r.ServerUrl);
        Assert.Equal("abcdef123456", r.AgentVersion);
        Assert.Equal(["svc"], r.KeepRunning);
        Assert.Equal(new string('a', 64), o.AgentSha256);
    }

    [Fact]
    public void PinnedAgentRejectsUnsignedJobs()
    {
        var (_, pem) = NewKey();
        var store = new Store { JobKey = pem };
        var r = Response(null);
        var o = CommandGate.Apply(r, store, 7, "n-0123456789abcdef", Now);
        Assert.False(o.Trusted);
        Assert.NotNull(o.Error);
        Assert.Empty(r.Jobs);
        Assert.Equal("", r.AgentVersion);
        Assert.Equal("", r.ServerUrl);
        Assert.Empty(r.KeepRunning);
    }

    [Fact]
    public void PinnedAgentDoesNotRePinToAnotherKey()
    {
        var (_, pinned) = NewKey();
        var (evil, evilPem) = NewKey();
        var store = new Store { JobKey = pinned };
        var r = Response(CommandSigning.Sign(evil, evilPem, Commands()));   // tilbyder sin egen nøgle
        var o = CommandGate.Apply(r, store, 7, "n-0123456789abcdef", Now);
        Assert.False(o.Trusted);
        Assert.Equal(pinned, store.JobKey);
        Assert.Empty(r.Jobs);
    }

    [Fact]
    public void PinnedAgentRejectsReplayedSignedResponse()
    {
        var (key, pem) = NewKey();
        var store = new Store { JobKey = pem };
        var r = Response(CommandSigning.Sign(key, pem, Commands(nonce: "gammel-nonce-0001")));
        Assert.False(CommandGate.Apply(r, store, 7, "ny-nonce-00000002", Now).Trusted);
        Assert.Empty(r.Jobs);
    }

    [Fact]
    public void UnpinnedAgentIgnoresBrokenSignatureWithoutPinning()
    {
        // ikke pinnet endnu: et ugyldigt signeret svar pinner intet og strander ikke agenten
        var (key, pem) = NewKey();
        var env = CommandSigning.Sign(key, pem, Commands(machine: 8));
        var store = new Store();
        var r = Response(env);
        var o = CommandGate.Apply(r, store, 7, "n-0123456789abcdef", Now);
        Assert.True(o.Trusted);
        Assert.False(o.Pinned);
        Assert.Null(store.JobKey);
        Assert.NotNull(o.Error);
    }

    [Fact]
    public void SelfUpdateHashIsCheckedAgainstSignedPayload()
    {
        var f = Path.GetTempFileName();
        File.WriteAllBytes(f, [1, 2, 3, 4, 5]);
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)));
        Assert.True(CommandGate.CheckUpdate(f, pinned: true, sha).Ok);
        Assert.True(CommandGate.CheckUpdate(f, pinned: true, sha.ToUpperInvariant()).Ok);
        Assert.False(CommandGate.CheckUpdate(f, pinned: true, new string('0', 64)).Ok);
        Assert.False(CommandGate.CheckUpdate(f, pinned: true, null).Ok);    // pinnet: opdatering kræver signeret kontrolsum
        Assert.True(CommandGate.CheckUpdate(f, pinned: false, null).Ok);    // ældre server: den gamle filsignatur gælder
        Assert.False(CommandGate.CheckUpdate(f, pinned: false, new string('0', 64)).Ok);
        File.Delete(f);
    }

    [Fact]
    public void EnrollResponsePinsKeyAndKeepsOldPinsOnReinstall()
    {
        var store = new Store();
        TrustSetup.Keep(store, new Store { JobKey = "gammel", TlsPin = Convert.ToBase64String(new byte[32]) });
        Assert.Equal("gammel", store.JobKey);
        TrustSetup.FromEnroll(store, new EnrollResponse(1, "1.x", 30, JobKey: "ny"));
        Assert.Equal("ny", store.JobKey);
        Assert.Equal(Convert.ToBase64String(new byte[32]), store.TlsPin);
        var report = TrustSetup.Report(new Store(), "fejl");
        Assert.False(report.JobKeyPinned);
        Assert.Equal("fejl", report.Error);
    }
}

public class TlsPinningTests
{
    [Fact]
    public void NormalizesPinFormats()
    {
        var raw = SHA256.HashData([1, 2, 3]);
        var b64 = Convert.ToBase64String(raw);
        Assert.Equal(b64, TlsPinning.Normalize(b64));
        Assert.Equal(b64, TlsPinning.Normalize("sha256//" + b64));
        Assert.Equal(b64, TlsPinning.Normalize(Convert.ToHexString(raw)));
        Assert.Equal(b64, TlsPinning.Normalize(string.Join(':', raw.Select(b => b.ToString("X2")))));
        Assert.Null(TlsPinning.Normalize("nej"));
        Assert.Null(TlsPinning.Normalize(""));
    }

    [Fact]
    public void SelfSignedCertificateIsAcceptedOnlyWithMatchingPin()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        TlsInfo.CreateSelfSigned(dir, ["localhost", "192.168.1.5"]);
        using var cert = X509Certificate2.CreateFromPemFile(Path.Combine(dir, TlsInfo.CertFile), Path.Combine(dir, TlsInfo.KeyFile));
        var pin = TlsPinning.Of(cert);
        Assert.True(TlsPinning.Accept(cert, SslPolicyErrors.RemoteCertificateChainErrors, [pin]));
        Assert.True(TlsPinning.Accept(cert, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, [pin]));
        Assert.False(TlsPinning.Accept(cert, SslPolicyErrors.RemoteCertificateChainErrors, [Convert.ToBase64String(new byte[32])]));
        Assert.False(TlsPinning.Accept(cert, SslPolicyErrors.RemoteCertificateChainErrors, []));
        Assert.True(TlsPinning.Accept(cert, SslPolicyErrors.None, []));   // et certifikat, systemet stoler på, virker som før
        Directory.Delete(dir, true);
    }
}

/// <summary>Tilmelding: dubletnavne overtager ikke, engangskoder, godkendelse og check-in for gamle/nye agenter.</summary>
public sealed class EnrollmentTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;
    private readonly SettingsService _s;
    private readonly EnrollmentService _enroll;
    private const string Key = "global-key-123456789012";

    public EnrollmentTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _s = new SettingsService(_db);
        _s.Set("enroll_key", Key).GetAwaiter().GetResult();
        _enroll = new EnrollmentService(_db, _s, new AuditService(_db));
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private Task<EnrollmentService.Outcome> Enroll(string name, string key = Key, string? previous = null, bool provision = false) =>
        _enroll.Enroll(new EnrollRequest(key, name, provision, null, Platform: Platforms.Linux, PreviousToken: previous));

    private static string Token(EnrollmentService.Outcome o) => $"{o.Machine!.Id}.{o.Secret}";

    [Fact]
    public async Task WrongKeyIsRejected()
    {
        Assert.Equal(403, (await Enroll("PC1", "forkert")).Status);
        Assert.Equal(400, (await Enroll("ugyldigt navn!")).Status);
    }

    [Fact]
    public async Task DuplicateNameDoesNotTakeOverExistingMachine()
    {
        var first = await Enroll("PC1");
        Assert.True(first.Ok);
        var original = first.Machine!;
        original.ProfileId = null; original.Notes = "vigtig";
        await _db.SaveChangesAsync();
        _db.Jobs.Add(new Job { MachineId = original.Id, Type = JobTypes.Script, Title = "venter" });
        await _db.SaveChangesAsync();

        var second = await Enroll("pc1", provision: true);
        Assert.True(second.Ok);
        Assert.True(second.Pending);
        Assert.NotEqual(original.Id, second.Machine!.Id);
        Assert.Equal("PC1~2", second.Machine.Name);
        Assert.Equal(original.Id, second.Machine.ReplacesId);
        Assert.True(second.Machine.PendingProvision);
        // den gamle maskine er urørt: dens token virker stadig, og jobbet venter stadig
        Assert.Equal(original.Id, (await AgentAuth.Resolve(_db, Token(first)))!.Id);
        Assert.Equal(JobStatus.Pending, (await _db.Jobs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(second.Machine.Id, (await AgentAuth.Resolve(_db, Token(second)))!.Id);
        // en tredje får sit eget navn
        Assert.Equal("PC1~3", (await Enroll("PC1")).Machine!.Name);
    }

    [Fact]
    public async Task PreviousTokenProvesSameMachine()
    {
        var first = await Enroll("PC1");
        var again = await Enroll("PC1", previous: Token(first));
        Assert.False(again.Pending);
        Assert.Equal(first.Machine!.Id, again.Machine!.Id);
        Assert.Equal(EnrollmentService.Methods.Token, again.Method);
        Assert.Null(await AgentAuth.Resolve(_db, Token(first)));   // det gamle token er erstattet
        Assert.NotNull(await AgentAuth.Resolve(_db, Token(again)));
        // et token fra en anden maskine beviser intet
        var other = await Enroll("PC2");
        Assert.True((await Enroll("PC1", previous: Token(other))).Pending);
    }

    [Fact]
    public async Task ApproveReplacementMovesIdentityAndKeepsTokenWorking()
    {
        var first = await Enroll("PC1");
        var pending = await Enroll("PC1");
        var pendingToken = Token(pending);
        var (target, err) = await _enroll.ApproveReplacement(pending.Machine!.Id, "admin");
        Assert.Null(err);
        Assert.Equal(first.Machine!.Id, target!.Id);
        Assert.False(await _db.Machines.AnyAsync(m => m.Id == pending.Machine.Id));
        // den nye agent bruger stadig sit token (med den ventende posts id) og rammer nu den gamle maskine
        Assert.Equal(first.Machine.Id, (await AgentAuth.Resolve(_db, pendingToken))!.Id);
        Assert.Null(await AgentAuth.Resolve(_db, Token(first)));
        Assert.Equal(pending.Machine.Id, target.TokenAlias);
    }

    [Fact]
    public async Task ApproveAsNewKeepsBoth()
    {
        var first = await Enroll("PC1");
        var pending = await Enroll("PC1");
        var (m, err) = await _enroll.ApproveAsNew(pending.Machine!.Id, "admin");
        Assert.Null(err);
        Assert.Null(m!.ReplacesId);
        Assert.NotNull(await AgentAuth.Resolve(_db, Token(first)));
        Assert.NotNull(await AgentAuth.Resolve(_db, Token(pending)));
    }

    [Fact]
    public async Task OneTimeCodeWorksOnceAndExpires()
    {
        var c = await _enroll.CreateCode("ny bærbar", TimeSpan.FromHours(1), null, null, "admin");
        Assert.StartsWith(EnrollmentService.CodePrefix, c.Code);
        Assert.Equal(EnrollmentService.KeyKind.Code, (await _enroll.CheckKey(c.Code)).Kind);
        var o = await Enroll("LAPTOP", c.Code);
        Assert.True(o.Ok);
        Assert.Equal(EnrollmentService.Methods.Code, o.Method);
        Assert.Equal(403, (await Enroll("LAPTOP2", c.Code)).Status);   // brugt
        var row = await _db.EnrollCodes.AsNoTracking().SingleAsync();
        Assert.Equal(o.Machine!.Id, row.UsedByMachineId);
        Assert.NotEqual(c.Code, row.CodeHash);                           // kun hash gemmes

        var exp = await _enroll.CreateCode("udløber", TimeSpan.FromHours(1), null, null, "admin");
        Assert.Equal(EnrollmentService.KeyKind.Invalid, (await _enroll.CheckKey(exp.Code, DateTime.UtcNow.AddHours(2))).Kind);
        Assert.Equal(403, (await _enroll.Enroll(new EnrollRequest(exp.Code, "X1", false, null), DateTime.UtcNow.AddHours(2))).Status);
    }

    [Fact]
    public async Task CodeForSpecificMachineAllowsReinstall()
    {
        var first = await Enroll("PC1");
        var c = await _enroll.CreateCode("geninstallation", TimeSpan.FromHours(1), first.Machine!.Id, null, "admin");
        var o = await Enroll("PC1", c.Code);
        Assert.False(o.Pending);
        Assert.Equal(first.Machine.Id, o.Machine!.Id);
        // en almindelig kode giver ikke ret til at overtage
        var plain = await _enroll.CreateCode("almindelig", TimeSpan.FromHours(1), null, null, "admin");
        Assert.True((await Enroll("PC1", plain.Code)).Pending);
    }

    [Fact]
    public async Task GlobalKeyCanBeTurnedOffWhileCodesKeepWorking()
    {
        await _s.Set("enroll_key_enabled", "0");
        Assert.Equal(403, (await Enroll("PC1")).Status);
        var c = await _enroll.CreateCode("", TimeSpan.FromHours(1), null, null, "admin");
        Assert.True((await Enroll("PC1", c.Code)).Ok);
    }

    // ---------------------------------------------------------------- check-in

    private (AgentBinary Bin, SigningKeys Files, JobSigningKey Jobs) Keys()
    {
        var paths = new Paths();
        return (new AgentBinary(paths), new SigningKeys(paths, NullLogger<SigningKeys>.Instance), new JobSigningKey(Directory.CreateTempSubdirectory().FullName));
    }

    private async Task<(Machine M, int TokenId)> MachineWithJob()
    {
        var o = await Enroll("PC1");
        _db.Jobs.Add(new Job { MachineId = o.Machine!.Id, Type = JobTypes.Script, Title = "kør", ParamsJson = Json.Ser(new JobParams { Body = "echo hej" }) });
        await _db.SaveChangesAsync();
        return (o.Machine, o.Machine.Id);
    }

    [Fact]
    public async Task OldAgentStillGetsJobsInSignOnlyMode()
    {
        var (m, id) = await MachineWithJob();
        var (bin, files, jobs) = Keys();
        var r = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m, new CheckinRequest(), id, "http://x");
        Assert.Null(r.Signed);
        Assert.Equal("echo hej", r.Jobs.Single().Params.Body);
        Assert.NotNull(r.Share);
    }

    [Fact]
    public async Task NewAgentGetsSignedJobsThatVerify()
    {
        var (m, id) = await MachineWithJob();
        var (bin, files, jobs) = Keys();
        var nonce = CommandSigning.NewNonce();
        var r = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m, new CheckinRequest { Nonce = nonce, Trust = new AgentTrustReport() }, id, "http://x");
        Assert.NotNull(r.Signed);
        var v = CommandSigning.Verify(jobs.PublicPem, r.Signed, id, nonce, DateTime.UtcNow);
        Assert.True(v.Ok, v.Why);
        Assert.Equal("echo hej", v.Commands!.Jobs.Single().Params.Body);
    }

    [Fact]
    public async Task OffModeSignsNothing()
    {
        await _s.Set("signed_jobs", SignedJobsMode.Off);
        var (m, id) = await MachineWithJob();
        var (bin, files, jobs) = Keys();
        var r = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m, new CheckinRequest { Nonce = CommandSigning.NewNonce() }, id, "http://x");
        Assert.Null(r.Signed);
        Assert.Single(r.Jobs);
    }

    [Fact]
    public async Task EnforceWithholdsJobsFromAgentsWithoutPinnedKey()
    {
        await _s.Set("signed_jobs", SignedJobsMode.Enforce);
        var (m, id) = await MachineWithJob();
        var (bin, files, jobs) = Keys();
        var old = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m, new CheckinRequest(), id, "http://x");
        Assert.Empty(old.Jobs);
        var pinned = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m,
            new CheckinRequest { Nonce = CommandSigning.NewNonce(), Trust = new AgentTrustReport { JobKeyPinned = true } }, id, "http://x");
        Assert.Single(pinned.Jobs);
    }

    [Fact]
    public async Task PendingMachineGetsNoJobsOrCredentials()
    {
        await Enroll("PC1");
        var p = await Enroll("PC1");
        _db.Jobs.Add(new Job { MachineId = p.Machine!.Id, Type = JobTypes.Script, Title = "kør" });
        await _db.SaveChangesAsync();
        var (bin, files, jobs) = Keys();
        var r = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), p.Machine, new CheckinRequest { Nonce = CommandSigning.NewNonce() }, p.Machine.Id, "http://x");
        Assert.Empty(r.Jobs);
        Assert.Null(r.Share);
        Assert.Empty(r.Drives);
    }

    [Fact]
    public async Task PinnedAgentIsMovedToHttpsOnlyWhenConfigured()
    {
        var (m, id) = await MachineWithJob();
        await _s.Set("server_url", "http://fleetdeck.lan:8585");
        await _s.Set("agent_https_url", "https://fleetdeck.lan:8586");
        var (bin, files, jobs) = Keys();
        var unpinned = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m, new CheckinRequest { Nonce = CommandSigning.NewNonce(), Trust = new() }, id, "http://x");
        Assert.Equal("http://fleetdeck.lan:8585", Json.Des<SignedCommands>(unpinned.Signed!.Payload).ServerUrl);
        var pinned = await AgentCheckin.Build(_db, _s, bin, files, jobs, new TlsInfo(), m,
            new CheckinRequest { Nonce = CommandSigning.NewNonce(), Trust = new() { JobKeyPinned = true } }, id, "http://x");
        Assert.Equal("https://fleetdeck.lan:8586", JsonSerializer.Deserialize<SignedCommands>(pinned.Signed!.Payload, CommandSigning.Json)!.ServerUrl);
        Assert.Equal("http://fleetdeck.lan:8585", pinned.ServerUrl);   // ældre agenter (usigneret felt) bliver på http
    }

    [Fact]
    public void TrustReportIsRecordedPerMachine()
    {
        var m = new Machine();
        AgentTrust.Record(m, new CheckinRequest(), false, false, "abc", DateTime.UtcNow);
        Assert.Null(m.JobTrust);
        Assert.Equal("http", m.Transport);
        AgentTrust.Record(m, new CheckinRequest { Trust = new() { JobKeyPinned = true, JobKeyId = "abc", TlsPinned = true } }, true, false, "abc", DateTime.UtcNow);
        Assert.Equal(AgentTrust.Pinned, m.JobTrust);
        Assert.True(m.TlsPinned);
        Assert.Equal("https", m.Transport);
        AgentTrust.Record(m, new CheckinRequest { Trust = new() { JobKeyPinned = true, JobKeyId = "xyz", Error = "afvist" } }, false, true, "abc", DateTime.UtcNow);
        Assert.Equal(AgentTrust.Mismatch, m.JobTrust);
        Assert.Equal("afvist", m.TrustError);
        Assert.Equal("socket", m.Transport);
    }

    [Fact]
    public void JobSigningKeySurvivesRestart()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        using var a = new JobSigningKey(dir);
        using var b = new JobSigningKey(dir);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Directory.Delete(dir, true);
    }
}
