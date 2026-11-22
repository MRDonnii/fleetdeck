using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Husstandens overvågningsidentitet: et selvsigneret X.509-certifikat med RSA-nøgle, som cfgutil bruger med -C/-K og --host-cert.</summary>
public static class SupervisionIdentity
{
    public const int Years = 20;

    /// <summary>(certifikat som DER, privat nøgle som PKCS#1 DER – samme format som "openssl rsa -outform DER").</summary>
    public static (byte[] Cert, byte[] Key) Create(string organization, DateTimeOffset now)
    {
        using var rsa = RSA.Create(2048);
        var name = new X500DistinguishedNameBuilder();
        name.AddOrganizationName(Clean(organization));
        name.AddCommonName($"{Clean(organization)} – Fleetdeck-overvågning");
        var req = new CertificateRequest(name.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(Years));
        return (cert.Export(X509ContentType.Cert), rsa.ExportRSAPrivateKey());
    }

    public static string Clean(string s)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length == 0 ? IpadSupervision.DefaultOrganization : t.Length > 64 ? t[..64] : t;
    }

    /// <summary>
    /// Den samme identitet som PEM til pymobiledevice3 (--keybag): RSA-nøglen (PKCS#1) og certifikatet i én fil. cfgutil får DER-filerne
    /// (-C/-K); begge er det samme certifikat og den samme nøgle, så en iPad overvåget den ene vej kan styres den anden vej.
    /// </summary>
    public static string ToPem(byte[] certDer, byte[] keyDer)
    {
        using var rsa = RSA.Create(); rsa.ImportRSAPrivateKey(keyDer, out _);
        return rsa.ExportRSAPrivateKeyPem() + "\n" + PemEncoding.WriteString("CERTIFICATE", certDer) + "\n";
    }

    /// <summary>PEM med certifikat og RSA-nøgle (PKCS#1 eller PKCS#8, ukrypteret) → (certifikat DER, nøgle PKCS#1 DER). null, hvis de ikke kan læses eller ikke passer sammen.</summary>
    public static (byte[] Cert, byte[] Key)? FromPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return null;
        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            using var rsa = RSA.Create(); rsa.ImportFromPem(pem);
            var c = cert.Export(X509ContentType.Cert); var k = rsa.ExportRSAPrivateKey();
            return Matches(c, k) ? (c, k) : null;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return null; }
    }

    /// <summary>SHA-256 af certifikatet (hex) – til at sammenligne to identiteter.</summary>
    public static string Thumbprint(byte[] certDer) => Convert.ToHexString(SHA256.HashData(certDer));

    /// <summary>Hører nøglen til certifikatet?</summary>
    public static bool Matches(byte[] certDer, byte[] keyDer)
    {
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(certDer);
            using var rsa = RSA.Create(); rsa.ImportRSAPrivateKey(keyDer, out _);
            using var pub = cert.GetRSAPublicKey();
            return pub is not null && pub.ExportParameters(false).Modulus!.AsSpan().SequenceEqual(rsa.ExportParameters(false).Modulus);
        }
        catch (CryptographicException) { return false; }
    }
}

