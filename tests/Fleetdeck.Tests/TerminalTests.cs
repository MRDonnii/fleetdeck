using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

public class TerminalPolicyTests
{
    private static TerminalTicket Good() => new() { SessionId = new string('a', 32), Secret = new string('b', 64) };

    [Fact] public void Ticket_must_be_well_formed()
    {
        Assert.True(TerminalPolicy.TicketValid(Good()));
        var t = Good(); t.SessionId = "../../etc"; Assert.False(TerminalPolicy.TicketValid(t));
        t = Good(); t.Secret = "kort"; Assert.False(TerminalPolicy.TicketValid(t));
        t = Good(); t.Cols = 9000; Assert.False(TerminalPolicy.TicketValid(t));
    }

    [Theory]
    [InlineData("https://fleet.example", true)]
    [InlineData("unix:/data/fleetdeck.sock", true)]
    [InlineData("http://fleet.example", false)]
    [InlineData("", false)]
    public void Shell_never_goes_over_plain_http(string server, bool ok) => Assert.Equal(ok, TerminalPolicy.TransportOk(server));

    [Fact] public void Agent_uri_follows_the_connection()
    {
        Assert.Equal("wss://fleet.example:8443/api/agent/terminal/abc", TerminalPolicy.AgentUri("https://fleet.example:8443/", "abc").ToString());
        Assert.Equal("ws://localhost/api/agent/terminal/abc", TerminalPolicy.AgentUri("unix:/data/fleetdeck.sock", "abc").ToString());
    }

    [Fact] public void Resize_frames_round_trip_and_reject_nonsense()
    {
        Assert.True(TerminalFrame.TryResize(TerminalFrame.MakeResize(132, 43), out var c, out var r));
        Assert.Equal((132, 43), (c, r));
        Assert.False(TerminalFrame.TryResize(TerminalFrame.MakeResize(1, 1), out _, out _));
        Assert.False(TerminalFrame.TryResize(new byte[] { TerminalFrame.Resize, 1 }, out _, out _));
    }

    [Theory]
    [InlineData("https://fleet.example:8443", "fleet.example:8443", true)]
    [InlineData("https://fleet.example", "fleet.example", true)]
    [InlineData("https://evil.example", "fleet.example", false)]
    [InlineData("http://fleet.example", "fleet.example", true)]
    [InlineData("http://evil.example", "fleet.example", false)]
    [InlineData("ftp://fleet.example", "fleet.example", false)]
    [InlineData("", "fleet.example", false)]
    public void Origin_must_be_our_own(string origin, string host, bool ok) => Assert.Equal(ok, TerminalSecurity.OriginAllowed(origin, host));

