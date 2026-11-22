using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleetdeck.Tests;

/// <summary>"Anmod om adgang": anmodning, kode, godkendelse, afvisning, udløb, grænser og at intet kan overtages eller lække.</summary>
public sealed class AccessRequestTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;
    private readonly SettingsService _s;
    private readonly EnrollmentService _enroll;
    private readonly AccessRequestService _svc;
    private readonly AccessRequestMemory _mem = new();
    private const string Ip = "192.168.1.50";
    /// <summary>http hele vejen: intet fingeraftryk, ikke verificeret.</summary>
    private static readonly (string, bool) NoPin = ("", false);
    /// <summary>Agentens side af hver anmodning: privat og offentlig nøgle og serverens nonce.</summary>
    private readonly Dictionary<int, (string Private, string Public, string Nonce)> _agent = [];

    public AccessRequestTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _s = new SettingsService(_db);
        _s.Set("enroll_key", "global-key-123456789012").GetAwaiter().GetResult();
        var audit = new AuditService(_db);
        _enroll = new EnrollmentService(_db, _s, audit);
        _svc = new AccessRequestService(_db, _s, audit, _enroll, _mem);
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    /// <summary>Begge skridt (forpligtelse, så hemmelighed og nøgle), som agenten gør det.</summary>
    private async Task<AccessRequestService.CreateResult> Create(string name, string ip = Ip, bool home = true, string? secret = null, DateTime? at = null,
        AccessReplace? replaces = null, (string, bool)? pin = null)
    {
        secret ??= AccessCode.NewSecret();
        var (pub, priv) = AccessSeal.NewKeyPair();
        var st = await _svc.Start(new AccessRequestCreate(AccessCode.Commit(secret, pub), name, Platforms.Windows, new SystemFacts { Os = "Windows 11", Model = "OptiPlex" },
            "anna", "Ny bærbar", "", replaces), ip, home, pin ?? NoPin, at);
        if (st.Started is not { } started) return new(st.Status, st.Error);
        var o = await _svc.Reveal(new AccessReveal(started.Handle, secret, pub), ip, at);
        if (o.Row is { } row && o.Fresh) _agent[row.Id] = (priv, pub, started.Nonce);
        return o;
    }

    private async Task<(AccessRequest Row, string Secret)> Ask(string name = "STUE-PC", string ip = Ip, bool home = true, string? secret = null, DateTime? at = null,
        AccessReplace? replaces = null)
    {
        secret ??= AccessCode.NewSecret();
        var o = await Create(name, ip, home, secret, at, replaces);
        Assert.True(o.Row is not null, o.Error);
        return (o.Row!, secret);
    }

    /// <summary>Spørger og åbner den forseglede engangskode med agentens private nøgle.</summary>
    private async Task<string> Key(int id, string secret, string ip = Ip, DateTime? at = null)
    {
        var r = await _svc.Poll(id, secret, ip, at);
        Assert.Equal(AccessStates.Issued, r!.State);
        return AccessSeal.Open(_agent[id].Private, r.SealedKey!, AccessSeal.Context(id));
    }

    private async Task<int> Profile(string name = "Børn")
    {
        var p = new Profile { Name = name };
        _db.Profiles.Add(p);
        await _db.SaveChangesAsync();
        return p.Id;
    }

    private Task<EnrollmentService.Outcome> Enroll(string key, string? secret, string name = "STUE-PC", string? previous = null, int? profile = null) =>
        _enroll.Enroll(new EnrollRequest(key, name, false, null, ProfileId: profile, Platform: Platforms.Windows, PreviousToken: previous, AccessSecret: secret));

    [Fact]
    public async Task Hele_flowet_anmod_godkend_tilmeld()
    {
        var pid = await Profile();
        var (a, secret) = await Ask();
        var agent = _agent[a.Id];
        Assert.Equal(AccessCode.Display(secret, agent.Nonce, "", agent.Public), a.Code);
        Assert.Matches("^[2-9A-HJKMNP-Z]{3}-[2-9A-HJKMNP-Z]{3}$", a.Code);

        var before = await _svc.Poll(a.Id, secret, Ip);
        Assert.Equal(AccessStates.Pending, before!.State);
        Assert.Null(before.SealedKey);

        Assert.Equal(200, (await _svc.Approve(a.Id, pid, null, "admin")).Status);
        var got = await _svc.Poll(a.Id, secret, Ip);
        Assert.Equal(AccessStates.Issued, got!.State);
        Assert.DoesNotContain(EnrollmentService.CodePrefix, got.SealedKey);   // kun krypteret
        var key = AccessSeal.Open(agent.Private, got.SealedKey!, AccessSeal.Context(a.Id));
        Assert.StartsWith(EnrollmentService.CodePrefix, key);

        // agenten prøver at vælge en anden profil selv: administratorens valg gælder
        var other = await Profile("Andet");
        var o = await Enroll(key, secret, profile: other);
        Assert.True(o.Ok, o.Error);
        Assert.Equal("STUE-PC", o.Machine!.Name);
        Assert.Equal(pid, o.Machine.ProfileId);
        Assert.Equal(EnrollmentService.Methods.Code, o.Method);
        var row = await _db.AccessRequests.AsNoTracking().SingleAsync();
        Assert.Equal(AccessStates.Enrolled, row.State);
        Assert.Equal(o.Machine.Id, row.MachineId);
        // maskinen har tjekket ind: anmodningen er afsluttet
        await _db.Machines.Where(m => m.Id == o.Machine.Id).ExecuteUpdateAsync(x => x.SetProperty(m => m.LastSeen, DateTime.UtcNow.AddMinutes(1)));
        Assert.Equal(AccessStates.Enrolled, (await _svc.Poll(a.Id, secret, Ip))!.State);
    }

    [Fact]
    public async Task Godkendt_navn_bruges_og_ingen_profil_er_et_valg()
    {
        var (a, secret) = await Ask("desktop-x1");
        Assert.Equal("DESKTOP-X1", a.Name);
        Assert.Equal(400, (await _svc.Approve(a.Id, null, null, "admin")).Status);   // profil skal vælges
        Assert.Equal(400, (await _svc.Approve(a.Id, 0, "ugyldigt navn!", "admin")).Status);
        Assert.Equal(200, (await _svc.Approve(a.Id, 0, "KONTOR-2", "admin")).Status);
        var got = await _svc.Poll(a.Id, secret, Ip);
        Assert.Equal("KONTOR-2", got!.Name);
        var o = await Enroll(await Key(a.Id, secret), secret, name: "DESKTOP-X1");
        Assert.Equal("KONTOR-2", o.Machine!.Name);
        Assert.Null(o.Machine.ProfileId);
    }

    [Fact]
    public async Task Forkert_hemmelighed_eller_id_giver_samme_svar_og_logges_højst_én_gang_i_timen_pr_adresse()
    {
        var (a, secret) = await Ask();
        var now = DateTime.UtcNow;
        Assert.Null(await _svc.Poll(a.Id, "forkert-hemmelighed-forkert-hemmelighed", Ip, now));
        Assert.Null(await _svc.Poll(a.Id + 100, secret, Ip, now));
        Assert.Null(await _svc.Poll(a.Id, null, Ip, now));
        Assert.Equal(1, await _db.Audit.CountAsync(x => x.Action == "access.bad_secret"));
        Assert.Null(await _svc.Poll(a.Id + 100, secret, "192.168.1.51", now));
        Assert.Null(await _svc.Poll(a.Id + 100, secret, Ip, now.AddMinutes(61)));
        Assert.Equal(3, await _db.Audit.CountAsync(x => x.Action == "access.bad_secret"));
        // IPv6 tælles pr. /64
        Assert.Null(await _svc.Poll(a.Id + 100, secret, "2001:db8:1:2::1", now));
        Assert.Null(await _svc.Poll(a.Id + 100, secret, "2001:db8:1:2::ffff", now));
        Assert.Equal(4, await _db.Audit.CountAsync(x => x.Action == "access.bad_secret"));
    }

    [Fact]
    public async Task Bundet_kode_virker_kun_med_hemmeligheden_og_kun_én_gang()
    {
        var (a, secret) = await Ask();
        await _svc.Approve(a.Id, 0, null, "admin");
        var key = await Key(a.Id, secret);
        // uden hemmelighed virker den hverken til profiler/installer eller tilmelding
        Assert.Equal(EnrollmentService.KeyKind.Invalid, (await _enroll.CheckKey(key)).Kind);
        Assert.Equal(403, (await Enroll(key, null)).Status);
        Assert.Equal(403, (await Enroll(key, AccessCode.NewSecret())).Status);
        Assert.True((await Enroll(key, secret)).Ok);
        Assert.Equal(403, (await Enroll(key, secret, name: "ANDEN-PC")).Status);
        Assert.Equal(1, await _db.Machines.CountAsync());
    }

    [Fact]
    public async Task Samme_forseglede_kode_til_den_er_brugt_og_kun_agentens_nøgle_åbner_den()
    {
        var (a, secret) = await Ask();
        await _svc.Approve(a.Id, 0, null, "admin");
        // en anden, der spørger med (fx har lyttet med over http), kan hverken åbne koden eller ødelægge den for maskinen
        var first = (await _svc.Poll(a.Id, secret, Ip))!.SealedKey!;
        var second = (await _svc.Poll(a.Id, secret, "192.168.1.99"))!.SealedKey!;
        Assert.Equal(first, second);
        var (_, stranger) = AccessSeal.NewKeyPair();
        Assert.ThrowsAny<CryptographicException>(() => AccessSeal.Open(stranger, first, AccessSeal.Context(a.Id)));
        Assert.True((await Enroll(AccessSeal.Open(_agent[a.Id].Private, first, AccessSeal.Context(a.Id)), secret)).Ok);
        Assert.Equal(1, await _db.EnrollCodes.CountAsync(c => c.AccessRequestId == a.Id));
    }

    [Fact]
    public void Forseglingen_afslører_ændringer_og_forkert_kontekst()
    {
        var (pub, priv) = AccessSeal.NewKeyPair();
        Assert.True(AccessSeal.ValidPublicKey(pub));
        Assert.False(AccessSeal.ValidPublicKey("ikke-en-nøgle"));
        var sealedKey = AccessSeal.Seal(pub, "fdc_hemmelig", AccessSeal.Context(7));
        Assert.Equal("fdc_hemmelig", AccessSeal.Open(priv, sealedKey, AccessSeal.Context(7)));
        Assert.ThrowsAny<CryptographicException>(() => AccessSeal.Open(priv, sealedKey, AccessSeal.Context(8)));
        var parts = sealedKey.Split('.');
        var flipped = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        Assert.ThrowsAny<CryptographicException>(() => AccessSeal.Open(priv, $"{parts[0]}.{flipped}.{parts[2]}", AccessSeal.Context(7)));
    }

    [Fact]
    public async Task Forpligtelsen_skal_passe_bruges_én_gang_fra_samme_adresse_og_inden_for_et_minut()
    {
        var now = DateTime.UtcNow;
        var secret = AccessCode.NewSecret();
        var (pub, _) = AccessSeal.NewKeyPair();
        AccessRequestCreate Req() => new(AccessCode.Commit(secret, pub), "PC", null, null, null, null, null);
        // en anden hemmelighed eller nøgle end den, der blev forpligtet til, afvises (mellemmanden kan ikke vælge efter at have set nonce)
        var st = (await _svc.Start(Req(), Ip, true, NoPin, now)).Started!;
        Assert.Equal(400, (await _svc.Reveal(new AccessReveal(st.Handle, AccessCode.NewSecret(), pub), Ip, now)).Status);
        Assert.Equal(404, (await _svc.Reveal(new AccessReveal(st.Handle, secret, pub), Ip, now)).Status);   // håndtaget er brugt
        st = (await _svc.Start(Req(), Ip, true, NoPin, now)).Started!;
        Assert.Equal(404, (await _svc.Reveal(new AccessReveal(st.Handle, secret, pub), "192.168.1.77", now)).Status);
        st = (await _svc.Start(Req(), Ip, true, NoPin, now)).Started!;
        Assert.Equal(410, (await _svc.Reveal(new AccessReveal(st.Handle, secret, pub), Ip, now.AddSeconds(61))).Status);
        st = (await _svc.Start(Req(), Ip, true, NoPin, now)).Started!;
        Assert.Equal(200, (await _svc.Reveal(new AccessReveal(st.Handle, secret, pub), Ip, now.AddSeconds(5))).Status);
        Assert.Equal(400, (await _svc.Start(Req() with { Commit = "ikke-hex" }, Ip, true, NoPin, now)).Status);
        Assert.Equal(0, await _db.AccessRequests.CountAsync(a => a.State != AccessStates.Pending));
        Assert.Equal(1, await _db.AccessRequests.CountAsync());
    }

    [Fact]
    public void Koden_afhænger_af_serverens_nonce_og_agentens_nøgle()
    {
        var secret = AccessCode.NewSecret();
        var (pub, _) = AccessSeal.NewKeyPair();
        var (other, _) = AccessSeal.NewKeyPair();
        var n = AccessCode.NewNonce();
        Assert.Equal(AccessCode.Display(secret, n, "", pub), AccessCode.Display(secret, n, "", pub));
        Assert.NotEqual(AccessCode.Display(secret, n, "", pub), AccessCode.Display(secret, AccessCode.NewNonce(), "", pub));
        Assert.NotEqual(AccessCode.Display(secret, n, "", pub), AccessCode.Display(secret, n, "", other));
        Assert.Equal(64, AccessCode.Commit(secret, pub).Length);
        Assert.NotEqual(AccessCode.Commit(secret, pub), AccessCode.Commit(secret, other));
    }

    [Fact]
    public async Task Eksisterende_maskine_overtages_aldrig()
    {
        // forberedt maskine uden token og en maskine under geninstallation: begge ville ellers kunne overtages
        var prepared = new Machine { Name = "STUE-PC", Platform = Platforms.Windows };
        var deploying = new Machine { Name = "KONTOR", Platform = Platforms.Windows, TokenHash = Crypto.Sha256("x") };
        _db.Machines.AddRange(prepared, deploying);
        await _db.SaveChangesAsync();
        _db.Deployments.Add(new Deployment { Mac = "AA-BB-CC-DD-EE-FF", MachineId = deploying.Id, Status = DeployStatus.Installing });
        await _db.SaveChangesAsync();

        foreach (var name in new[] { "STUE-PC", "KONTOR" })
        {
            var (a, secret) = await Ask(name, ip: "192.168.1." + name.Length);
            var list = await _svc.List();
            Assert.Contains("Navnet findes allerede", list.Single(x => x.Id == a.Id).Conflict);
            await _svc.Approve(a.Id, 0, null, "admin");
            var key = await Key(a.Id, secret, "192.168.1." + name.Length);
            var o = await Enroll(key, secret, name);
            Assert.True(o.Pending);
            Assert.Equal($"{name}~2", o.Machine!.Name);
            Assert.NotEqual(o.Machine.Id, o.Existing!.Id);
        }
        Assert.Null((await _db.Machines.AsNoTracking().SingleAsync(m => m.Id == prepared.Id)).TokenHash);
        Assert.Equal(Crypto.Sha256("x"), (await _db.Machines.AsNoTracking().SingleAsync(m => m.Id == deploying.Id)).TokenHash);
    }

    [Fact]
    public async Task Maskinens_eget_gamle_token_må_overtage()
    {
        var first = await _enroll.Enroll(new EnrollRequest("global-key-123456789012", "STUE-PC", false, null, Platform: Platforms.Windows));
        var (a, secret) = await Ask();
        await _svc.Approve(a.Id, 0, null, "admin");
        var key = await Key(a.Id, secret);
        var o = await Enroll(key, secret, previous: $"{first.Machine!.Id}.{first.Secret}");
        Assert.False(o.Pending);
        Assert.Equal(first.Machine.Id, o.Machine!.Id);
        Assert.Equal(EnrollmentService.Methods.Token, o.Method);
    }

    [Fact]
    public async Task Grænser_pr_ip_og_i_alt()
    {
        await Ask("A1"); await Ask("A2");
        Assert.Equal(429, (await Create("A3")).Status);
        await _s.Set(AccessRequestService.MaxSetting, "3");
        await Ask("B1", ip: "192.168.1.2");
        Assert.Equal(429, (await Create("C1", "192.168.1.3")).Status);
    }

    [Fact]
    public async Task Lofterne_tjekkes_før_den_gamle_erstattes()
    {
        var (a, _) = await Ask("A1");
        await Ask("B1", ip: "192.168.1.2");
        await Ask("C1", ip: "192.168.1.3");
        // administratoren sænker loftet: en ny anmodning fra A1 ville stadig være over loftet – den gamle må ikke gå tabt
        await _s.Set(AccessRequestService.MaxSetting, "2");
        Assert.Equal(429, (await Create("A1")).Status);
        Assert.Equal(AccessStates.Pending, (await _db.AccessRequests.AsNoTracking().SingleAsync(x => x.Id == a.Id)).State);
        await _s.Set(AccessRequestService.MaxSetting, "3");
        Assert.Equal(200, (await Create("A1")).Status);
        Assert.Equal(AccessStates.Superseded, (await _db.AccessRequests.AsNoTracking().SingleAsync(x => x.Id == a.Id)).State);
    }

    [Fact]
    public async Task Anmod_igen_med_nyt_navn_og_ny_adresse_lukker_maskinens_egen_anmodning()
    {
        var (a, secret) = await Ask("GAMMELT-NAVN");
        var (other, _) = await Ask("ANDEN", ip: "192.168.1.9");
        // forkert hemmelighed: den andens anmodning røres ikke
        await Ask("NYT-NAVN", ip: "192.168.1.5", replaces: new AccessReplace(other.Id, AccessCode.NewSecret()));
        Assert.Equal(AccessStates.Pending, (await _db.AccessRequests.AsNoTracking().SingleAsync(x => x.Id == other.Id)).State);
        var o = await Create("NYT-NAVN-2", "192.168.1.6", replaces: new AccessReplace(a.Id, secret));
        Assert.Equal(200, o.Status);
        Assert.Null(o.Notice);   // erstatter en, der allerede er meldt: ingen ny notifikation
        Assert.Equal(AccessStates.Superseded, (await _db.AccessRequests.AsNoTracking().SingleAsync(x => x.Id == a.Id)).State);
    }

    [Fact]
    public async Task Notifikationer_kun_for_nye_og_højst_ti_i_timen()
    {
        var now = DateTime.UtcNow;
        await _s.Set(AccessRequestService.MaxSetting, "100");
        var first = await Create("PC0", "10.1.0.0", at: now);
        Assert.Contains("Anmodning om adgang", first.Notice);
        Assert.Null((await Create("PC0", "10.1.0.0", at: now)).Notice);   // samme sted og navn igen: erstatter
        var notices = new List<string?>();
        for (var i = 1; i <= 11; i++) notices.Add((await Create("PC" + i, "10.1.0." + i, at: now)).Notice);
        Assert.Equal(9, notices.Count(n => n?.Contains("kode") == true));
        Assert.Contains("Mange anmodninger", notices[9]);
        Assert.Null(notices[10]);
        Assert.Contains("Anmodning om adgang", (await Create("PC99", "10.1.0.99", at: now.AddMinutes(61))).Notice);
    }

    [Fact]
    public async Task Tabt_svar_på_tilmeldingen_giver_en_ny_kode_til_samme_maskine()
    {
        var pid = await Profile();
        var (a, secret) = await Ask();
        await _svc.Approve(a.Id, pid, null, "admin");
        var first = await Enroll(await Key(a.Id, secret), secret);
        Assert.True(first.Ok);
        // svaret nåede aldrig agenten: næste forespørgsel giver en ny kode, der kun må tage netop den maskine
        var again = await Enroll(await Key(a.Id, secret), secret, name: "NOGET-ANDET");
        Assert.True(again.Ok, again.Error);
        Assert.True(again.Repeat);
        Assert.Equal(first.Machine!.Id, again.Machine!.Id);
        Assert.Equal(pid, again.Machine.ProfileId);
        Assert.Equal(1, await _db.Machines.CountAsync());
        Assert.Equal(Crypto.Sha256(again.Secret!), (await _db.Machines.AsNoTracking().SingleAsync()).TokenHash);
        Assert.Contains(await _db.Audit.Select(x => x.Action).ToListAsync(), x => x == "access.reissue");
        // har maskinen tjekket ind, er der ingen ny kode
        await _db.Machines.ExecuteUpdateAsync(x => x.SetProperty(m => m.LastSeen, DateTime.UtcNow.AddMinutes(5)));
        Assert.Equal(AccessStates.Enrolled, (await _svc.Poll(a.Id, secret, Ip))!.State);
    }

    [Fact]
    public async Task Tabt_svar_ved_navnekonflikt_giver_samme_ventende_maskine_og_profilen_bruges_ved_godkend_som_ny()
    {
        await _enroll.Enroll(new EnrollRequest("global-key-123456789012", "STUE-PC", false, null, Platform: Platforms.Windows));
        var pid = await Profile();
        var (a, secret) = await Ask();
        await _svc.Approve(a.Id, pid, null, "admin");
        var first = await Enroll(await Key(a.Id, secret), secret);
        Assert.True(first.Pending);
        Assert.Equal("STUE-PC~2", first.Machine!.Name);
        Assert.Null(first.Machine.ProfileId);
        Assert.Equal(pid, first.Machine.PendingProfileId);
        var again = await Enroll(await Key(a.Id, secret), secret);
        Assert.True(again.Ok && again.Pending && again.Repeat, again.Error);
        Assert.Equal(first.Machine.Id, again.Machine!.Id);
        Assert.Equal(2, await _db.Machines.CountAsync());
        var (m, err) = await _enroll.ApproveAsNew(first.Machine.Id, "admin");
        Assert.Null(err);
        Assert.Equal(pid, m!.ProfileId);
        Assert.Null(m.PendingProfileId);
    }

    [Fact]
    public async Task Ny_anmodning_fra_samme_sted_erstatter_den_gamle_og_samme_hemmelighed_er_idempotent()
    {
        var (a, secret) = await Ask();
        var again = await Create("STUE-PC", secret: secret);
        Assert.Equal(a.Id, again.Row!.Id);
        Assert.False(again.Fresh);
        var (b, _) = await Ask();
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(AccessStates.Superseded, (await _db.AccessRequests.AsNoTracking().SingleAsync(x => x.Id == a.Id)).State);
        Assert.Equal(AccessStates.Superseded, (await _svc.Poll(a.Id, secret, Ip))!.State);
    }

    [Fact]
    public async Task Tilstand_fra_lan_og_overalt()
    {
        // standard (ny installation): kun hjemmenet og VPN – også for forespørgsler
        Assert.Equal(403, (await Create("PC", "203.0.113.9", home: false)).Status);
        Assert.True(await _svc.Accepts(true));
        Assert.False(await _svc.Accepts(false));
        await _s.Set(AccessRequestService.ModeSetting, AccessMode.On);
        Assert.Equal(200, (await Create("PC", "203.0.113.9", home: false)).Status);
        Assert.True(await _svc.Accepts(false));
        await _s.Set(AccessRequestService.ModeSetting, AccessMode.Off);
        Assert.Equal(403, (await Create("PC2")).Status);
        Assert.False(await _svc.Accepts(true));
    }

    [Theory]
    [InlineData(false, null)]   // ny installation: standarden (kun hjemmenet og VPN)
    [InlineData(true, "off")]   // opgradering med maskiner: slået fra, til administratoren slår det til
    public async Task Opgraderinger_får_funktionen_slået_fra(bool withMachine, string? expected)
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        // skemaet fra før denne funktion: migrationen lige før AccessRequests
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_AccessRequests")) - 1]);
        if (withMachine)
        {
            // en maskine med kun de krævede kolonner udfyldt (skemaet fra før denne udgave)
            using var info = conn.CreateCommand();
            info.CommandText = "SELECT name, type FROM pragma_table_info('Machines') WHERE \"notnull\" = 1 AND dflt_value IS NULL AND pk = 0";
            var cols = new List<(string Name, string Type)>();
            using (var r = info.ExecuteReader()) while (r.Read()) cols.Add((r.GetString(0), r.GetString(1)));
            using var ins = conn.CreateCommand();
            ins.CommandText = $"INSERT INTO Machines ({string.Join(",", cols.Select(c => c.Name))}) VALUES ({string.Join(",", cols.Select(c => c.Type == "TEXT" ? "'x'" : "0"))})";
            ins.ExecuteNonQuery();
        }
        await migrator.MigrateAsync();
        Assert.Equal(expected, await db.Settings.Where(x => x.Key == AccessRequestService.ModeSetting).Select(x => x.Value).FirstOrDefaultAsync());
    }

    [Fact]
    public void Rate_limit_nøgle_ipv6_pr_64_og_socket()
    {
        Assert.Equal("192.168.1.5", AccessRequestService.RateKey(IPAddress.Parse("192.168.1.5")));
        Assert.Equal("192.168.1.5", AccessRequestService.RateKey(IPAddress.Parse("::ffff:192.168.1.5")));
        Assert.Equal(AccessRequestService.RateKey(IPAddress.Parse("2001:db8:1:2::1")), AccessRequestService.RateKey(IPAddress.Parse("2001:db8:1:2:abcd::9")));
        Assert.NotEqual(AccessRequestService.RateKey(IPAddress.Parse("2001:db8:1:2::1")), AccessRequestService.RateKey(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("socket", AccessRequestService.RateKey(null));
    }

    [Fact]
    public async Task Afvist_udefra_giver_outside_slået_fra_og_blokeret_giver_off()
    {
        static AccessRequestCreate Req() => new(AccessCode.Commit(AccessCode.NewSecret(), AccessSeal.NewKeyPair().PublicKey), "PC", null, null, null, null, null);
        // standard: kun hjemmenet – et kald udefra får grunden "outside" (ufarlig at afsløre: den står i indstillingen)
        var outside = await _svc.Start(Req(), "203.0.113.9", false, NoPin);
        Assert.Equal((403, AccessRefusal.Outside), (outside.Status, outside.Reason));
        Assert.Null((await _svc.Start(Req(), Ip, true, NoPin)).Reason);
        // blokeret: samme svar som slået fra – grunden "off", så den blokerede maskine ikke kan se forskel
        var (a, _) = await Ask();
        Assert.Equal(200, (await _svc.Block(a.Id, ip: true, mac: false, name: false, hours: 24, reason: null, by: "admin")).Status);
        var blocked = await _svc.Start(Req(), Ip, true, NoPin);
        Assert.Equal((403, AccessRefusal.Off), (blocked.Status, blocked.Reason));
        await _s.Set(AccessRequestService.ModeSetting, AccessMode.Off);
        var off = await _svc.Start(Req(), "203.0.113.9", false, NoPin);
        Assert.Equal((403, AccessRefusal.Off), (off.Status, off.Reason));
        Assert.Equal(blocked.Error, off.Error);
        Assert.Equal(outside.Error, off.Error);   // den menneskelige tekst er den samme som før
    }

    [Fact]
    public async Task Hjemmenet_afviser_offentlig_ip_og_cloudflare()
    {
        static HttpContext Ctx(string ip, string? header = null)
        {
            var c = new DefaultHttpContext();
            c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            if (header is not null) c.Request.Headers[header] = "1.2.3.4";
            return c;
        }
        Assert.True(await ApiKeyGuard.FromHomeNetwork(Ctx("192.168.1.20")));
        Assert.True(await ApiKeyGuard.FromHomeNetwork(Ctx("100.64.0.7")));   // VPN
        Assert.False(await ApiKeyGuard.FromHomeNetwork(Ctx("203.0.113.9")));
        Assert.False(await ApiKeyGuard.FromHomeNetwork(Ctx("127.0.0.1", "CF-Connecting-IP")));
    }

    [Fact]
    public async Task Udløb_lukker_anmodningen_og_sletter_koden()
    {
        var now = DateTime.UtcNow;
        var (a, secret) = await Ask(at: now);
        Assert.Equal(now.AddHours(24), a.Expires);
        await _svc.Approve(a.Id, 0, null, "admin", now);
        var key = await Key(a.Id, secret, Ip, now);
        Assert.Equal(1, await _svc.Tick(now.AddHours(25)));
        Assert.Equal(AccessStates.Expired, (await _db.AccessRequests.AsNoTracking().SingleAsync()).State);
        Assert.Equal(0, await _db.EnrollCodes.CountAsync());
        Assert.Equal(403, (await Enroll(key, secret)).Status);
        Assert.Equal(AccessStates.Expired, (await _svc.Poll(a.Id, secret, Ip, now.AddHours(26)))!.State);
        // gamle rækker ryddes efter 30 dage
        await _svc.Tick(now.AddDays(31));
        Assert.Equal(0, await _db.AccessRequests.CountAsync());
    }

    [Fact]
    public async Task Godkendelse_efter_udløb_afvises()
    {
        var now = DateTime.UtcNow;
        var (a, secret) = await Ask(at: now);
        Assert.Equal(AccessStates.Expired, (await _svc.Poll(a.Id, secret, Ip, now.AddHours(30)))!.State);
        Assert.Equal(400, (await _svc.Approve(a.Id, 0, null, "admin")).Status);
    }

    [Fact]
    public async Task Afvisning_med_begrundelse()
    {
        var (a, secret) = await Ask();
        await _svc.Approve(a.Id, 0, null, "admin");
        var key = await Key(a.Id, secret);
        Assert.Equal(200, (await _svc.Reject(a.Id, "Ukendt maskine", "admin")).Status);
        var r = await _svc.Poll(a.Id, secret, Ip);
        Assert.Equal(AccessStates.Rejected, r!.State);
        Assert.Equal("Ukendt maskine", r.Reason);
        Assert.Null(r.SealedKey);
        Assert.Equal(403, (await Enroll(key, secret)).Status);
        Assert.Equal(400, (await _svc.Approve(a.Id, 0, null, "admin")).Status);
    }

    [Fact]
    public async Task Send_nøgle_i_stedet_giver_en_almindelig_engangskode()
    {
        var pid = await Profile();
        var (a, secret) = await Ask();
        var d = await _svc.SendKey(a.Id, pid, 2, "admin");
        Assert.Equal(200, d.Status);
        Assert.Equal(AccessStates.KeySent, (await _svc.Poll(a.Id, secret, Ip))!.State);
        var o = await Enroll(d.Key!, null);
        Assert.True(o.Ok, o.Error);
        Assert.Equal(pid, o.Machine!.ProfileId);
        Assert.Contains("Anmodning", (await _db.EnrollCodes.SingleAsync()).Label);
    }

    [Fact]
    public async Task Intet_hemmeligt_i_revisionsloggen_eller_notifikationen()
    {
        var (a, secret) = await Ask();
        await _svc.Poll(a.Id, "forkert-hemmelighed-forkert-hemmelighed", Ip);
        await _svc.Approve(a.Id, 0, null, "admin");
        var key = await Key(a.Id, secret);
        await Enroll(key, null);
        await Enroll(key, secret);
        var log = string.Join("\n", await _db.Audit.Select(x => x.User + " " + x.Action + " " + x.Detail).ToListAsync());
        Assert.Contains("access.request", log);
        Assert.Contains("access.approve", log);
        Assert.Contains("access.enrolled", log);
        Assert.DoesNotContain(secret, log);
        Assert.DoesNotContain(key, log);
        Assert.DoesNotContain(key[..10], log);
        Assert.DoesNotContain(Crypto.Sha256(secret), log);
        var text = AccessRequestService.NotifyText(a);
        Assert.Contains(a.Code, text);
        Assert.DoesNotContain(secret, text);
        // kun hash i databasen
        Assert.DoesNotContain(await _db.AccessRequests.Select(x => x.SecretHash).ToListAsync(), h => h == secret);
        Assert.DoesNotContain(EnrollmentService.CodePrefix, await _db.AccessRequests.Select(x => x.SealedKey).SingleAsync());
    }

    [Fact]
    public void Koden_er_ens_på_begge_sider_og_afslører_en_mellemmand()
    {
        var secret = AccessCode.NewSecret();
        var (pub, _) = AccessSeal.NewKeyPair();
        var n = AccessCode.NewNonce();
        const string pin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        const string other = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";
        Assert.Equal(AccessCode.Display(secret, n, pin, pub), AccessCode.Display(secret, n, pin, pub));
        Assert.NotEqual(AccessCode.Display(secret, n, pin, pub), AccessCode.Display(secret, n, other, pub));
        Assert.NotEqual(AccessCode.Display(secret, n, "", pub), AccessCode.Display(AccessCode.NewSecret(), n, "", pub));
        Assert.DoesNotMatch("[01OIL]", string.Concat(Enumerable.Range(0, 200).Select(_ => AccessCode.Display(AccessCode.NewSecret(), n, pin, pub))));
    }

    [Fact]
    public async Task Http_er_ikke_verificeret_og_vises_som_ukrypteret()
    {
        var (a, _) = await Ask();   // NoPin: http hele vejen
        Assert.False(a.PinVerified);
        Assert.True((await _svc.List()).Single().Unencrypted);
        await Create("TLS-PC", "192.168.1.8", pin: ("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", true));
        Assert.False((await _svc.List()).Single(x => x.Name == "TLS-PC").Unencrypted);
    }

    [Fact]
    public void Tls_ved_anmodningen_godtages_og_låses_bagefter_kun_pin_eller_systemets_kontrol()
    {
        static X509Certificate2 Cert(string cn)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        }
        using var a = Cert("a"); using var b = Cert("b");
        const SslPolicyErrors untrusted = SslPolicyErrors.RemoteCertificateChainErrors;
        // første kontakt: selvsigneret godtages (koden bekræfter det), men resten af anmodningen skal være samme server
        var first = new AccessTlsObserver(null, firstContact: true);
        Assert.True(first.Validate(null, a, null, untrusted));
        Assert.False(first.SystemTrusted);
        Assert.False(first.Validate(null, b, null, untrusted));
        Assert.False(first.Validate(null, b, null, SslPolicyErrors.None));
        // bagefter uden fastlåst fingeraftryk (betroet certifikat): almindelig kontrol – et nyt, betroet certifikat er fint
        var later = new AccessTlsObserver(null);
        Assert.False(later.Validate(null, a, null, untrusted));
        Assert.True(later.Validate(null, b, null, SslPolicyErrors.None));
        // selvsigneret: kun det fastlåste
        var pinned = new AccessTlsObserver(TlsPinning.Of(a));
        Assert.True(pinned.Validate(null, a, null, untrusted));
        Assert.False(pinned.Validate(null, b, null, SslPolicyErrors.None));
    }

    [Fact]
    public void Serveren_bruger_sit_eget_fingeraftryk_når_den_kender_det()
    {
        const string own = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", mitm = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=", proxy = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC=";
        Assert.Equal((own, true), AccessRequestService.ChoosePin(true, own, null, mitm));       // direkte https: altid eget
        Assert.Equal(("", false), AccessRequestService.ChoosePin(false, own, null, null));      // http hele vejen: ikke verificeret
        Assert.Equal((proxy, true), AccessRequestService.ChoosePin(false, own, proxy, mitm));   // proxy med kendt fingeraftryk
        Assert.Equal((mitm, false), AccessRequestService.ChoosePin(false, own, null, mitm));    // proxy uden: ikke verificeret
    }

    [Fact]
    public void Visningstekster()
    {
        Assert.Equal("Venter på godkendelse – kode 7K4-Q2M", new TrayAccessRequest { Code = "7K4-Q2M" }.Text);
        Assert.Equal("Kan ikke nå serveren – prøver igen (kode 7K4-Q2M): timeout", new TrayAccessRequest { Code = "7K4-Q2M", Problem = "timeout" }.Text);
        Assert.Equal("Afvist: Nej tak", new TrayAccessRequest { State = AccessStates.Rejected, Reason = "Nej tak", Problem = "x" }.Text);
        Assert.DoesNotContain("Tilmeldt\"", new TrayAccessRequest { State = AccessStates.Enrolled }.Text + "\"");   // aldrig bare "Tilmeldt" uden nøgle
        Assert.Contains("anmod igen", new TrayAccessRequest { State = AccessStates.Enrolled }.Text);
        Assert.Equal(AccessMode.Lan, AccessMode.Normalize("noget"));
        Assert.Equal(AccessMode.Off, AccessMode.Normalize(" off "));
    }
}

