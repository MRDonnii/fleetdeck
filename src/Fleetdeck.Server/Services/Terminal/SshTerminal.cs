using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using Renci.SshNet.Common;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

public enum HostKeyDecision { PinFirstUse, Match, Changed }

public static class HostKeyPolicy
{
    public static HostKeyDecision Decide(string? pinned, string seen) =>
        pinned is null ? HostKeyDecision.PinFirstUse : pinned == seen ? HostKeyDecision.Match : HostKeyDecision.Changed;
}

/// <summary>Direkte SSH fra serveren. Login hentes i nøgleboksen lige før brug og når aldrig browseren.</summary>
public sealed class SshTerminal : ITerminalBackend
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private SshTerminal(SshClient client, ShellStream shell) { _client = client; _shell = shell; }

    /// <summary>Forbinder, tjekker værtsnøglen og logger ind. Fejl beskrives med faste tekster, så intet følsomt slipper ud.</summary>
    private static async Task<SshClient> Connect(IServiceScopeFactory scopes, TerminalVault vault, int hostId, string user, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        var h = await db.TerminalHosts.FirstOrDefaultAsync(x => x.Id == hostId, ct) ?? throw new TerminalConnectException("Værten findes ikke længere.");
        if (h.PendingKeyFingerprint is not null) throw new TerminalConnectException("Værtsnøglen er ændret. En administrator skal godkende den nye nøgle.", "værtsnøglen venter på godkendelse");
        var c = h.CredentialId is { } cid ? await db.TerminalCredentials.FirstOrDefaultAsync(x => x.Id == cid, ct) : null;
        if (c is null) throw new TerminalConnectException("Vælg et login fra nøgleboksen til denne vært.", "intet login valgt");

        var secret = vault.Open(c.Cipher);
        AuthenticationMethod auth;
        PrivateKeyFile? keyFile = null;
        if (c.Kind == "password") auth = new PasswordAuthenticationMethod(h.Username, secret.Password ?? "");
        else
        {
            var ms = new MemoryStream(Encoding.UTF8.GetBytes(secret.PrivateKey ?? ""));
            keyFile = string.IsNullOrEmpty(secret.Passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, secret.Passphrase);
            auth = new PrivateKeyAuthenticationMethod(h.Username, keyFile);
        }
        var info = new Renci.SshNet.ConnectionInfo(h.Address, h.Port, h.Username, auth) { Timeout = TimeSpan.FromSeconds(15) };
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        (string Type, string Fingerprint)? seen = null; var decision = HostKeyDecision.Match;
        client.HostKeyReceived += (_, e) =>
        {
            var fp = "SHA256:" + e.FingerPrintSHA256.TrimEnd('=');
            seen = (e.HostKeyName, fp);
            decision = HostKeyPolicy.Decide(h.HostKeyFingerprint, fp);
            e.CanTrust = decision != HostKeyDecision.Changed;      // hårdt stop, før der sendes noget login
        };
        try { await client.ConnectAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            client.Dispose(); keyFile?.Dispose();
            if (decision == HostKeyDecision.Changed && seen is { } s)
            {
                (h.PendingKeyType, h.PendingKeyFingerprint) = (s.Type, s.Fingerprint);
                await db.SaveChangesAsync(CancellationToken.None);
                await audit.Log(user, "terminal.hostkey_changed", $"{h.Name}: {h.HostKeyFingerprint} → {s.Fingerprint}");
                throw new TerminalConnectException("Værtsnøglen er ændret! Forbindelsen er stoppet, til en administrator har godkendt den nye nøgle.", "værtsnøglen er ændret");
            }
            throw e switch
            {
                SshAuthenticationException => new TerminalConnectException("Værten afviste login.", "login afvist"),
                SocketException or SshOperationTimeoutException or TimeoutException => new TerminalConnectException("Værten svarer ikke.", "værten svarer ikke"),
                _ => new TerminalConnectException("Forbindelsen kunne ikke oprettes.", e.GetType().Name),
            };
        }
        if (decision == HostKeyDecision.PinFirstUse && seen is { } first)
        {
            (h.HostKeyType, h.HostKeyFingerprint) = first;
            await audit.Log(user, "terminal.hostkey_pinned", $"{h.Name}: {first.Fingerprint}");
        }
        h.LastConnected = c.LastUsed = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        return client;
    }

    public static async Task<ITerminalBackend> Open(IServiceScopeFactory scopes, TerminalVault vault, TerminalSession s, CancellationToken ct)
    {
        var client = await Connect(scopes, vault, s.HostId!.Value, s.UserName, ct);
        try { return new SshTerminal(client, client.CreateShellStream("xterm-256color", (uint)s.Cols, (uint)s.Rows, 0, 0, 64 * 1024)); }
        catch (Exception e) when (e is SshException or SocketException or InvalidOperationException) { client.Dispose(); throw new TerminalConnectException("Værten ville ikke åbne en skal.", e.GetType().Name); }
    }

    /// <summary>Lægger en offentlig nøgle i authorized_keys. Linjen kommer fra <see cref="SshKeys"/> og indeholder kun ufarlige tegn.</summary>
    public static async Task InstallKey(IServiceScopeFactory scopes, TerminalVault vault, int hostId, string publicKey, CancellationToken ct)
    {
        if (publicKey.Contains('\'') || publicKey.Contains('\n')) throw new TerminalConnectException("Nøglen er ugyldig.");
        using var client = await Connect(scopes, vault, hostId, "system", ct);
        using var cmd = client.CreateCommand($"umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && (grep -qxF '{publicKey}' ~/.ssh/authorized_keys || echo '{publicKey}' >> ~/.ssh/authorized_keys)");
        cmd.CommandTimeout = TimeSpan.FromSeconds(20);
        try { cmd.Execute(); } catch (SshException e) { throw new TerminalConnectException("Nøglen kunne ikke lægges på værten.", e.GetType().Name); }
        if (cmd.ExitStatus != 0) throw new TerminalConnectException("Nøglen kunne ikke lægges på værten.", $"slutkode {cmd.ExitStatus}");
    }

    public Task Write(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        _shell.Write(data.Span); _shell.Flush();
        return Task.CompletedTask;
    }

    public Task Resize(int cols, int rows, CancellationToken ct) { _shell.ChangeWindowSize((uint)cols, (uint)rows, 0, 0); return Task.CompletedTask; }

    public async Task<int?> Pump(Func<ReadOnlyMemory<byte>, Task> output, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var n = await _shell.ReadAsync(buf, ct);
            if (n <= 0) break;
            await output(buf.AsMemory(0, n));
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _shell.Dispose();
        try { if (_client.IsConnected) _client.Disconnect(); } catch (Exception e) when (e is SshException or SocketException or ObjectDisposedException) { }
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
