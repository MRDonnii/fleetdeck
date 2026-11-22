namespace Fleetdeck.MacPack;

/// <summary>CRC-32 (IEEE 802.3, som zlib og UDIF bruger).</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var c = (uint)i;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private uint _crc = 0xFFFFFFFF;

    public void Append(ReadOnlySpan<byte> data)
    {
        var c = _crc;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        _crc = c;
    }

    public uint GetCurrentHashAsUInt32() => _crc ^ 0xFFFFFFFF;

    public static uint HashToUInt32(ReadOnlySpan<byte> data) { var c = new Crc32(); c.Append(data); return c.GetCurrentHashAsUInt32(); }
}
