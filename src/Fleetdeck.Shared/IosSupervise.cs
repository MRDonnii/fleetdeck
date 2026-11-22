using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// Hemmelighederne til et ios-job, hentet af agenten med jobbets engangskode (aldrig i jobbets parametre eller output).
/// Certifikat og nøgle er DER (base64) til cfgutil -C/-K; nøglen er PKCS#1 som fra "openssl rsa -outform DER".
/// </summary>
public sealed class IosSecrets
{
    public string? CertDer { get; set; }
    public string? KeyDer { get; set; }
    public string? Organization { get; set; }
    public string? BackupPassword { get; set; }
}

/// <summary>Agenten melder et færdigt backup-sæt (alle dele er sendt med PUT /api/agent/kit).</summary>
public sealed class IosBackupComplete
{
    public string Set { get; set; } = "";
    public int Parts { get; set; }
    public long Bytes { get; set; }
    public bool Encrypted { get; set; }
    /// <summary>true = backup-adgangskoden er prøvet mod backuppens nøglering og passer.</summary>
    public bool? PasswordVerified { get; set; }
    public string? Udid { get; set; }
    public string? DeviceName { get; set; }
    public string? Firmware { get; set; }
    /// <summary>iPad'ens brugte plads (uden apps), da backuppen blev taget. null = ukendt.</summary>
    public long? DeviceBytes { get; set; }
    /// <summary>true = iPad'en lignede en tom iPad (opsætning, ingen apps eller næsten tom). Sådan en backup vælges aldrig af sig selv.</summary>
    public bool? DeviceEmpty { get; set; }
}

/// <summary>De tilvalg, cfgutil selv nævner i "cfgutil help &lt;kommando&gt;" – så valgfrie flag kun sendes med, når versionen kender dem.</summary>
public sealed partial class CfgutilHelp
{
    public string Text { get; }
    public IReadOnlySet<string> Options { get; }
    private CfgutilHelp(string text, HashSet<string> options) { Text = text; Options = options; }

    public static CfgutilHelp Parse(string? text)
    {
        var t = text ?? "";
        return new CfgutilHelp(t, Opt().Matches(t).Select(m => m.Value).ToHashSet(StringComparer.Ordinal));
    }

    public bool Has(string option) => Options.Contains(option);

    [GeneratedRegex(@"(?<![\w-])--[a-z][a-z0-9]*(?:-[a-z0-9]+)*")] private static partial Regex Opt();
}

/// <summary>Hvordan iPad'en overvåges: navnet på organisationen og overvågningscertifikatet (DER), samt opsætningens sprog og område.</summary>
public sealed record PrepareOptions(string Organization, string HostCertPath, string? Language = "da", string? Locale = "da_DK", string? Region = "DK", bool SkipSetup = true);

/// <summary>
/// Alle cfgutil-kommandoer til overvågning og backup bygges her – ét sted at rette, hvis Apple ændrer cfgutil. Argumenterne er en liste
/// (ingen skal), så navne med mellemrum er sikre. Kilde: Apples cfgutil(1): "To supervise a device, use the --supervised, --name and
/// --host-cert options"; prepare kræver en slettet iPad i opsætningsassistenten; restore-backup tager --source (backuppens navn = UDID).
/// </summary>
public static class CfgutilCommands
{
    /// <summary>Fælles tilvalg før kommandoen: JSON-output, overvågningsidentiteten (-C/-K) og præcis én iPad (-e).</summary>
    public static List<string> Global(string? ecid, string? certPath = null, string? keyPath = null)
    {
        var a = new List<string> { "--format", "JSON" };
        if (certPath is { Length: > 0 } && keyPath is { Length: > 0 }) a.AddRange(["-C", certPath, "-K", keyPath]);
        if (ecid is { Length: > 0 }) a.AddRange(["-e", ecid]);
        return a;
    }

    public static string[] Help(string command) => ["help", command];

    /// <summary>Krypterede backups: adgangskoden sættes på iPad'en (bekræftet med cfgutil 2.21).</summary>
    public static string[] SetBackupPassword(string password) => ["set-backup-password", "--password", password];

    /// <summary>Backup til MobileSync-mappen hos den bruger, cfgutil kører som (mappen hedder iPad'ens UDID).</summary>
    public static string[] Backup() => ["backup"];

    /// <summary>"Slet alt indhold og indstillinger". Find min iPad skal være slået fra.</summary>
    public static string[] Erase() => ["erase"];

