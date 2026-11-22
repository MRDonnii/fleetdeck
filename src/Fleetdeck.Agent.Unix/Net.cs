using System.Net.Sockets;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Forbindelsen til serveren: almindelig http(s) – eller "unix:/sti/fleetdeck.sock", når agenten kører på selve værten
/// (fx Unraid), som ikke kan nå containerens egen IP på et macvlan-net.
/// </summary>
public static class Net
{
    private static string? _socket;
    public static bool IsUnix(string server) => server.StartsWith("unix:", StringComparison.Ordinal);

    /// <summary>Adressen, der sættes foran stien; for en socket-fil er det http://localhost (og socket-filen huskes).</summary>
    public static string Base(string server)
    {
        if (!IsUnix(server)) { _socket = null; return server.TrimEnd('/'); }
        _socket = server["unix:".Length..];
        return "http://localhost";
    }

    public static SocketsHttpHandler Handler() => new()
    {
        // https med selvsigneret certifikat: det fastlåste fingeraftryk accepteres ud over certifikater, systemet stoler på
        SslOptions = { RemoteCertificateValidationCallback = Fleetdeck.Shared.TlsPinning.Validate },
        ConnectCallback = async (ctx, ct) =>
        {
            if (_socket is { } path)
            {
                var us = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await us.ConnectAsync(new UnixDomainSocketEndPoint(path), ct); return new NetworkStream(us, ownsSocket: true); }
                catch { us.Dispose(); throw; }
            }
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(ctx.DnsEndPoint, ct); return new NetworkStream(s, ownsSocket: true); }
            catch { s.Dispose(); throw; }
        },
    };
}
