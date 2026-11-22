using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Tests;

/// <summary>"Anmod om adgang" → "Bloker…": IP (IPv6 pr. /64), MAC og navn med udløb, åbne anmodninger lukkes, og alt står i revisionsloggen.</summary>
public sealed class AccessBlockTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDb _db;
    private readonly SettingsService _s;
    private readonly AccessRequestService _svc;
    private const string Ip = "192.168.1.50", Other = "192.168.1.77";
    private static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    public AccessBlockTests()
    {
        _conn.Open();
        _db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _s = new SettingsService(_db);
        _s.Set(AccessRequestService.ModeSetting, AccessMode.On).GetAwaiter().GetResult();
        var audit = new AuditService(_db);
        _svc = new AccessRequestService(_db, _s, audit, new EnrollmentService(_db, _s, audit), new AccessRequestMemory());
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private static SystemFacts Facts(params string[] macs) =>
        new() { Os = "Windows 11", Model = "OptiPlex", Nics = [.. macs.Select(m => new NicInfo { Name = "Ethernet", Status = "Up", Mac = m })] };

    /// <summary>Begge skridt som agenten. Status 200 + rækken, eller fejlen fra det skridt, der stoppede.</summary>
    private async Task<(int Status, AccessRequest? Row)> Ask(string name = "STUE-PC", string ip = Ip, SystemFacts? facts = null, DateTime? at = null)
    {
        var secret = AccessCode.NewSecret();
        var (pub, _) = AccessSeal.NewKeyPair();
        var st = await _svc.Start(new AccessRequestCreate(AccessCode.Commit(secret, pub), name, Platforms.Windows, facts ?? Facts("aa:bb:cc:dd:ee:01"),
            "anna", null, "", null), ip, true, ("", false), at ?? T0);
        if (st.Started is not { } s) return (st.Status, null);
        var o = await _svc.Reveal(new AccessReveal(s.Handle, secret, pub), ip, at ?? T0);
        return (o.Status, o.Row);
    }

    [Fact]
    public async Task Blocked_ip_closes_its_open_requests_and_refuses_new_ones_until_it_expires()
    {
        var (_, a) = await Ask("STUE-PC");
        var (_, b) = await Ask("ARBEJDS-PC");
        var (_, other) = await Ask("LAGER-PC", Other, Facts("aa:bb:cc:dd:ee:02"));
        var r = await _svc.Block(a!.Id, ip: true, mac: false, name: false, hours: 24, reason: "Ukendt enhed", by: "admin", at: T0.AddMinutes(1));
        Assert.Equal(200, r.Status);
        Assert.Equal(2, r.Closed);   // begge fra samme adresse – ikke den fra en anden adresse
        _db.ChangeTracker.Clear();
        Assert.Equal(AccessStates.Rejected, (await _db.AccessRequests.FindAsync(a.Id))!.State);
        Assert.Equal("Ukendt enhed", (await _db.AccessRequests.FindAsync(b!.Id))!.RejectReason);
        Assert.Equal(AccessStates.Pending, (await _db.AccessRequests.FindAsync(other!.Id))!.State);

        // samme svar som "slået fra": enheden kan ikke se, at den er blokeret, og optager ingen plads
        var again = await Ask("NY-PC", at: T0.AddHours(2));
        Assert.Equal(403, again.Status);
        Assert.Null(again.Row);
        Assert.Equal(200, (await Ask("NY-PC", Other, Facts("aa:bb:cc:dd:ee:03"), T0.AddHours(2))).Status);
        var blk = await _db.AccessBlocks.SingleAsync();
        Assert.Equal((AccessBlockKinds.Ip, Ip, 1), (blk.Kind, blk.Value, blk.Hits));

        // udløbet: kan anmode igen, og oprydningen sletter blokeringen
        Assert.Equal(200, (await Ask("NY-PC", at: T0.AddHours(25))).Status);
        await _svc.Tick(T0.AddHours(25));
        Assert.Equal(0, await _db.AccessBlocks.CountAsync());

        Assert.Equal(1, await _db.Audit.CountAsync(x => x.Action == "access.block" && x.Detail.Contains("IP 192.168.1.50") && x.Detail.Contains("Ukendt enhed")));
        Assert.Equal(1, await _db.Audit.CountAsync(x => x.Action == "access.blocked"));
    }

    [Fact]
    public async Task Ipv6_is_blocked_per_64()
    {
        var (_, a) = await Ask("STUE-PC", "2001:db8:1:2::10");
        Assert.Equal(200, (await _svc.Block(a!.Id, true, false, false, 1, null, "admin", T0)).Status);
        Assert.Equal(403, (await Ask("NY-PC", "2001:db8:1:2::abcd")).Status);       // samme /64: bare en ny adresse
        Assert.Equal(200, (await Ask("NY-PC", "2001:db8:1:3::10", Facts("aa:bb:cc:dd:ee:05"))).Status);
        Assert.Equal("2001:db8:1:2::/64", (await _svc.Blocks(T0)).Single().Value);
    }

    [Fact]
    public async Task List_shows_what_an_ip_block_hits_and_warns_when_the_ip_is_shared()
    {
        await Ask("STUE-PC", "2001:db8:1:2::10");
        Assert.Equal("2001:db8:1:2::/64", (await _svc.List(T0)).Single().IpKey);   // dialogen viser det, serveren blokerer
        Assert.Null((await _svc.List(T0)).Single().IpShared);
        // to forskellige enheder fra samme IP: sandsynligvis en proxy eller fælles NAT – advar, så IP ikke er forvalgt
        await Ask("STUE-PC", Ip);
        await Ask("ARBEJDS-PC", Ip, Facts("aa:bb:cc:dd:ee:09"));
        Assert.All((await _svc.List(T0)).Where(r => r.Ip == Ip), r => Assert.Contains("1 anden anmodning", r.IpShared));
        // en betroet proxy (standard: docker-broen 172.16.0.0/12): alle bag den har samme IP
        await Ask("TV-PC", "172.17.0.1", Facts("aa:bb:cc:dd:ee:0a"));
        Assert.Contains("betroet proxy", (await _svc.List(T0)).Single(r => r.Name == "TV-PC").IpShared);
    }

    [Fact]
    public async Task Mac_and_name_follow_the_machine_to_another_address()
    {
        var (_, a) = await Ask("STUE-PC", facts: Facts("AA-BB-CC-DD-EE-10", "aabb.ccdd.ee11"));
        var r = await _svc.Block(a!.Id, ip: false, mac: true, name: true, hours: 48, reason: null, by: "admin", at: T0);
        Assert.Equal(200, r.Status);
        Assert.Equal(3, r.Blocks!.Count);
        // ny adresse, samme MAC
        Assert.Equal(403, (await Ask("ANDET-NAVN", Other, Facts("aa:bb:cc:dd:ee:11"))).Status);
        // ny adresse og MAC, samme navn
        Assert.Equal(403, (await Ask("stue-pc", Other, Facts("aa:bb:cc:dd:ee:99"))).Status);
        // IP'en selv er ikke blokeret
        Assert.Equal(200, (await Ask("TREDJE", Ip, Facts("aa:bb:cc:dd:ee:98"))).Status);
        var rows = await _svc.Blocks(T0);
        Assert.Contains(rows, b => b.Label == "MAC AA:BB:CC:DD:EE:10" && b.RequestName == "STUE-PC");
        Assert.Contains(rows, b => b.Label == "navn STUE-PC");
        // listen viser MAC-adresserne, så administratoren kan se, hvad der blokeres
        Assert.Contains("AA:BB:CC:DD:EE:11", (await _svc.List(T0)).Single(x => x.Id == a.Id).Macs);
    }

    [Fact]
    public async Task Block_needs_a_choice_an_expiry_and_something_to_block()
    {
        var (_, a) = await Ask("STUE-PC", facts: new SystemFacts { Os = "Windows 11" });
        Assert.Equal(400, (await _svc.Block(a!.Id, false, false, false, 24, null, "admin", T0)).Status);
        Assert.Equal(400, (await _svc.Block(a.Id, true, false, false, null, null, "admin", T0)).Status);
        Assert.Equal(400, (await _svc.Block(a.Id, true, false, false, 0, null, "admin", T0)).Status);
        Assert.Equal(400, (await _svc.Block(a.Id, false, true, false, 24, null, "admin", T0)).Status);   // ingen MAC i anmodningen
        Assert.Equal(404, (await _svc.Block(999, true, false, false, 24, null, "admin", T0)).Status);
        // højst 90 dage ad gangen – en blokering skal altid udløbe
        Assert.Equal(200, (await _svc.Block(a.Id, true, false, false, 1e9, null, "admin", T0)).Status);
        Assert.Equal(T0.AddHours(AccessBlockKinds.MaxHours), (await _db.AccessBlocks.SingleAsync()).Until);
        Assert.Equal(0, await _db.Audit.CountAsync(x => x.Action == "access.blocked"));
    }

    [Fact]
    public async Task Requests_from_the_servers_own_host_cannot_be_ip_blocked()
    {
        var (_, a) = await Ask("STUE-PC", "socket");
        Assert.Equal(400, (await _svc.Block(a!.Id, true, false, false, 24, null, "admin", T0)).Status);
        var (_, b) = await Ask("ARBEJDS-PC", "127.0.0.1", Facts("aa:bb:cc:dd:ee:20"));
        Assert.Equal(400, (await _svc.Block(b!.Id, true, false, false, 24, null, "admin", T0)).Status);
        Assert.Equal(200, (await _svc.Block(b.Id, false, false, true, 24, null, "admin", T0)).Status);   // navnet kan godt
    }

    [Fact]
    public async Task Blocking_again_extends_instead_of_duplicating_and_unblock_is_logged()
    {
        var (_, a) = await Ask("STUE-PC");
        await _svc.Block(a!.Id, true, false, false, 24, null, "admin", T0);
        await _svc.Block(a.Id, true, false, false, 72, "Igen", "admin", T0.AddHours(1));
        await _svc.Block(a.Id, true, false, false, 1, null, "admin", T0.AddHours(2));   // kortere: afkorter ikke
        var b = await _db.AccessBlocks.SingleAsync();
        Assert.Equal(T0.AddHours(73), b.Until);
        Assert.Equal("Igen", b.Reason);
        Assert.True(await _svc.Unblock(b.Id, "admin"));
        Assert.False(await _svc.Unblock(b.Id, "admin"));
        Assert.Equal(200, (await Ask("NY-PC")).Status);
        Assert.Equal(1, await _db.Audit.CountAsync(x => x.Action == "access.unblock" && x.Detail.Contains("IP 192.168.1.50")));
    }

    [Fact]
    public async Task Refused_attempts_are_logged_at_most_once_an_hour_per_address()
    {
        var (_, a) = await Ask("STUE-PC");
        await _svc.Block(a!.Id, true, false, false, 24, null, "admin", T0);
        for (var i = 0; i < 5; i++) Assert.Equal(403, (await Ask($"PC-{i}", at: T0.AddMinutes(i))).Status);
        Assert.Equal(1, await _db.Audit.CountAsync(x => x.Action == "access.blocked"));
        Assert.Equal(5, (await _db.AccessBlocks.SingleAsync()).Hits);
        await Ask("PC-X", at: T0.AddHours(2));
        Assert.Equal(2, await _db.Audit.CountAsync(x => x.Action == "access.blocked"));
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AABBCCDDEEFF")]
    [InlineData("AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF")]
    [InlineData("aabb.ccdd.eeff", "AABBCCDDEEFF")]
    [InlineData("00:00:00:00:00:00", null)]
    [InlineData("ff:ff:ff:ff:ff:ff", null)]
    [InlineData("aa:bb:cc:dd:ee", null)]
    [InlineData("zz:bb:cc:dd:ee:ff", null)]
    [InlineData(null, null)]
    public void Mac_addresses_are_normalized(string? mac, string? expected)
    {
        Assert.Equal(expected, AccessBlockKinds.NormalizeMac(mac));
        if (expected is not null) Assert.Equal("AA:BB:CC:DD:EE:FF", AccessBlockKinds.ShowMac(expected));
    }

    /// <summary>Første skridt alene: status og grunden i 403-svaret.</summary>
    private async Task<AccessRequestService.StartResult> Start(string name = "STUE-PC", string ip = Ip, bool home = true, DateTime? at = null)
    {
        var (pub, _) = AccessSeal.NewKeyPair();
        return await _svc.Start(new AccessRequestCreate(AccessCode.Commit(AccessCode.NewSecret(), pub), name, Platforms.Mac, Facts("aa:bb:cc:dd:ee:01"),
            "anna", null, "", null), ip, home, ("", false), at ?? T0);
    }

    [Fact]
    public async Task Refusal_reason_is_off_when_switched_off_and_when_blocked()
    {
        // slået fra: grunden "off", så Mac'en/pc'en kan sige, at det er slået fra (og hvor det slås til)
        await _s.Set(AccessRequestService.ModeSetting, AccessMode.Off);
        var off = await Start();
        Assert.Equal((403, AccessRefusal.Off), (off.Status, off.Reason));
        Assert.Equal(AccessRefusal.OffText, AccessRefusal.Explain(off.Reason, "http://192.168.1.10:8585"));
        // kun hjemmenet, kaldet kom udefra: "outside" som i 3.44.1
        await _s.Set(AccessRequestService.ModeSetting, AccessMode.Lan);
        var outside = await Start(home: false);
        Assert.Equal((403, AccessRefusal.Outside), (outside.Status, outside.Reason));
        Assert.Equal(200, (await Start(home: true)).Status);
        // blokeret: præcis samme svar som slået fra – en blokeret maskine kan ikke se forskel
        await _s.Set(AccessRequestService.ModeSetting, AccessMode.On);
        var (_, a) = await Ask("STUE-PC");
        Assert.Equal(200, (await _svc.Block(a!.Id, ip: true, mac: false, name: false, hours: 24, reason: "x", by: "admin", at: T0.AddMinutes(1))).Status);
        var blocked = await Start("NY-PC", at: T0.AddHours(1));
        Assert.Equal((off.Status, off.Error, off.Reason), (blocked.Status, blocked.Error, blocked.Reason));
    }
}