    [Theory]
    [InlineData("disk:/", "warning", "warning", "13 GB fri", "12 GB fri", false)]
    [InlineData("disk:/", "warning", "critical", "5 GB fri", "1 GB fri", true)]
    [InlineData("crash", "warning", "warning", "1 de sidste 7 dage (senest a.crash)", "2 de sidste 7 dage (senest b.crash)", true)]
    [InlineData("crash", "warning", "warning", "2 de sidste 7 dage (senest a.crash)", "1 de sidste 7 dage (senest a.crash)", false)]
    [InlineData("winupdate", "critical", "warning", "3", "2", false)]
    public void Acknowledged_alert_returns_only_when_worse_or_a_new_crash(string key, string oldSev, string newSev, string oldDetail, string newDetail, bool reopens) =>
        Assert.Equal(reopens, AlertAck.Reopens(key, oldSev, newSev, oldDetail, newDetail));

    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData("١٢٣٤", false)]
    [InlineData(null, false)]
    public void Pin_is_four_to_eight_digits(string? pin, bool ok) => Assert.Equal(ok, TerminalPin.Valid(pin));

    [Theory]
    [InlineData("192.168.1.25", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.9", true)]
    [InlineData("100.64.0.7", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("fd10::518", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Only_home_and_vpn_addresses_count_as_private(string ip, bool ok) => Assert.Equal(ok, TerminalSecurity.IsPrivate(System.Net.IPAddress.Parse(ip)));

    [Theory]
    [InlineData("198.51.100.1", true)]
    [InlineData("router.lan", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("localhost", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("foo bar", false)]
    public void Server_never_targets_itself(string address, bool ok) => Assert.Equal(ok, TerminalHostRules.AddressProblem(address) is null);
}

public class TerminalGateTests
{
    private static User U() => new() { Id = 7, Username = "a", SecurityStamp = "s1" };

    [Fact] public void Unlock_expires_to_reauth_after_15_idle_minutes_and_can_be_renewed()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch); var gate = new TerminalGate(clock); var u = U();
        var t = gate.Issue(u);
        Assert.Equal(UnlockState.Active, gate.State(t, u));
        clock.Now += TimeSpan.FromMinutes(16);
        Assert.Equal(UnlockState.NeedsReauth, gate.State(t, u));
        gate.Touch(t);      // brug vækker ikke en udløbet oplåsning
        Assert.Equal(UnlockState.NeedsReauth, gate.State(t, u));
        Assert.True(gate.Renew(t, u));
        Assert.Equal(UnlockState.Active, gate.State(t, u));
    }

    [Fact] public void Unlock_ends_with_the_absolute_limit_a_new_stamp_or_another_user()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch); var gate = new TerminalGate(clock); var u = U();
        var t = gate.Issue(u);
        Assert.Equal(UnlockState.None, gate.State(t, new User { Id = 8, SecurityStamp = "s1" }));
        Assert.Equal(UnlockState.None, gate.State(t, new User { Id = 7, SecurityStamp = "s2" }));
        Assert.Equal(UnlockState.None, gate.State("forkert", u));
        clock.Now += TimeSpan.FromHours(13);
        Assert.Equal(UnlockState.None, gate.State(t, u));
        Assert.False(gate.Renew(t, u));
    }

    [Fact] public void Eligibility_requires_pinned_key_and_encrypted_transport()
    {
        var m = new Machine { TerminalCapable = true, JobTrust = AgentTrust.Pinned, Transport = "https" };
        Assert.Null(TerminalEligibility.Why(m, SignedJobsMode.Sign));
        Assert.NotNull(TerminalEligibility.Why(m, SignedJobsMode.Off));
        m.Transport = "http"; Assert.NotNull(TerminalEligibility.Why(m, SignedJobsMode.Sign));
        m.Transport = "socket"; Assert.Null(TerminalEligibility.Why(m, SignedJobsMode.Sign));
        m.JobTrust = AgentTrust.Unpinned; Assert.NotNull(TerminalEligibility.Why(m, SignedJobsMode.Sign));
        m.JobTrust = AgentTrust.Pinned; m.TerminalCapable = false; Assert.NotNull(TerminalEligibility.Why(m, SignedJobsMode.Sign));
    }
}

public class TerminalSignedChannelTests
{
    private sealed class Store : ITrustStore { public string? JobKey { get; set; } public string? TlsPin { get; set; } }
    private static TerminalTicket Ticket() => new() { SessionId = new string('a', 32), Secret = new string('b', 64) };

    private static (ECDsa Key, string Pem) NewKey() { var k = ECDsa.Create(ECCurve.NamedCurves.nistP256); return (k, k.ExportSubjectPublicKeyInfoPem()); }
    private static CommandEnvelope Signed(ECDsa k, string pem, string nonce, int machine = 5) => CommandSigning.Sign(k, pem, new SignedCommands
    {
        MachineId = machine, Nonce = nonce, Issued = DateTime.UtcNow, Expires = DateTime.UtcNow.AddMinutes(5), Terminals = [Ticket()],
    });

    [Fact] public void Tickets_only_come_from_a_response_signed_with_the_pinned_key()
    {
        var (k, pem) = NewKey(); var nonce = CommandSigning.NewNonce();
        var ok = CommandGate.Apply(new CheckinResponse { Signed = Signed(k, pem, nonce) }, new Store { JobKey = pem }, 5, nonce, DateTime.UtcNow);
        Assert.Single(ok.Terminals);
        var (other, otherPem) = NewKey();
        var forged = CommandGate.Apply(new CheckinResponse { Signed = Signed(other, otherPem, nonce) }, new Store { JobKey = pem }, 5, nonce, DateTime.UtcNow);
        Assert.Empty(forged.Terminals);
        var replay = CommandGate.Apply(new CheckinResponse { Signed = Signed(k, pem, CommandSigning.NewNonce()) }, new Store { JobKey = pem }, 5, nonce, DateTime.UtcNow);
        Assert.Empty(replay.Terminals);
    }

    [Fact] public void First_contact_never_opens_a_terminal()
    {
        var (k, pem) = NewKey(); var nonce = CommandSigning.NewNonce();
        var first = CommandGate.Apply(new CheckinResponse { Signed = Signed(k, pem, nonce) }, new Store(), 5, nonce, DateTime.UtcNow);
        Assert.True(first.Pinned); Assert.Empty(first.Terminals);
    }
}

public class UnixPtyTests
{
    /// <summary>Den byggede agent bruges som hjælper. Testen starter kun en lokal skal, der skriver sin størrelse og stopper.</summary>
    private static string? Helper()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Fleetdeck.slnx"))) root = Path.GetDirectoryName(root);
        var bin = root is null ? null : Path.Combine(root, "src/Fleetdeck.Agent.Unix/bin");
        if (bin is null || !Directory.Exists(bin)) return null;
        // bin kan også rumme færdigbyggede agenter til Mac og ARM, som ikke kan startes her
        var rid = $"/linux-{(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64")}/";
        return Directory.EnumerateFiles(bin, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) is "fleetdeck-agent" or "Fleetdeck.Agent.Unix" && !f.Contains("/publish/") && f.Contains(rid))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();   // den nyeste: en gammel Release-agent kender ikke pty-hjælperen
    }

    [Fact] public void Shell_runs_in_a_real_pty_with_size_and_exit_code()
    {
        if (!OperatingSystem.IsLinux()) return;
        var helper = Helper();
        Assert.True(helper is not null, "Byg hele løsningen først: agentens hjælper mangler");
        using var pty = UnixPty.Start(101, 33, helper!);
        pty.Write(Encoding.UTF8.GetBytes("stty size; exit 7\n"));
        var sb = new StringBuilder(); var buf = new byte[4096]; var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30)) { var n = pty.Read(buf); if (n <= 0) break; sb.Append(Encoding.UTF8.GetString(buf, 0, n)); }
        Assert.Contains("33 101", sb.ToString());
        Assert.Equal(7, pty.WaitForExit());
    }
}