    /// <summary>
    /// Overvåg en slettet iPad. Påkrævet: --name &lt;organisation&gt; og --host-cert &lt;cert.der&gt; (-C/-K står i de fælles tilvalg).
    /// --supervised sendes med, medmindre "cfgutil help prepare" kunne læses og ikke nævner den (cfgutil 2.21 lister --name som
    /// "required for supervision"). Valgfrit og kun hvis hjælpen nævner dem: --skip-all, --language (da), --locale (da_DK), --region (DK).
    /// </summary>
    public static string[] Prepare(PrepareOptions o, CfgutilHelp? help)
    {
        if (string.IsNullOrWhiteSpace(o.Organization)) throw new ArgumentException("Organisationens navn mangler", nameof(o));
        if (string.IsNullOrWhiteSpace(o.HostCertPath)) throw new ArgumentException("Overvågningscertifikatet mangler", nameof(o));
        var known = help is { Options.Count: > 0 };
        bool Has(string opt) => help?.Has(opt) == true;
        var a = new List<string> { "prepare" };
        if (!known || Has("--supervised")) a.Add("--supervised");
        a.AddRange(["--name", o.Organization.Trim(), "--host-cert", o.HostCertPath]);
        if (o.SkipSetup && Has("--skip-all")) a.Add("--skip-all");
        if (o.Language is { Length: > 0 } lang && Has("--language")) a.AddRange(["--language", lang]);
        if (o.Locale is { Length: > 0 } loc && Has("--locale")) a.AddRange(["--locale", loc]);
        if (o.Region is { Length: > 0 } reg && Has("--region")) a.AddRange(["--region", reg]);
        return [.. a];
    }

    /// <summary>
    /// Gendan en backup til en slettet iPad. --source = backuppens mappenavn (UDID). Adgangskoden til en krypteret backup sendes kun med,
    /// hvis "cfgutil help restore-backup" nævner --password (ellers spørger cfgutil ikke – og jobbet melder fejlen).
    /// </summary>
    public static string[] RestoreBackup(string? source, string? password, CfgutilHelp? help)
    {
        var a = new List<string> { "restore-backup" };
        if (source is { Length: > 0 } s) a.AddRange(["--source", s]);
        if (password is { Length: > 0 } pw && help?.Has("--password") == true) a.AddRange(["--password", pw]);
        return [.. a];
    }

    public static string[] InstallProfile(string path) => ["install-profile", path];
    public static string[] RemoveProfile(string identifier) => ["remove-profile", identifier];

    /// <summary>Kommandoen som tekst til jobbets output – med hemmelighederne skjult.</summary>
    public static string Display(IEnumerable<string> args, params string?[] secrets) =>
        Redact("cfgutil " + string.Join(' ', args.Select(x => x.Length == 0 || x.Any(char.IsWhiteSpace) ? $"\"{x}\"" : x)), secrets);

    /// <summary>Skjul hemmeligheder i tekst, der vises eller gemmes.</summary>
    public static string Redact(string? text, params string?[] secrets)
    {
        var t = text ?? "";
        foreach (var s in secrets)
            if (s is { Length: >= 4 }) t = t.Replace(s, "••••••", StringComparison.Ordinal);
        return t;
    }
}

/// <summary>
/// Trinene i "Gør overvåget via Mac'en". Standard (restore_first): backup → upload → slet → gendan → overvåg → profil → tjek, så iPad'en har sine
/// data igen, selv hvis overvågningen fejler. prepare_first overvåger før gendannelsen (som Apple Configurators "Forbered" + "Gendan").
/// </summary>
public static class SupervisePlan
{
    public const string RestoreFirst = "restore_first", PrepareFirst = "prepare_first";
    public const string Backup = "backup", Upload = "upload", Erase = "erase", Wait = "wait", Restore = "restore", Prepare = "prepare", Profile = "profile", Verify = "verify";

    public sealed record Step(string Id, string Title, string? Tap, int From, int To);

    public static string Norm(string? order) => order == PrepareFirst ? PrepareFirst : RestoreFirst;

