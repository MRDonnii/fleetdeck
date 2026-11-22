using System.Text;

namespace Fleetdeck.Shared;

/// <summary>
/// Tekst fra Windows kommer i flere tegnsæt: UTF-8 (med/uden BOM), UTF-16 og gammelt Windows-1252/Latin-1
/// (fx Windows PowerShell 5.1 og ældre programmer). Uden at gætte rigtigt bliver æøå til "Ã¦" eller "�".
/// </summary>
public static class TextCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static string Decode(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE) return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        return IsUtf8(raw) ? Encoding.UTF8.GetString(raw) : Encoding.Latin1.GetString(raw);
    }

    public static bool IsUtf8(byte[] raw)
    {
        try { StrictUtf8.GetCharCount(raw); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}