public class TerminalVaultTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fd-vault").FullName;
    private TerminalVault New()
    {
        Environment.SetEnvironmentVariable("DATA_DIR", _dir);
        return new TerminalVault(new Paths(), Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dir, "keys"))));
    }
    public void Dispose() { Environment.SetEnvironmentVariable("DATA_DIR", null); Directory.Delete(_dir, true); }

    [Fact] public void Secret_round_trips_and_is_not_readable_in_the_sealed_text()
    {
        var v = New();
        var sealedText = v.Seal(new VaultSecret { Password = "hemmelig-eksempelkode" });
        Assert.DoesNotContain("hemmelig", sealedText);
        Assert.DoesNotContain("hemmelig", Encoding.UTF8.GetString(Convert.FromBase64String(sealedText[3..])));
        Assert.Equal("hemmelig-eksempelkode", New().Open(sealedText).Password);      // ny instans: samme hovednøgle fra filen
    }

    [Fact] public void Master_key_lives_outside_the_keys_folder_that_self_update_copies()
    {
        New().Seal(new VaultSecret { Password = "x" });
        Assert.True(File.Exists(Path.Combine(_dir, TerminalVault.Dir, "vault.key")));
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "keys")) ? Directory.GetFiles(Path.Combine(_dir, "keys"), "vault*") : []);
    }

    [Fact] public void Tampering_is_detected()
    {
        var v = New(); var s = v.Seal(new VaultSecret { Password = "x" });
        var b = Convert.FromBase64String(s[3..]); b[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => v.Open("v1." + Convert.ToBase64String(b)));
    }
}