    public static IReadOnlyList<Step> Steps(string? order)
    {
        var restoreFirst = Norm(order) == RestoreFirst;
        var ids = restoreFirst
            ? new[] { Backup, Upload, Erase, Wait, Restore, Prepare, Profile, Verify }
            : new[] { Backup, Upload, Erase, Wait, Prepare, Restore, Profile, Verify };
        // vægte: backup og gendannelse tager længst
        var weight = new Dictionary<string, int> { [Backup] = 25, [Upload] = 10, [Erase] = 8, [Wait] = 7, [Restore] = 30, [Prepare] = 8, [Profile] = 6, [Verify] = 6 };
        var list = new List<Step>(); var at = 0; var n = 0;
        foreach (var id in ids)
        {
            n++;
            var to = n == ids.Length ? 100 : at + weight[id];
            list.Add(new Step(id, $"Trin {n}/{ids.Length}: {Title(id)}", Tap(id, restoreFirst), at, to));
            at = to;
        }
        return list;
    }

    private static string Title(string id) => id switch
    {
        Backup => "Krypteret backup af iPad'en",
        Upload => "Sender backuppen til Fleetdeck",
        Erase => "Sletter iPad'en",
        Wait => "Venter på, at iPad'en starter igen",
        Restore => "Lægger backuppen tilbage",
        Prepare => "Gør iPad'en overvåget",
        Profile => "Installerer den låste profil",
        _ => "Tjekker, at iPad'en er overvåget",
    };

    private static string? Tap(string id, bool restoreFirst) => id switch
    {
        Backup => "Lås iPad'en op. Spørger den, så tryk \"Stol på\" og skriv iPad'ens kode.",
        Erase => "Rør ikke iPad'en og tag ikke kablet ud. Den slettes og genstarter.",
        Wait => "Lad iPad'en stå på \"Hej\"-skærmen – tryk ikke på noget.",
        Restore => "Rør ikke iPad'en – den genstarter, når gendannelsen er færdig.",
        Prepare => restoreFirst ? "Rør ikke iPad'en, før Fleetdeck siger, at den er færdig." : "Lad iPad'en stå på opsætningsskærmen.",
        _ => null,
    };
}

/// <summary>
/// Tjek af backup-adgangskoden mod en krypteret backups nøglering (BackupKeyBag i Manifest.plist), før iPad'en slettes.
/// Formatet: TLV (4 tegn + 4 byte længde). Nøglen afledes med PBKDF2-SHA256 (DPSL/DPIC, iOS 10.2+) og derefter PBKDF2-SHA1 (SALT/ITER);
/// passer koden, kan klassenøglerne (WPKY) pakkes ud med AES key unwrap (RFC 3394).
/// </summary>
public static class IosBackupKeybag
{
    private const int WrapPasscode = 2;

    public sealed class Keybag
    {
        public Dictionary<string, byte[]> Attrs { get; } = [];
        public List<Dictionary<string, byte[]>> ClassKeys { get; } = [];
    }