/// <summary>
/// Husstandens ENE sæt iPad-hemmeligheder – til både Mac'en (Apple Configurator/cfgutil) og "iPad via USB" (pymobiledevice3):
/// overvågningsidentiteten (nøglen krypteret med DataProtection; udleveres som DER til cfgutil og som PEM-keybag til pymobiledevice3),
/// organisationens navn, backup-adgangskoden (krypteret) og engangskoderne, som ios-jobs henter hemmelighederne med.
/// Hemmelighederne står aldrig i et jobs parametre eller output.
/// <para>
/// Sammenlægning: "iPad via USB" havde før sine egne nøgler (usb_ipad_backup_password / usb_ipad_keybag). Har husstanden ingen identitet
/// eller backup-kode her, overtages de derfra. Findes der allerede en identitet her, vinder den altid (den kan sidde på en overvåget iPad) –
/// de gamle USB-værdier slettes ikke, og en afvigende gammel backup-kode vises på nødkortet, så ældre USB-backupper stadig kan åbnes.
/// </para>
/// </summary>
public sealed class IpadSupervision(SettingsService s, AppDb db, IDataProtectionProvider dp)
{
    public const string DefaultOrganization = "Familien";
    private const string KCert = "ipad_supervision_cert", KKey = "ipad_supervision_key", KBackup = "ipad_backup_password";
    public const string KOrg = "ipad_supervision_org";
    private const string KCertPrev = "ipad_supervision_cert_prev", KKeyPrev = "ipad_supervision_key_prev";
    /// <summary>De gamle nøgler fra "iPad via USB" (før sammenlægningen). Læses kun – for at overtage dem.</summary>
    public const string LegacyUsbPassword = "usb_ipad_backup_password", LegacyUsbKeybag = "usb_ipad_keybag";
    private const string NoncePrefix = "ios_nonce_";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IDataProtector keys = dp.CreateProtector("Fleetdeck.Ipad.SupervisionKey");
    private readonly IDataProtector backup = dp.CreateProtector("Fleetdeck.Ipad.BackupPassword");
    private readonly IDataProtector legacyUsb = dp.CreateProtector("Fleetdeck.UsbIpad.Secrets");

    public async Task<string> Organization() => (await s.Get(KOrg)).Trim() is { Length: > 0 } o ? o : DefaultOrganization;
    public Task SetOrganization(string org) => s.Set(KOrg, SupervisionIdentity.Clean(org));

    public sealed record IdentityInfo(string Subject, DateTime NotAfter, string Thumbprint);

