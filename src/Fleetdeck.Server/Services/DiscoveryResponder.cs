using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Svarer på "Find automatisk" fra agentens opsætningsvindue (UDP 8586 på det lokale net).
/// Svaret er serverens adresse: indstillingen "Serverens adresse", ellers den IP, spørgsmålet kom ind på.
/// Slås fra med DISCOVERY=0.
/// </summary>
public sealed class DiscoveryResponder(IServiceScopeFactory scopes, ILogger<DiscoveryResponder> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (Environment.GetEnvironmentVariable("DISCOVERY") == "0") return;
        var port = Environment.GetEnvironmentVariable("PORT") ?? "8585";
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Discovery.Port));
        }
        catch (SocketException e) { log.LogWarning("Find automatisk er slået fra: UDP {Port} kan ikke bruges ({Msg})", Discovery.Port, e.Message); return; }
        log.LogInformation("Find automatisk: lytter på UDP {Port}", Discovery.Port);
        using (udp)
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(stop); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { continue; }
                if (Encoding.UTF8.GetString(r.Buffer).Trim() != Discovery.Request) continue;
                try
                {
                    string url; bool access;
                    using (var scope = scopes.CreateScope())
                    {
                        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
                        url = (await settings.Get("server_url")).TrimEnd('/');
                        // "Anmod om adgang": spørgsmålet kom fra det lokale net, så "kun hjemmenet" tæller også
                        access = AccessMode.Normalize(await settings.Get(AccessRequestService.ModeSetting)) != AccessMode.Off;
                    }
                    if (url.Length == 0) url = $"http://{LocalAddressFor(r.RemoteEndPoint.Address)}:{port}";
                    var reply = JsonSerializer.SerializeToUtf8Bytes(new Discovery.Found("Fleetdeck", url, ThisVersion.Value, access),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    await udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                }
                catch (Exception e) when (e is SocketException or InvalidOperationException) { log.LogDebug("Find automatisk: {Msg}", e.Message); }
            }
        }
    }

    /// <summary>Den lokale IP, der bruges til at nå klienten (der sendes ingen pakker).</summary>
    private static IPAddress LocalAddressFor(IPAddress remote)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Connect(remote, 9);
        return ((IPEndPoint)s.LocalEndPoint!).Address;
    }
}