    public static Keybag Parse(byte[] data)
    {
        var kb = new Keybag();
        Dictionary<string, byte[]>? cur = null; var haveUuid = false;
        var i = 0;
        while (i + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data, i, 4);
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i + 4, 4));
            if (len < 0 || i + 8 + len > data.Length) throw new InvalidDataException("Nøgleringen er ødelagt");
            var v = data.AsSpan(i + 8, len).ToArray();
            i += 8 + len;
            if (tag == "UUID" && !haveUuid) { haveUuid = true; kb.Attrs[tag] = v; }
            else if (tag == "UUID") { cur = new Dictionary<string, byte[]> { [tag] = v }; kb.ClassKeys.Add(cur); }
            else if (cur is not null && tag is "CLAS" or "WRAP" or "WPKY" or "KTYP" or "PBKY") cur[tag] = v;
            else kb.Attrs[tag] = v;
        }
        return kb;
    }

    private static int Int(byte[] v) => v.Length switch
    {
        4 => (int)BinaryPrimitives.ReadUInt32BigEndian(v),
        8 => (int)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(v), int.MaxValue),
        _ => throw new InvalidDataException("Uventet tal i nøgleringen"),
    };

    /// <summary>Nøglen afledt af adgangskoden (32 byte).</summary>
    public static byte[] DeriveKey(Keybag kb, string password)
    {
        if (!kb.Attrs.TryGetValue("SALT", out var salt) || !kb.Attrs.TryGetValue("ITER", out var iter)) throw new InvalidDataException("Nøgleringen mangler SALT/ITER");
        var pw = Encoding.UTF8.GetBytes(password);
        if (kb.Attrs.TryGetValue("DPSL", out var dpsl) && kb.Attrs.TryGetValue("DPIC", out var dpic))
            pw = Rfc2898DeriveBytes.Pbkdf2(pw, dpsl, Int(dpic), HashAlgorithmName.SHA256, 32);
        return Rfc2898DeriveBytes.Pbkdf2(pw, salt, Int(iter), HashAlgorithmName.SHA1, 32);
    }

    /// <summary>true = koden passer, false = forkert kode, null = kan ikke afgøres (ingen kodebeskyttede nøgler eller ukendt format).</summary>
    public static bool? VerifyPassword(byte[] keybag, string password)
    {
        Keybag kb;
        try { kb = Parse(keybag); } catch (InvalidDataException) { return null; }
        var locked = kb.ClassKeys.Where(c => c.TryGetValue("WPKY", out _) && c.TryGetValue("WRAP", out var w) && (Int(w) & WrapPasscode) != 0).ToList();
        if (locked.Count == 0) return null;
        // urimeligt mange gennemløb: lad være (det ville hænge jobbet)
        if (kb.Attrs.TryGetValue("DPIC", out var dpic) && Int(dpic) > 100_000_000) return null;
        byte[] key;
        try { key = DeriveKey(kb, password); } catch (InvalidDataException) { return null; }
        return locked.All(c => Unwrap(key, c["WPKY"]) is not null);
    }

    private static readonly byte[] DefaultIv = [0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6, 0xA6];

    /// <summary>AES key wrap (RFC 3394).</summary>
    public static byte[] Wrap(byte[] kek, byte[] plain)
    {
        if (plain.Length % 8 != 0 || plain.Length < 16) throw new ArgumentException("Nøglen skal være et multiplum af 8 byte", nameof(plain));
        using var aes = Aes.Create(); aes.Key = kek;
        var n = plain.Length / 8;
        var a = (byte[])DefaultIv.Clone();
        var r = new byte[n][];
        for (var i = 0; i < n; i++) r[i] = plain.AsSpan(i * 8, 8).ToArray();
        var b = new byte[16];
        for (var j = 0; j <= 5; j++)
            for (var i = 0; i < n; i++)
            {
                a.CopyTo(b, 0); r[i].CopyTo(b, 8);
                var e = aes.EncryptEcb(b, PaddingMode.None);
                var t = (ulong)(n * j + i + 1);
                BinaryPrimitives.WriteUInt64BigEndian(a, BinaryPrimitives.ReadUInt64BigEndian(e.AsSpan(0, 8)) ^ t);
                r[i] = e.AsSpan(8, 8).ToArray();
            }
        return [.. a, .. r.SelectMany(x => x)];
    }

    /// <summary>AES key unwrap (RFC 3394): null, hvis integritetstjekket fejler (forkert nøgle).</summary>
    public static byte[]? Unwrap(byte[] kek, byte[] wrapped)
    {
        if (wrapped.Length % 8 != 0 || wrapped.Length < 24) return null;
        using var aes = Aes.Create(); aes.Key = kek;
        var n = wrapped.Length / 8 - 1;
        var a = wrapped.AsSpan(0, 8).ToArray();
        var r = new byte[n][];
        for (var i = 0; i < n; i++) r[i] = wrapped.AsSpan((i + 1) * 8, 8).ToArray();
        var b = new byte[16];
        for (var j = 5; j >= 0; j--)
            for (var i = n - 1; i >= 0; i--)
            {
                var t = (ulong)(n * j + i + 1);
                BinaryPrimitives.WriteUInt64BigEndian(b, BinaryPrimitives.ReadUInt64BigEndian(a) ^ t);
                r[i].CopyTo(b, 8);
                var d = aes.DecryptEcb(b, PaddingMode.None);
                a = d.AsSpan(0, 8).ToArray();
                r[i] = d.AsSpan(8, 8).ToArray();
            }
        return CryptographicOperations.FixedTimeEquals(a, DefaultIv) ? r.SelectMany(x => x).ToArray() : null;
    }
}

/// <summary>Navne på iPad-backups i Fleetdeck: sættet "ipad-&lt;iPad&gt;-&lt;yyyyMMdd-HHmmss&gt;", delene "&lt;sæt&gt;.p001.tar.gz" osv.</summary>
public static class IpadBackupNames
{
    public const string Prefix = "ipad-";
    public static string Part(string set, int no) => $"{set}.p{no:000}.tar.gz";
}