/// <summary>Over rigtig HTTP: rate-limit, 404 uden opremsning, installer uden nøgle og at svar aldrig viser profiler.</summary>
public sealed class AccessRequestHttpTests : IAsyncLifetime
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _conn.Open();
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Services.AddDbContext<AppDb>(o => o.UseSqlite(_conn));
        b.Services.AddScoped<SettingsService>();
        b.Services.AddScoped<AuditService>();
        b.Services.AddScoped<EnrollmentService>();
        b.Services.AddScoped<AccessRequestService>();
        b.Services.AddSingleton<AccessRequestMemory>();
        b.Services.AddSingleton(new TlsInfo());
        b.Services.AddSignalR();
        b.Services.AddHttpClient("webhook");
        b.Services.AddSingleton<Live>();
        b.Services.AddSingleton<Notifier>();
        b.Services.AddAuthorization(o => o.AddPolicy("admin", p => p.RequireAssertion(_ => false)));
        b.Services.AddAuthentication();
        b.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; AccessRequestService.AddRateLimits(o); });
        _app = b.Build();
        using (var scope = _app.Services.CreateScope()) scope.ServiceProvider.GetRequiredService<AppDb>().Database.EnsureCreated();
        _app.UseRateLimiter();
        _app.MapAccessRequestEndpoints();
        await _app.StartAsync();
        var url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(url) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _conn.Dispose();
    }

    [Fact]
    public async Task Opret_spørg_og_rate_limit()
    {
        var secret = AccessCode.NewSecret();
        var p = await AccessRequestClient.Request(_http, null, _http.BaseAddress!.ToString(), "TEST-PC", Platforms.Linux, null, "bo", "hej");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            Assert.Equal(p.Code, (await db.AccessRequests.SingleAsync()).Code);   // samme kode på begge sider
            Assert.Matches("^" + AccessCode.Pattern + "$", p.Code);
        }
        var poll = await AccessRequestClient.Poll(_http, _http.BaseAddress!.ToString(), p.Id, p.Secret);
        Assert.Equal(AccessStates.Pending, poll.State);
        var raw = await (await _http.PostAsJsonAsync($"api/agent/access-requests/{p.Id}/poll", new AccessPollRequest(p.Secret))).Content.ReadAsStringAsync();
        Assert.DoesNotContain("profile", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sealedKey\":\"", raw);
        // godkendt: kun en krypteret kode over nettet, som agenten selv kan åbne
        using (var scope = _app.Services.CreateScope())
            Assert.Equal(200, (await scope.ServiceProvider.GetRequiredService<AccessRequestService>().Approve(p.Id, 0, null, "admin")).Status);
        var issued = await AccessRequestClient.Poll(_http, _http.BaseAddress!.ToString(), p.Id, p.Secret);
        Assert.Equal(AccessStates.Issued, issued.State);
        var key = AccessRequestClient.OpenKey(p, issued);
        Assert.StartsWith(EnrollmentService.CodePrefix, key);
        raw = await (await _http.PostAsJsonAsync($"api/agent/access-requests/{p.Id}/poll", new AccessPollRequest(p.Secret))).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key!, raw);
        // forkert id og forkert hemmelighed: samme 404
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync($"api/agent/access-requests/{p.Id}/poll", new AccessPollRequest(secret))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync("api/agent/access-requests/999/poll", new AccessPollRequest(p.Secret))).StatusCode);
        // administratorens liste kræver login
        Assert.NotEqual(HttpStatusCode.OK, (await _http.GetAsync("api/access-requests")).StatusCode);
        // 5 nye anmodninger i timen pr. IP
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            codes.Add((await _http.PostAsJsonAsync("api/agent/access-requests", new AccessRequestCreate(AccessCode.Commit(secret, "x"), "PC" + i, null, null, null, null, null))).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(4, codes.Count(c => c != HttpStatusCode.TooManyRequests));   // den første anmodning brugte én
        // slået fra: også forespørgsler afvises med det samme
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SettingsService>().Set(AccessRequestService.ModeSetting, AccessMode.Off);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.PostAsJsonAsync($"api/agent/access-requests/{p.Id}/poll", new AccessPollRequest(p.Secret))).StatusCode);
    }
}
