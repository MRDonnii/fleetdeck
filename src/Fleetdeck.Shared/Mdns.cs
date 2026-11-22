using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Fleetdeck.Shared;

/// <summary>
/// Et lille mDNS/DNS-SD (Bonjour) til at finde printere: PTR-forespørgsel på printertjenesterne og læsning af svarene
/// (PTR, SRV, TXT, A). Forespørgslen sendes fra en tilfældig port, så printerne svarer direkte tilbage (RFC 6762, afsnit 6.7).
/// </summary>
public static class Mdns
{
    public static readonly string[] PrinterServices = ["_ipp._tcp.local", "_ipps._tcp.local", "_pdl-datastream._tcp.local", "_printer._tcp.local"];
    public const ushort A = 1, Ptr = 12, Txt = 16, Aaaa = 28, Srv = 33;

    public sealed record Record(string Name, ushort Type, string? Target = null, ushort Port = 0, Dictionary<string, string>? Txt = null, IPAddress? Address = null);

    public sealed record Service(string Instance, string Type, string Host, int Port, IReadOnlyDictionary<string, string> Txt, IReadOnlyList<IPAddress> Addresses)
    {
        /// <summary>Navnet uden tjenesten: "Canon TS8350._ipp._tcp.local" → "Canon TS8350".</summary>
        public string Label => Instance.EndsWith("." + Type, StringComparison.OrdinalIgnoreCase) ? Instance[..^(Type.Length + 1)] : Instance.Split('.')[0];
    }

    public static byte[] Query(IEnumerable<string> names)
    {
        var list = names.ToList();
        using var ms = new MemoryStream();
        ms.Write([0, 0, 0, 0, (byte)(list.Count >> 8), (byte)list.Count, 0, 0, 0, 0, 0, 0]);
        foreach (var n in list)
        {
            foreach (var label in n.TrimEnd('.').Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)Math.Min(63, b.Length));
                ms.Write(b, 0, Math.Min(63, b.Length));
            }
            ms.WriteByte(0);
            ms.Write([0, (byte)Ptr, 0, 1]);   // PTR, IN
        }
        return ms.ToArray();
    }

    /// <summary>Alle svar-poster i en pakke (svar, autoritet og ekstra). Kaster FormatException ved en ødelagt pakke.</summary>
    public static List<Record> Parse(byte[] p)
    {
        if (p.Length < 12) throw new FormatException("for kort til DNS");
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4)), an = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)),
            ns = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8)), ar = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(10));
        var i = 12;
        for (var q = 0; q < qd; q++) { Name(p, ref i); i += 4; }
        var list = new List<Record>();
        for (var r = 0; r < an + ns + ar; r++)
        {
            var name = Name(p, ref i);
            if (i + 10 > p.Length) throw new FormatException("afkortet DNS-post");
            var type = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(i));
            int len = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(i + 8));
            i += 10;
            if (i + len > p.Length) throw new FormatException("afkortet DNS-post");
            var start = i;
            switch (type)
            {
                case Ptr: { var j = start; list.Add(new(name, type, Target: Name(p, ref j))); break; }
                case Srv when len >= 7:
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(start + 4));
                    var j = start + 6;
                    list.Add(new(name, type, Target: Name(p, ref j), Port: port));
                    break;
                }
                case Txt: list.Add(new(name, type, Txt: ParseTxt(p.AsSpan(start, len)))); break;
                case A when len == 4: list.Add(new(name, type, Address: new IPAddress(p.AsSpan(start, 4)))); break;
                case Aaaa when len == 16: list.Add(new(name, type, Address: new IPAddress(p.AsSpan(start, 16)))); break;
            }
            i = start + len;
        }
        return list;
    }

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> d)
    {
        var t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < d.Length)
        {
            int l = d[i++];
            if (i + l > d.Length) break;
            var s = Encoding.UTF8.GetString(d.Slice(i, l));
            i += l;
            var eq = s.IndexOf('=');
            if (eq > 0) t.TryAdd(s[..eq], s[(eq + 1)..]);
            else if (s.Length > 0) t.TryAdd(s, "");
        }
        return t;
    }

    /// <summary>Navn med komprimering (højst 20 hop, så en ond pakke ikke kan låse løkken).</summary>
    private static string Name(byte[] p, ref int i)
    {
        var labels = new List<string>();
        var pos = i;
        var jumped = false;
        for (var hops = 0; hops < 20; hops++)
        {
            if (pos >= p.Length) throw new FormatException("afkortet navn");
            int l = p[pos];
            if (l == 0) { if (!jumped) i = pos + 1; return string.Join('.', labels); }
            if ((l & 0xC0) == 0xC0)
            {
                if (pos + 1 >= p.Length) throw new FormatException("afkortet navn");
                var target = ((l & 0x3F) << 8) | p[pos + 1];
                if (!jumped) i = pos + 2;
                jumped = true;
                pos = target;
                continue;
            }
            if (pos + 1 + l > p.Length) throw new FormatException("afkortet navn");
            labels.Add(Encoding.UTF8.GetString(p, pos + 1, l));
            pos += 1 + l;
        }
        throw new FormatException("for mange navnehop");
    }

    /// <summary>Saml posterne fra alle svar til tjenester (PTR → SRV → A/TXT).</summary>
    public static List<Service> Resolve(IEnumerable<Record> records, IEnumerable<string>? types = null)
    {
        var all = records.ToList();
        var wanted = (types ?? PrinterServices).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var res = new List<Service>();
        foreach (var ptr in all.Where(r => r.Type == Ptr && wanted.Contains(r.Name) && r.Target is not null).DistinctBy(r => r.Target!.ToLowerInvariant()))
        {
            var inst = ptr.Target!;
            var srv = all.FirstOrDefault(r => r.Type == Srv && Eq(r.Name, inst));
            if (srv?.Target is null) continue;
            var txt = all.FirstOrDefault(r => r.Type == Txt && Eq(r.Name, inst))?.Txt ?? [];
            var addrs = all.Where(r => r.Address is not null && (r.Type is A or Aaaa) && Eq(r.Name, srv.Target)).Select(r => r.Address!)
                .OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 0 : 1).Distinct().ToList();
            res.Add(new(inst, ptr.Name, srv.Target, srv.Port, txt, addrs));
        }
        return res;
    }

    private static bool Eq(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}