public class SshKeyTests
{
    [Fact] public void Generated_key_can_be_read_back_and_matches_its_public_half()
    {
        var k = SshKeys.Generate("Netværk nøgle");
        Assert.StartsWith("ssh-ed25519 ", k.PublicKey);
        Assert.Matches("^[A-Za-z0-9+/= ._@-]+$", k.PublicKey);      // kan trygt stå i en kommando på værten
        var seen = SshKeys.Inspect(k.PrivateKey, null, "Netværk nøgle");
        Assert.NotNull(seen);
        Assert.Equal(k.Fingerprint, seen!.Fingerprint);
    }

    [Fact] public void Garbage_is_rejected() => Assert.Null(SshKeys.Inspect("ikke en nøgle", null, "x"));

    [Fact] public void Host_key_policy_pins_first_and_stops_on_change()
    {
        Assert.Equal(HostKeyDecision.PinFirstUse, HostKeyPolicy.Decide(null, "a"));
        Assert.Equal(HostKeyDecision.Match, HostKeyPolicy.Decide("a", "a"));
        Assert.Equal(HostKeyDecision.Changed, HostKeyPolicy.Decide("a", "b"));
    }
}

public class SnippetTests
{
    [Fact] public void Variables_are_found_but_docker_format_is_left_alone()
    {
        Assert.Equal(["tjeneste"], SnippetTemplate.Variables("systemctl restart {{tjeneste}}"));
        Assert.Empty(SnippetTemplate.Variables("docker ps --format '{{.Names}}'"));
        Assert.Equal("docker ps --format '{{.Names}}'", SnippetTemplate.Render("docker ps --format '{{.Names}}'", null).Text);
    }

    [Fact] public void Render_requires_values_and_refuses_a_new_command_line()
    {
        Assert.Equal("journalctl -n 20", SnippetTemplate.Render("journalctl -n {{antal}}", new Dictionary<string, string> { ["antal"] = "20" }).Text);
        Assert.NotNull(SnippetTemplate.Render("x {{a}}", new Dictionary<string, string>()).Problem);
        Assert.NotNull(SnippetTemplate.Render("x {{a}}", new Dictionary<string, string> { ["a"] = "1\nreboot" }).Problem);
    }

    [Theory]
    [InlineData("reboot", true)]
    [InlineData("sudo shutdown -r now", true)]
    [InlineData("systemctl restart nginx", true)]
    [InlineData("rm -rf /srv/x", true)]
    [InlineData("Restart-Computer -Force", true)]
    [InlineData("dd if=/dev/zero of=/dev/sda", true)]
    [InlineData("df -h", false)]
    [InlineData("systemctl status nginx", false)]
    [InlineData("journalctl -u reboot-guard", false)]
    public void Dangerous_commands_are_detected(string command, bool risky) => Assert.Equal(risky, SnippetRisk.Detect(command));

    [Fact] public void Protected_machine_requires_its_name_for_risky_commands()
    {
        Assert.True(SnippetGuard.Allowed(false, true, "PI", false, null));                // ufarlig kommando
        Assert.False(SnippetGuard.Allowed(true, false, "PI", false, null));               // risikabel kræver bekræftelse
        Assert.True(SnippetGuard.Allowed(true, false, "PI", true, null));
        Assert.False(SnippetGuard.Allowed(true, true, "PI", true, null));                 // beskyttet: navnet mangler
        Assert.False(SnippetGuard.Allowed(true, true, "PI", true, "en anden"));
        Assert.True(SnippetGuard.Allowed(true, true, "PI", true, " pi "));
    }

    [Fact] public void Snippet_only_runs_on_its_platform()
    {
        Assert.True(SnippetGuard.PlatformFits("", "windows"));
        Assert.True(SnippetGuard.PlatformFits("linux", null));
        Assert.False(SnippetGuard.PlatformFits("windows", "linux"));
    }
}

