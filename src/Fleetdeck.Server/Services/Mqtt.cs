using System.Net.Sockets;
using System.Text;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Minimal MQTT 3.1.1-klient (QoS 0): forbind med login og "sidste vilje", udgiv (evt. "retained"), abonnér og modtag.
/// Nok til Home Assistant's MQTT-discovery uden en ekstra pakke.
/// </summary>
public sealed class MqttConnection : IAsyncDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private NetworkStream _s = null!;
    private readonly SemaphoreSlim _write = new(1, 1);
    private ushort _packetId;

    public static async Task<MqttConnection> Connect(string host, int port, string clientId, string? user, string? pass,
        string willTopic, string willPayload, CancellationToken ct)
    {
        var c = new MqttConnection();
        await c._tcp.ConnectAsync(host, port, ct);
        c._s = c._tcp.GetStream();
        var flags = (byte)(0x02 | 0x04 | 0x20);               // ren session, sidste vilje (retained)
        if (!string.IsNullOrEmpty(user)) flags |= 0x80;
        if (!string.IsNullOrEmpty(pass)) flags |= 0x40;
        var body = new List<byte>();
        body.AddRange(Str("MQTT")); body.Add(4); body.Add(flags); body.Add(0); body.Add(60);   // keepalive 60 s
        body.AddRange(Str(clientId)); body.AddRange(Str(willTopic)); body.AddRange(Str(willPayload));
        if (!string.IsNullOrEmpty(user)) body.AddRange(Str(user));
        if (!string.IsNullOrEmpty(pass)) body.AddRange(Str(pass));
        await c.Send(0x10, body, ct);
        var (type, payload) = await c.Read(ct);
        if (type != 0x20 || payload.Length < 2) throw new IOException("Uventet svar fra MQTT-brokeren");
        if (payload[1] != 0)
            throw new IOException(payload[1] switch { 4 => "forkert brugernavn eller adgangskode", 5 => "ikke tilladt", _ => $"afvist (kode {payload[1]})" });
        return c;
    }

    public Task Publish(string topic, string payload, bool retain, CancellationToken ct)
    {
        var body = new List<byte>(); body.AddRange(Str(topic)); body.AddRange(Encoding.UTF8.GetBytes(payload));
        return Send((byte)(0x30 | (retain ? 1 : 0)), body, ct);
    }

    public Task Subscribe(string filter, CancellationToken ct)
    {
        var id = ++_packetId == 0 ? ++_packetId : _packetId;
        var body = new List<byte> { (byte)(id >> 8), (byte)id }; body.AddRange(Str(filter)); body.Add(0);
        return Send(0x82, body, ct);
    }

    public Task Ping(CancellationToken ct) => Send(0xC0, [], ct);

    /// <summary>Næste besked på et abonneret emne (andre pakker – svar på ping/abonnement – springes over).</summary>
    public async Task<(string Topic, string Payload)> Receive(CancellationToken ct)
    {
        while (true)
        {
            var (type, p) = await Read(ct);
            if ((type & 0xF0) != 0x30) continue;
            var qos = (type >> 1) & 3;
            var tl = (p[0] << 8) | p[1];
            var topic = Encoding.UTF8.GetString(p, 2, tl);
            var start = 2 + tl + (qos > 0 ? 2 : 0);
            if (qos == 1) await Send(0x40, [p[2 + tl], p[3 + tl]], ct);   // PUBACK
            return (topic, Encoding.UTF8.GetString(p, start, p.Length - start));
        }
    }

    private static byte[] Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return [(byte)(b.Length >> 8), (byte)b.Length, .. b];
    }

    private async Task Send(byte header, List<byte> body, CancellationToken ct)
    {
        var packet = new List<byte> { header };
        var len = body.Count;
        do { var d = (byte)(len % 128); len /= 128; if (len > 0) d |= 0x80; packet.Add(d); } while (len > 0);
        packet.AddRange(body);
        await _write.WaitAsync(ct);
        try { await _s.WriteAsync(packet.ToArray(), ct); await _s.FlushAsync(ct); }
        finally { _write.Release(); }
    }

    private async Task<(byte Type, byte[] Payload)> Read(CancellationToken ct)
    {
        var h = await ReadExact(1, ct);
        int len = 0, mul = 1;
        while (true)
        {
            var d = (await ReadExact(1, ct))[0];
            len += (d & 127) * mul; mul *= 128;
            if ((d & 128) == 0) break;
            if (mul > 128 * 128 * 128) throw new IOException("Ugyldig MQTT-pakke");
        }
        return (h[0], len == 0 ? [] : await ReadExact(len, ct));
    }

    private async Task<byte[]> ReadExact(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        var got = 0;
        while (got < n)
        {
            var r = await _s.ReadAsync(buf.AsMemory(got), ct);
            if (r == 0) throw new IOException("MQTT-forbindelsen blev lukket");
            got += r;
        }
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        try { if (_tcp.Connected) await _s.WriteAsync(new byte[] { 0xE0, 0x00 }); } catch (IOException) { } catch (ObjectDisposedException) { }
        _tcp.Dispose();
        _write.Dispose();
    }
}