    public async Task<IdentityInfo?> Info()
    {
        await Adopt();
        var c = await s.Get(KCert);
        if (c.Length == 0) return null;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(c));
            return new IdentityInfo(cert.Subject, cert.NotAfter.ToUniversalTime(), cert.GetCertHashString(HashAlgorithmName.SHA256));
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    /// <summary>Identiteten (laves første gang). Den overskrives aldrig: iPads, der er overvåget med den, kan ellers kun styres efter en sletning.</summary>
    public async Task<(byte[] Cert, byte[] Key)> Identity()
    {
        if (await Read() is { } have) return have;
        await Gate.WaitAsync();
        try
        {
            if (await Read() is { } again) return again;
            if ((await s.Get(KKey)).Length > 0)
                throw new InvalidOperationException("Overvågningsnøglen kan ikke læses (DataProtection-nøglerne i data/keys er skiftet). Gendan dem fra backup – en ny identitet ville " +
                                                    "gøre de overvågede iPads ustyrlige uden sletning.");
            if (await AdoptIdentityLocked() is { } adopted) return adopted;
            var (cert, key) = SupervisionIdentity.Create(await Organization(), DateTimeOffset.UtcNow);
            await s.Set(KKey, keys.Protect(Convert.ToBase64String(key)));
            await s.Set(KCert, Convert.ToBase64String(cert));
            return (cert, key);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Identiteten, hvis husstanden har en (overtager USB-udvidelsens gamle) – laver aldrig en ny. Til nødkortet.</summary>
    public async Task<(byte[] Cert, byte[] Key)?> Existing()
    {
        await Adopt();
        return await Read();
    }

    /// <summary>Identiteten som PEM-keybag til pymobiledevice3 (samme certifikat og nøgle som cfgutil får som DER). Laves første gang.</summary>
    public async Task<string> Keybag()
    {
        var (cert, key) = await Identity();
        return SupervisionIdentity.ToPem(cert, key);
    }

    /// <summary>
    /// Brug en anden identitet (PEM med certifikat og nøgle – fx Apple Configurators eksporteret som PEM). Har husstanden allerede en anden
    /// identitet, sker det kun med <paramref name="replace"/>: iPads, der er overvåget med den gamle, kan så kun styres lydløst efter en sletning.
    /// Den gamle gemmes som "forrige" (krypteret), så den kan findes igen.
    /// </summary>
    public async Task<string> SetIdentity(string pem, bool replace)
    {
        if (SupervisionIdentity.FromPem(pem) is not { } id) throw new ArgumentException("Indsæt certifikat og den private nøgle, der hører til (PEM, ukrypteret)");
        await Adopt();
        await Gate.WaitAsync();
        try
        {
            var curCert = await s.Get(KCert); var curKey = await s.Get(KKey);
            if (curCert.Length > 0)
            {
                if (SupervisionIdentity.Thumbprint(Convert.FromBase64String(curCert)) == SupervisionIdentity.Thumbprint(id.Cert)) return "Identiteten er allerede husstandens";
                if (!replace) throw new InvalidOperationException("Husstanden har allerede en overvågningsidentitet, som iPads kan være overvåget med. Vælg \"Erstat\" for at skifte " +
                                                              "alligevel – iPads overvåget med den nuværende kan så kun styres lydløst efter en sletning.");
                await s.Set(KCertPrev, curCert); await s.Set(KKeyPrev, curKey);
            }
            await s.Set(KKey, keys.Protect(Convert.ToBase64String(id.Key)));
            await s.Set(KCert, Convert.ToBase64String(id.Cert));
            return curCert.Length > 0 ? "Overvågningsidentiteten er skiftet (den forrige er gemt)" : "Overvågningsidentiteten er gemt";
        }
        finally { Gate.Release(); }
    }

    // ------------------------------------------------------------------ sammenlægning med "iPad via USB"
    private async Task<string?> Legacy(string key)
    {
        var enc = await s.Get(key);
        if (enc.Length == 0) return null;
        try { return legacyUsb.Unprotect(enc); } catch (CryptographicException) { return null; }
    }

    /// <summary>Overtag USB-udvidelsens gamle identitet og backup-kode, hvis husstanden ikke selv har dem. Rører aldrig en eksisterende værdi.</summary>
    public async Task Adopt()
    {
        if ((await s.Get(KCert)).Length > 0 && (await s.Get(KBackup)).Length > 0) return;
        if ((await s.Get(LegacyUsbKeybag)).Length == 0 && (await s.Get(LegacyUsbPassword)).Length == 0) return;
        await Gate.WaitAsync();
        try
        {
            await AdoptIdentityLocked();
            if ((await s.Get(KBackup)).Length == 0 && await Legacy(LegacyUsbPassword) is { Length: > 0 } pw) await s.Set(KBackup, backup.Protect(pw));
        }
        finally { Gate.Release(); }
    }

    /// <summary>(Under Gate) USB-udvidelsens gamle keybag bliver husstandens identitet – kun hvis der ingen er.</summary>
    private async Task<(byte[], byte[])?> AdoptIdentityLocked()
    {
        if ((await s.Get(KCert)).Length > 0 || (await s.Get(KKey)).Length > 0) return null;
        var legacy = SupervisionIdentity.FromPem(await Legacy(LegacyUsbKeybag));
        if (HouseholdSecrets.Decide(false, legacy is not null, false) != HouseholdSecrets.Adopt || legacy is not { } id) return null;
        await s.Set(KKey, keys.Protect(Convert.ToBase64String(id.Key)));
        await s.Set(KCert, Convert.ToBase64String(id.Cert));
        return id;
    }

    /// <summary>USB-udvidelsens gamle backup-kode, hvis den er en anden end husstandens (ældre USB-backupper er krypteret med den).</summary>
    public async Task<string?> PreviousUsbBackupPassword()
    {
        var old = await Legacy(LegacyUsbPassword);
        if (old is not { Length: > 0 }) return null;
        string? cur = null;
        if ((await s.Get(KBackup)) is { Length: > 0 } enc) try { cur = backup.Unprotect(enc); } catch (CryptographicException) { }
        return HouseholdSecrets.Decide(cur is not null, true, cur == old) == HouseholdSecrets.Conflict ? old : null;
    }

    /// <summary>Fingeraftrykket af USB-udvidelsens gamle identitet, hvis den er en anden end husstandens (iPads overvåget med den kan ikke styres lydløst).</summary>
    public async Task<string?> PreviousUsbIdentity()
    {
        if (SupervisionIdentity.FromPem(await Legacy(LegacyUsbKeybag)) is not { } old) return null;
        var cur = await s.Get(KCert);
        if (cur.Length == 0) return null;
        try
        {
            var same = SupervisionIdentity.Thumbprint(Convert.FromBase64String(cur)) == SupervisionIdentity.Thumbprint(old.Cert);
            return HouseholdSecrets.Decide(true, true, same) == HouseholdSecrets.Conflict ? SupervisionIdentity.Thumbprint(old.Cert) : null;
        }
        catch (FormatException) { return null; }
    }

    private async Task<(byte[], byte[])?> Read()
    {
        var c = await s.Get(KCert); var k = await s.Get(KKey);
        if (c.Length == 0 || k.Length == 0) return null;
        try { return (Convert.FromBase64String(c), Convert.FromBase64String(keys.Unprotect(k))); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    /// <summary>Husstandens backup-adgangskode til krypterede iPad-backups (laves første gang).</summary>
    public async Task<string> BackupPassword()
    {
        await Adopt();
        var enc = await s.Get(KBackup);
        if (enc.Length > 0)
            try { return backup.Unprotect(enc); }
            catch (CryptographicException) { throw new InvalidOperationException("Backup-adgangskoden kan ikke læses (DataProtection-nøglerne i data/keys er skiftet)."); }
        await Gate.WaitAsync();
        try
        {
            if ((await s.Get(KBackup)) is { Length: > 0 } e2) return backup.Unprotect(e2);
            var pw = NewBackupPassword();
            await s.Set(KBackup, backup.Protect(pw));
            return pw;
        }
        finally { Gate.Release(); }
    }

    public async Task<bool> HasBackupPassword() { await Adopt(); return (await s.Get(KBackup)).Length > 0; }

    /// <summary>Backup-koden, hvis husstanden har en – laver aldrig en ny (til nødkortet). null, hvis den ikke findes eller ikke kan læses.</summary>
    public async Task<string?> ExistingBackupPassword()
    {
        await Adopt();
        var enc = await s.Get(KBackup);
        if (enc.Length == 0) return null;
        try { return backup.Unprotect(enc); } catch (CryptographicException) { return null; }
    }

    /// <summary>5 × 4 små bogstaver/tal uden forvekslinger – til at skrive af, hvis backuppen skal gendannes i Finder.</summary>
    public static string NewBackupPassword()
    {
        const string a = "abcdefghijkmnpqrstuvwxyz23456789";
        return string.Join('-', Enumerable.Range(0, 5).Select(_ => new string(Enumerable.Range(0, 4).Select(_ => a[RandomNumberGenerator.GetInt32(a.Length)]).ToArray())));
    }

    // ------------------------------------------------------------------ engangskoder
    /// <summary>Ny engangskode til et job: kun SHA-256 gemmes. Selve koden står i jobbets parametre.</summary>
    public static string NewNonceValue() => Crypto.Token(24);
    public Task StoreNonce(int jobId, string nonce) => s.Set(NoncePrefix + jobId, Crypto.Sha256(nonce));

    /// <summary>Brug engangskoden (den slettes). false, hvis den ikke passer eller er brugt.</summary>
    public async Task<bool> TakeNonce(int jobId, string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return false;
        var row = await db.Settings.FindAsync(NoncePrefix + jobId);
        if (row is null || string.IsNullOrEmpty(row.Value) || !Crypto.FixedEquals(row.Value, Crypto.Sha256(nonce))) return false;
        db.Settings.Remove(row);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Ubrugte engangskoder til jobs, der er færdige.</summary>
    public async Task<int> PruneNonces()
    {
        var rows = await db.Settings.Where(x => x.Key.StartsWith(NoncePrefix)).ToListAsync();
        var gone = 0;
        foreach (var r in rows)
        {
            if (!int.TryParse(r.Key[NoncePrefix.Length..], out var id) || !await db.Jobs.AnyAsync(j => j.Id == id && JobStatus.Active.Contains(j.Status)))
            { db.Settings.Remove(r); gone++; }
        }
        if (gone > 0) await db.SaveChangesAsync();
        return gone;
    }

    /// <summary>Hemmelighederne til en handling – kun dem, den skal bruge.</summary>
    public async Task<IosSecrets> SecretsFor(string? action)
    {
        (byte[], byte[])? id = IosJobs.NeedsIdentity(action) ? await Identity() : null;
        var pw = IosJobs.NeedsBackupPassword(action) ? await BackupPassword() : null;
        return IosJobs.Secrets(action, id, await Organization(), pw);
    }

    /// <summary>Skjul hemmelighederne i tekst fra agenten (ekstra sikring – agenten skjuler dem selv).</summary>
    public async Task<string> Mask(string text)
    {
        var enc = await s.Get(KBackup);
        string? pw = null;
        if (enc.Length > 0) try { pw = backup.Unprotect(enc); } catch (CryptographicException) { }
        var key = await Read() is { } id ? Convert.ToBase64String(id.Item2) : null;
        return CfgutilCommands.Redact(text, pw, key);
    }
}

/// <summary>Hvad der sker med "iPad via USB"'s gamle værdi (identitet eller backup-kode) ved sammenlægningen.</summary>
public static class HouseholdSecrets
{
    public const string None = "none", Adopt = "adopt", Same = "same", Conflict = "conflict";

    /// <summary>
    /// <paramref name="haveOwn"/>: husstanden har allerede værdien. <paramref name="haveLegacy"/>: USB-udvidelsen har en. <paramref name="same"/>: de er ens.
    /// Kun "adopt" skriver noget: husstandens egen værdi erstattes aldrig (den kan sidde på en overvåget iPad eller i en krypteret backup).
    /// </summary>
    public static string Decide(bool haveOwn, bool haveLegacy, bool same) =>
        !haveLegacy ? None : !haveOwn ? Adopt : same ? Same : Conflict;
}

/// <summary>ios-jobs: parametrene (uden hemmeligheder – kun engangskoden) og hvilke hemmeligheder hver handling får.</summary>
public static class IosJobs
{
    public static bool NeedsIdentity(string? action) => action is IosActions.InstallProfile or IosActions.Supervise or IosActions.RemoveProfile or IosActions.Release or IosActions.SuperviseNoWipe;
    public static bool NeedsBackupPassword(string? action) => action is IosActions.Backup or IosActions.Supervise or IosActions.Restore or IosActions.Release or IosActions.SuperviseNoWipe;

    /// <param name="locale">sprog og region til overvågningen (Indstillinger); null = da/DK som hidtil.</param>
    public static JobParams Params(string action, string? ecid, Ipad p, string? order, string? backupSet, string? nonce, InstallLocale? locale = null)
    {
        var prm = new JobParams
        {
            Action = action, Ecid = ecid, IpadId = p.Id, SecretNonce = IosActions.NeedSecrets.Contains(action) ? nonce : null,
            TimeoutMin = action switch { IosActions.Backup => 120, IosActions.Update => 150, IosActions.Restore => 150, IosActions.Supervise or IosActions.Release => 235, _ => 10 },
        };
        if (action is IosActions.InstallProfile or IosActions.Supervise or IosActions.RemoveProfile or IosActions.Release)
        {
            prm.ProfileIdentifier = LockedProfile.Identifier(p);
            prm.ReplaceIds = [LockedProfile.DnsLinkIdentifier(p)];
        }
        if (action == IosActions.Supervise) { prm.Order = SupervisePlan.Norm(order); (prm.Language, prm.Region) = locale?.Apple ?? ("da", "DK"); }
        if (action == IosActions.Restore) prm.BackupSet = backupSet;
        return prm;
    }

    public static IosSecrets Secrets(string? action, (byte[] Cert, byte[] Key)? identity, string organization, string? backupPassword)
    {
        var r = new IosSecrets();
        if (NeedsIdentity(action) && identity is { } id)
        {
            r.CertDer = Convert.ToBase64String(id.Cert); r.KeyDer = Convert.ToBase64String(id.Key);
            r.Organization = organization;
        }
        if (NeedsBackupPassword(action)) r.BackupPassword = backupPassword;
        return r;
    }
}

/// <summary>
/// iPad-backups i Fleetdeck: data/ipad-backups/&lt;iPad&gt;/&lt;sæt&gt;/pNNN.tar.gz (+ meta.json, når alle dele er modtaget). De sendes med samme
/// PUT /api/agent/kit som gendannelsespunkter, men navne med "ipad-" havner her og røres ikke af gendannelsespunkternes oprydning.
/// Delene er stykker af én tar.gz (under 1,8 GB hver) – sæt dem sammen med cat før udpakning.
/// </summary>
public static partial class IpadBackups
{
    public const string Prefix = IpadBackupNames.Prefix;
    public const int Keep = 3;

    public static string Root(Paths p) => Path.Combine(p.Data, "ipad-backups");
    public static string Dir(Paths p, int ipadId) => Path.Combine(Root(p), ipadId.ToString());
    public static bool IsIpad(string? name) => name?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public sealed record Part(int IpadId, string Set, int No);

    /// <summary>"ipad-12-20261117-101500.p001.tar.gz" → iPad 12, sæt "ipad-12-20261117-101500", del 1.</summary>
    public static Part? ParsePart(string? name)
    {
        if (name is null) return null;
        var m = PartRx().Match(name);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var id) || !int.TryParse(m.Groups[3].Value, out var no) || no < 1) return null;
        return new Part(id, $"{Prefix}{id}-{m.Groups[2].Value}", no);
    }

    public static string PartName(string set, int no) => IpadBackupNames.Part(set, no);

    public static (int IpadId, string Set)? ParseSet(string? set)
    {
        if (set is null) return null;
        var m = SetRx().Match(set);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? (id, set) : null;
    }

    public static string PartFile(Paths p, Part x) => Path.Combine(Dir(p, x.IpadId), x.Set, $"p{x.No:000}.tar.gz");

    /// <summary>Gem en del (skrives færdig under et midlertidigt navn først).</summary>
    public static async Task<long> Receive(Paths p, Part x, Stream body, CancellationToken ct)
    {
        var file = PartFile(p, x);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".upload";
        await using (var f = File.Create(tmp)) await body.CopyToAsync(f, ct);
        var len = new FileInfo(tmp).Length;
        if (len == 0) { File.Delete(tmp); return 0; }
        File.Move(tmp, file, true);
        return len;
    }

    public sealed class Meta
    {
        public string Set { get; set; } = "";
        public DateTime Created { get; set; }
        public int Parts { get; set; }
        public long Bytes { get; set; }
        public bool Encrypted { get; set; }
        public bool? PasswordVerified { get; set; }
        public string? Udid { get; set; }
        public string? DeviceName { get; set; }
        public string? Firmware { get; set; }
        public int MachineId { get; set; }
        public string? Machine { get; set; }
        public long? DeviceBytes { get; set; }
        public bool? DeviceEmpty { get; set; }
    }

    /// <summary>Det sæt, "Overvåg uden datatab" sidst brugte, fastholdes (filen "pinned") og ryddes aldrig op.</summary>
    public const string PinFile = "pinned";

    public static void Pin(Paths p, int ipadId, string set) => PinDir(Dir(p, ipadId), set);

    public static void PinDir(string ipadDir, string set)
    {
        if (ParseSet(set) is null || !Directory.Exists(Path.Combine(ipadDir, set))) return;
        foreach (var d in Directory.GetDirectories(ipadDir))
        {
            var f = Path.Combine(d, PinFile);
            if (Path.GetFileName(d) != set && File.Exists(f)) File.Delete(f);
        }
        File.WriteAllText(Path.Combine(ipadDir, set, PinFile), DateTime.UtcNow.ToString("O"));
    }

    /// <summary>Alle dele er modtaget: skriv meta.json og ryd op i ældre sæt. Fejl (manglende dele) giver en besked.</summary>
    public static string? Complete(Paths p, int ipadId, IosBackupComplete c, Machine m, IReadOnlySet<string>? protect = null)
    {
        if (ParseSet(c.Set) is not { } s || s.IpadId != ipadId) return "Ukendt backup-sæt";
        var dir = Path.Combine(Dir(p, ipadId), c.Set);
        if (!Directory.Exists(dir)) return "Backuppen er ikke modtaget";
        var parts = Directory.GetFiles(dir, "p*.tar.gz").Length;
        if (c.Parts < 1 || parts != c.Parts) return $"Der er modtaget {parts} af {c.Parts} dele";
        var bytes = Directory.GetFiles(dir, "p*.tar.gz").Sum(f => new FileInfo(f).Length);
        var meta = new Meta
        {
            Set = c.Set, Created = DateTime.UtcNow, Parts = parts, Bytes = bytes, Encrypted = c.Encrypted, PasswordVerified = c.PasswordVerified,
            Udid = Clip(c.Udid), DeviceName = Clip(c.DeviceName), Firmware = Clip(c.Firmware), MachineId = m.Id, Machine = m.Name,
            DeviceBytes = c.DeviceBytes, DeviceEmpty = c.DeviceEmpty,
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta));
        Prune(p, ipadId, protect: protect);
        return null;
    }

    private static string? Clip(string? s) => s is null ? null : s.Length > 80 ? s[..80] : s;

    public sealed record SetInfo(string Set, DateTime Created, bool Complete, int Parts, long Bytes, bool Encrypted, bool? PasswordVerified,
        string? DeviceName, string? Firmware, string? Machine, string? Udid = null, bool? DeviceEmpty = null, bool Pinned = false);

    public static List<SetInfo> List(Paths p, int ipadId) => ListDir(Dir(p, ipadId));

    public static List<SetInfo> ListDir(string d)
    {
        if (!Directory.Exists(d)) return [];
        var list = new List<SetInfo>();
        foreach (var dir in new DirectoryInfo(d).GetDirectories().Where(x => ParseSet(x.Name) is not null))
        {
            var mf = Path.Combine(dir.FullName, "meta.json");
            Meta? meta = null;
            try { if (File.Exists(mf)) meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(mf)); } catch (Exception e) when (e is IOException or JsonException) { }
            var files = dir.GetFiles("p*.tar.gz");
            var pinned = File.Exists(Path.Combine(dir.FullName, PinFile));
            list.Add(meta is not null
                ? new SetInfo(dir.Name, meta.Created, true, meta.Parts, meta.Bytes, meta.Encrypted, meta.PasswordVerified, meta.DeviceName, meta.Firmware, meta.Machine,
                    meta.Udid, meta.DeviceEmpty, pinned)
                : new SetInfo(dir.Name, dir.LastWriteTimeUtc, false, files.Length, files.Sum(f => f.Length), false, null, null, null, null, null, null, pinned));
        }
        return [.. list.OrderByDescending(x => x.Set, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Behold de nyeste <paramref name="keep"/> hele sæt; ufærdige sæt slettes efter et døgn. Det fastholdte sæt (<see cref="PinFile"/>) og sæt, et kørende
    /// "Overvåg uden datatab" kan bruge (<paramref name="protect"/>), slettes aldrig – så en ny, tom backup kan ikke skubbe den gode ud.
    /// </summary>
    public static void Prune(Paths p, int ipadId, int keep = Keep, IReadOnlySet<string>? protect = null) => PruneDir(Dir(p, ipadId), keep, protect);

    public static void PruneDir(string ipadDir, int keep = Keep, IReadOnlySet<string>? protect = null)
    {
        var all = ListDir(ipadDir);
        var drop = all.Where(x => x.Complete && !x.Pinned && protect?.Contains(x.Set) != true).Skip(keep)
            .Concat(all.Where(x => !x.Complete && !x.Pinned && protect?.Contains(x.Set) != true && x.Created < DateTime.UtcNow.AddDays(-1)));
        foreach (var x in drop) { try { Directory.Delete(Path.Combine(ipadDir, x.Set), true); } catch (IOException) { } }
    }

    [GeneratedRegex(@"^ipad-(\d{1,9})-(\d{8}-\d{6})\.p(\d{3})\.tar\.gz$")] private static partial Regex PartRx();
    [GeneratedRegex(@"^ipad-(\d{1,9})-\d{8}-\d{6}$")] private static partial Regex SetRx();
}