/// <summary>Hele vejen gennem serveren: browser ↔ mægler ↔ agentens forbindelse ↔ en falsk skal. Ingen rigtig skal eller netværk ud af maskinen.</summary>
public class TerminalBridgeTests
{
    private sealed class FakePty : IPtySession
    {
        private readonly System.Threading.Channels.Channel<byte[]> _out = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        public volatile string Size = ""; public int Exit = 5;
        public int Read(byte[] buffer)
        {
            try { var b = _out.Reader.ReadAsync().AsTask().GetAwaiter().GetResult(); b.CopyTo(buffer, 0); return b.Length; }
            catch (System.Threading.Channels.ChannelClosedException) { return 0; }
        }
        public void Write(ReadOnlySpan<byte> data)
        {
            var text = Encoding.UTF8.GetString(data);
            _out.Writer.TryWrite(Encoding.UTF8.GetBytes(text.ToUpperInvariant()));
            if (text.Contains("exit")) _out.Writer.TryComplete();
        }
        public void Resize(int cols, int rows) => Size = $"{cols}x{rows}";
        public int WaitForExit() => Exit;
        public void Dispose() => _out.Writer.TryComplete();
    }

    private static async Task<(System.Net.WebSockets.WebSocket A, System.Net.WebSockets.WebSocket B)> Pair()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start();
        var c = new System.Net.Sockets.TcpClient();
        await c.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)l.LocalEndpoint).Port);
        var s = await l.AcceptTcpClientAsync();
        return (System.Net.WebSockets.WebSocket.CreateFromStream(c.GetStream(), false, null, TimeSpan.Zero), System.Net.WebSockets.WebSocket.CreateFromStream(s.GetStream(), true, null, TimeSpan.Zero));
    }

    private static async Task<byte[]> Next(System.Net.WebSockets.WebSocket ws, byte type)
    {
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            if (r.Count > 0 && buf[0] == type) return buf[1..r.Count];
        }
    }

    [Fact] public async Task Keystrokes_output_resize_and_exit_cross_the_bridge()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"fd-term-{Guid.NewGuid():N}.db");
        var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<AuditService>(sc);
        Microsoft.Extensions.DependencyInjection.EntityFrameworkServiceCollectionExtensions.AddDbContext<AppDb>(sc, o => Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions.UseSqlite(o, $"Data Source={dbFile}"));
        await using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(sc);
        using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(sp))
            await Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AppDb>(scope.ServiceProvider).Database.EnsureCreatedAsync();
        var scopes = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(sp);
        var broker = new TerminalBroker(scopes, new TerminalGate(TimeProvider.System), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<TerminalBroker>.Instance);
        var s = new TerminalSession { Id = new string('a', 32), UserId = 1, UserName = "admin", Stamp = "s", Label = "pc", Kind = "agent", MachineId = 3, UnlockToken = "u", TicketHash = "x", Created = broker.Now, LastInput = broker.Now };
        broker.Add(s);
        var (browser, serverSide) = await Pair(); var (agentServer, agentClient) = await Pair();
        var pty = new FakePty();
        var agent = TerminalAgent.Pump(agentClient, pty, CancellationToken.None);
        s.AgentSocket.TrySetResult(agentServer);
        var run = broker.Run(s, serverSide, async ct => { var b = new AgentTerminalBackend(s); await b.Attach(ct); return b; });

        await browser.SendAsync(TerminalFrame.MakeResize(90, 30), System.Net.WebSockets.WebSocketMessageType.Binary, true, default);
        await browser.SendAsync(TerminalFrame.MakeData("hej"u8), System.Net.WebSockets.WebSocketMessageType.Binary, true, default);
        Assert.Equal("HEJ", Encoding.UTF8.GetString(await Next(browser, TerminalFrame.Data)));
        Assert.Equal("90x30", pty.Size);
        await browser.SendAsync(TerminalFrame.MakeData("exit"u8), System.Net.WebSockets.WebSocketMessageType.Binary, true, default);
        Assert.Equal(5, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(await Next(browser, TerminalFrame.Exit)));
        await run.WaitAsync(TimeSpan.FromSeconds(10)); s.AgentDone.TrySetResult(); agentServer.Abort();
        await agent.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(broker.Find(s.Id));      // sessionen er ryddet op
        using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(sp))
        {
            var actions = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AppDb>(scope.ServiceProvider).Audit.Select(a => a.Action).ToList();
            Assert.Contains("terminal.connect", actions); Assert.Contains("terminal.disconnect", actions);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(dbFile);
    }
}
