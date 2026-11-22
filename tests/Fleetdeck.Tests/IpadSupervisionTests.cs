using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Xunit;

namespace Fleetdeck.Tests;

public class CfgutilCommandTests
{
    private static readonly CfgutilHelp Full = CfgutilHelp.Parse("""
        Usage: cfgutil prepare [--supervised] [--name <name>] [--host-cert <path>] [--skip-all] [--language <code>] [--locale <code>] [--region <code>]
          --forbid-pairing   Never pair with another host.
        """);

    /// <summary>De tilvalg, cfgutil 2.21 på den rigtige Mac viste for "help prepare" (uddrag – listen fortsætter).</summary>
    private static readonly CfgutilHelp V221 = CfgutilHelp.Parse("""
        prepare: Initial configuration of freshly erased devices.
          --address --anchor-cert --dep --dep-mdm-password --dep-mdm-username --email --host-cert --language (e.g. en) --locale (e.g. en_US)
          --magic --mdm-url --name (supervising organization's name, required for supervision)
        """);

    [Fact] public void PrepareWithTheRealCfgutil221Options() =>
        Assert.Equal(["prepare", "--name", "Familien", "--host-cert", "/t/c.der", "--language", "da", "--locale", "da_DK"],
            CfgutilCommands.Prepare(new PrepareOptions("Familien", "/t/c.der"), V221));

    [Fact] public void GlobalOptionsComeFirstWithIdentityAndOneDevice() =>
        Assert.Equal(["--format", "JSON", "-C", "/t/cert.der", "-K", "/t/key.der", "-e", "0x1A2B"], CfgutilCommands.Global("0x1A2B", "/t/cert.der", "/t/key.der"));

    [Fact] public void GlobalOptionsWithoutIdentity() => Assert.Equal(["--format", "JSON", "-e", "0x1"], CfgutilCommands.Global("0x1"));

    [Fact] public void IdentityNeedsBothCertAndKey() => Assert.DoesNotContain("-C", CfgutilCommands.Global("0x1", "/t/cert.der", null));

    [Fact] public void PrepareSupervisesWithNameAndHostCert() =>
        Assert.Equal(["prepare", "--supervised", "--name", "Familien Hansen", "--host-cert", "/t/cert.der", "--skip-all", "--language", "da", "--locale", "da_DK", "--region", "DK"],
            CfgutilCommands.Prepare(new PrepareOptions(" Familien Hansen ", "/t/cert.der"), Full));

    [Fact] public void PrepareOnlyUsesOptionalFlagsTheHelpMentions() =>
        Assert.Equal(["prepare", "--supervised", "--name", "Familien", "--host-cert", "/t/c.der"],
            CfgutilCommands.Prepare(new PrepareOptions("Familien", "/t/c.der"), CfgutilHelp.Parse("prepare [--supervised] [--name] [--host-cert]")));

    [Fact] public void PrepareWithUnknownHelpSendsOnlyTheRequiredFlags() =>
        Assert.Equal(["prepare", "--supervised", "--name", "F", "--host-cert", "/c"], CfgutilCommands.Prepare(new PrepareOptions("F", "/c"), null));

    [Fact] public void PrepareCanKeepTheSetupAssistant() =>
        Assert.DoesNotContain("--skip-all", CfgutilCommands.Prepare(new PrepareOptions("F", "/c", SkipSetup: false), Full));

    [Theory, InlineData(""), InlineData("  ")]
    public void PrepareRefusesAMissingOrganization(string org) =>
        Assert.Throws<ArgumentException>(() => CfgutilCommands.Prepare(new PrepareOptions(org, "/c"), Full));

    [Fact] public void BackupPasswordCommand() => Assert.Equal(["set-backup-password", "--password", "abcd-efgh"], CfgutilCommands.SetBackupPassword("abcd-efgh"));

    [Fact] public void EraseAndBackupCommands()
    {
        Assert.Equal(["erase"], CfgutilCommands.Erase());
        Assert.Equal(["backup"], CfgutilCommands.Backup());
        Assert.Equal(["help", "prepare"], CfgutilCommands.Help("prepare"));
    }

    [Fact] public void RestoreSendsThePasswordOnlyWhenCfgutilKnowsTheOption()
    {
        Assert.Equal(["restore-backup", "--source", "00008027-001A"], CfgutilCommands.RestoreBackup("00008027-001A", "pw-1234", CfgutilHelp.Parse("restore-backup [--source <name>]")));
        Assert.Equal(["restore-backup", "--source", "U", "--password", "pw-1234"],
            CfgutilCommands.RestoreBackup("U", "pw-1234", CfgutilHelp.Parse("restore-backup [--source <name>] [--password <pw>]")));
        Assert.Equal(["restore-backup"], CfgutilCommands.RestoreBackup(null, null, null));
    }

    [Fact] public void HelpParsingFindsOptionsButNotHyphenatedWords()
    {
        var h = CfgutilHelp.Parse("Use --skip-all to skip. A re-try of set-backup-password; --host-cert <x>, [--language]");
        Assert.True(h.Has("--skip-all")); Assert.True(h.Has("--host-cert")); Assert.True(h.Has("--language"));
        Assert.False(h.Has("--re-try")); Assert.Equal(3, h.Options.Count);
    }

    [Fact] public void DisplayedCommandsNeverShowTheBackupPassword()
    {
        const string pw = "k7pm-x2qa-9fhr-3kdn-wq8e";
        var shown = CfgutilCommands.Display([.. CfgutilCommands.Global("0x1"), .. CfgutilCommands.SetBackupPassword(pw)], pw);
        Assert.DoesNotContain(pw, shown);
        Assert.Contains("set-backup-password --password ••••••", shown);
    }

    [Fact] public void DisplayQuotesArgumentsWithSpaces() =>
        Assert.Equal("cfgutil prepare --name \"Familien Hansen\"", CfgutilCommands.Display(["prepare", "--name", "Familien Hansen"]));

    [Fact] public void RedactHidesEverySecretAndIgnoresTinyOnes()
    {
        Assert.Equal("a •••••• b •••••• c", CfgutilCommands.Redact("a SECRET1 b KEYDATA== c", "SECRET1", "KEYDATA==", null));
        Assert.Equal("abc", CfgutilCommands.Redact("abc", "b"));   // for korte "hemmeligheder" ville ødelægge teksten
    }
}

public class SupervisePlanTests
{
    [Theory, InlineData(null), InlineData(""), InlineData("prepare_first"), InlineData("restore_first"), InlineData("noget andet")]
    public void StepsCoverZeroToHundredInOrder(string? order)
    {
        var s = SupervisePlan.Steps(order);
        Assert.Equal(0, s[0].From);
        Assert.Equal(100, s[^1].To);
        for (var i = 0; i < s.Count; i++)
        {
            Assert.True(s[i].From < s[i].To);
            if (i > 0) Assert.Equal(s[i - 1].To, s[i].From);
            Assert.StartsWith($"Trin {i + 1}/{s.Count}: ", s[i].Title);
        }
    }

    [Fact] public void DefaultRestoresBeforeSupervising() =>
        Assert.Equal(["backup", "upload", "erase", "wait", "restore", "prepare", "profile", "verify"], SupervisePlan.Steps(null).Select(x => x.Id));

    [Fact] public void PrepareFirstSupervisesBeforeRestoring() =>
        Assert.Equal(["backup", "upload", "erase", "wait", "prepare", "restore", "profile", "verify"], SupervisePlan.Steps("prepare_first").Select(x => x.Id));

    [Fact] public void NothingIsErasedBeforeTheBackupIsUploaded()
    {
        foreach (var order in new[] { SupervisePlan.RestoreFirst, SupervisePlan.PrepareFirst })
        {
            var ids = SupervisePlan.Steps(order).Select(x => x.Id).ToList();
            Assert.True(ids.IndexOf("backup") < ids.IndexOf("upload"));
            Assert.True(ids.IndexOf("upload") < ids.IndexOf("erase"));
        }
    }

    [Fact] public void TheUserIsToldWhatToDoDuringTheErase() => Assert.Contains("Rør ikke", SupervisePlan.Steps(null).Single(x => x.Id == "erase").Tap);
}

public class BackupKeybagTests
{
    [Fact] public void KeyWrapMatchesRfc3394()
    {
        var kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var key = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var wrapped = IosBackupKeybag.Wrap(kek, key);
        Assert.Equal("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5", Convert.ToHexString(wrapped));
        Assert.Equal(key, IosBackupKeybag.Unwrap(kek, wrapped));
        kek[0] ^= 1;
        Assert.Null(IosBackupKeybag.Unwrap(kek, wrapped));
    }

    private static void Tlv(MemoryStream ms, string tag, byte[] v)
    {
        ms.Write(Encoding.ASCII.GetBytes(tag));
        var len = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(len, (uint)v.Length); ms.Write(len);
        ms.Write(v);
    }

    private static byte[] U32(int v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }

    /// <summary>En nøglering som i Manifest.plist (få gennemløb, så testen er hurtig).</summary>
    private static byte[] Keybag(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(20); var dpsl = RandomNumberGenerator.GetBytes(20);
        var pk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), dpsl, 50, HashAlgorithmName.SHA256, 32);
        pk = Rfc2898DeriveBytes.Pbkdf2(pk, salt, 10, HashAlgorithmName.SHA1, 32);
        using var ms = new MemoryStream();
        Tlv(ms, "VERS", U32(4)); Tlv(ms, "TYPE", U32(1)); Tlv(ms, "UUID", RandomNumberGenerator.GetBytes(16)); Tlv(ms, "HMCK", new byte[40]);
        Tlv(ms, "WRAP", U32(0)); Tlv(ms, "SALT", salt); Tlv(ms, "ITER", U32(10)); Tlv(ms, "DPWT", U32(1)); Tlv(ms, "DPIC", U32(50)); Tlv(ms, "DPSL", dpsl);
        foreach (var cls in new[] { 1, 2, 3 })
        {
            Tlv(ms, "UUID", RandomNumberGenerator.GetBytes(16)); Tlv(ms, "CLAS", U32(cls)); Tlv(ms, "WRAP", U32(2)); Tlv(ms, "KTYP", U32(0));
            Tlv(ms, "WPKY", IosBackupKeybag.Wrap(pk, RandomNumberGenerator.GetBytes(32)));
        }
        return ms.ToArray();
    }

    [Fact] public void TheRightPasswordUnlocksTheKeybag()
    {
        var kb = Keybag("k7pm-x2qa-9fhr-3kdn-wq8e");
        Assert.True(IosBackupKeybag.VerifyPassword(kb, "k7pm-x2qa-9fhr-3kdn-wq8e"));
        Assert.False(IosBackupKeybag.VerifyPassword(kb, "en-anden-kode"));
    }

    [Fact] public void KeybagParsingSeparatesClassKeys()
    {
        var kb = IosBackupKeybag.Parse(Keybag("x"));
        Assert.Equal(3, kb.ClassKeys.Count);
        Assert.True(kb.Attrs.ContainsKey("SALT"));
        Assert.All(kb.ClassKeys, c => Assert.True(c.ContainsKey("WPKY")));
    }

    [Fact] public void BrokenKeybagsAreUndecided()
    {
        Assert.Null(IosBackupKeybag.VerifyPassword([1, 2, 3], "x"));
        Assert.Null(IosBackupKeybag.VerifyPassword(Encoding.ASCII.GetBytes("VERS\0\0\0\x7f"), "x"));
    }
}

public class SupervisionIdentityTests
{
    [Fact] public void IdentityIsASelfSignedCertWithAMatchingPkcs1Key()
    {
        var now = DateTimeOffset.UtcNow;
        var (certDer, keyDer) = SupervisionIdentity.Create("Familien", now);
        using var cert = X509CertificateLoader.LoadCertificate(certDer);
        Assert.Contains("O=Familien", cert.Subject);
        Assert.Equal(cert.Subject, cert.Issuer);
        Assert.True(cert.NotAfter.ToUniversalTime() > now.AddYears(SupervisionIdentity.Years - 1).UtcDateTime);
        using var rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(keyDer, out var read);   // PKCS#1, som "openssl rsa -outform DER"
        Assert.Equal(keyDer.Length, read);
        Assert.Equal(2048, rsa.KeySize);
        Assert.True(SupervisionIdentity.Matches(certDer, keyDer));
        Assert.False(SupervisionIdentity.Matches(certDer, SupervisionIdentity.Create("Andre", now).Key));
    }

    [Theory, InlineData("", "Familien"), InlineData("  Hansen \n", "Hansen")]
    public void OrganizationNameIsCleaned(string input, string expected) => Assert.Equal(expected, SupervisionIdentity.Clean(input));

    [Fact] public void BackupPasswordsAreLongAndEasyToType()
    {
        var pw = IpadSupervision.NewBackupPassword();
        Assert.Matches("^[a-z2-9]{4}(-[a-z2-9]{4}){4}$", pw);
        Assert.DoesNotContain('l', pw); Assert.DoesNotContain('o', pw); Assert.DoesNotContain('1', pw); Assert.DoesNotContain('0', pw);
        Assert.NotEqual(pw, IpadSupervision.NewBackupPassword());
    }
}

public class IosJobSecretTests
{
    private const string Password = "k7pm-x2qa-9fhr-3kdn-wq8e";
    private static readonly (byte[] Cert, byte[] Key) Id = SupervisionIdentity.Create("Familien", DateTimeOffset.UtcNow);
    private static Ipad Pad() => new() { Id = 7, Name = "Test", Kind = IpadKinds.Child, ClientId = "fdabc23", Ecid = "0x1A2B" };

    [Theory, MemberData(nameof(Actions))]
    public void SecretsAreNeverInTheJobParams(string action)
    {
        var nonce = IpadSupervision.NewNonceValue();
        var json = Json.Ser(IosJobs.Params(action, "0x1A2B", Pad(), null, null, nonce));
        Assert.DoesNotContain(Password, json);
        Assert.DoesNotContain(Convert.ToBase64String(Id.Key), json);
        Assert.DoesNotContain(Convert.ToBase64String(Id.Cert), json);
        // kun handlinger, der skal bruge hemmeligheder, får en engangskode
        Assert.Equal(IosActions.NeedSecrets.Contains(action), json.Contains(nonce));
    }

    public static TheoryData<string> Actions() => [.. IosActions.All];

    [Fact] public void EachActionOnlyGetsTheSecretsItNeeds()
    {
        var install = IosJobs.Secrets(IosActions.InstallProfile, Id, "Familien", Password);
        Assert.NotNull(install.KeyDer); Assert.Null(install.BackupPassword);
        var backup = IosJobs.Secrets(IosActions.Backup, Id, "Familien", Password);
        Assert.Null(backup.KeyDer); Assert.Null(backup.CertDer); Assert.Equal(Password, backup.BackupPassword);
        var restore = IosJobs.Secrets(IosActions.Restore, Id, "Familien", Password);
        Assert.Null(restore.KeyDer); Assert.Equal(Password, restore.BackupPassword);
        var sup = IosJobs.Secrets(IosActions.Supervise, Id, "Familien", Password);
        Assert.NotNull(sup.KeyDer); Assert.Equal(Password, sup.BackupPassword); Assert.Equal("Familien", sup.Organization);
        foreach (var a in new[] { IosActions.List, IosActions.Update, IosActions.Help })
        {
            var none = IosJobs.Secrets(a, Id, "Familien", Password);
            Assert.Null(none.KeyDer); Assert.Null(none.CertDer); Assert.Null(none.BackupPassword);
        }
    }

    [Fact] public void ErasingActionsNeedSecretsAndAreKnown()
    {
        Assert.All(IosActions.Erasing, a => Assert.Contains(a, IosActions.All));
        Assert.All(IosActions.Erasing, a => Assert.Contains(a, IosActions.NeedSecrets));
    }

    [Fact] public void SuperviseParamsCarryOrderAndLocale()
    {
        var p = IosJobs.Params(IosActions.Supervise, "0x1", Pad(), "prepare_first", null, "n");
        Assert.Equal("prepare_first", p.Order); Assert.Equal("da", p.Language); Assert.Equal("DK", p.Region);
        Assert.Equal(LockedProfile.Identifier(Pad()), p.ProfileIdentifier);
        Assert.True(p.TimeoutMin < 240);   // serveren giver op efter 4 timer
        Assert.Equal("restore_first", IosJobs.Params(IosActions.Supervise, "0x1", Pad(), "x", null, "n").Order);
    }

    [Theory]
    [InlineData("Test", "Test", true), InlineData(" test ", "Test", true), InlineData("Tes", "Test", false), InlineData("", "Test", false), InlineData(null, "Test", false)]
    public void TypedConfirmationMustMatchTheName(string? typed, string name, bool ok) =>
        Assert.Equal(ok, Fleetdeck.Server.Endpoints.IpadEndpoints.ConfirmMatches(typed, name));
}

public class IpadBackupTests
{
    [Theory]
    [InlineData("ipad-12-20261117-101500.p001.tar.gz", 12, "ipad-12-20261117-101500", 1)]
    [InlineData("ipad-3-20260101-000000.p042.tar.gz", 3, "ipad-3-20260101-000000", 42)]
    public void PartNamesAreParsed(string name, int ipad, string set, int no)
    {
        var p = IpadBackups.ParsePart(name);
        Assert.NotNull(p);
        Assert.Equal(ipad, p.IpadId); Assert.Equal(set, p.Set); Assert.Equal(no, p.No);
        Assert.Equal(name, IpadBackupNames.Part(p.Set, p.No));
    }

    [Theory]
    [InlineData("ipad-12-20261117-101500.p000.tar.gz"), InlineData("ipad-x-20261117-101500.p001.tar.gz"), InlineData("ipad-1-../../etc.p001.tar.gz"),
     InlineData("ipad-1-20261117-101500.tar.gz"), InlineData("20261117-101500.tar.gz"), InlineData(null)]
    public void BadPartNamesAreRefused(string? name) => Assert.Null(IpadBackups.ParsePart(name));

    [Fact] public void IpadBackupsAreNotKits()
    {
        Assert.True(IpadBackups.IsIpad("ipad-1-20261117-101500.p001.tar.gz"));
        Assert.False(IpadBackups.IsIpad("20261117-101500.tar.gz"));
        // også et gyldigt kit-navn – derfor skal ipad- håndteres før kit-reglerne
        Assert.NotNull(Kits.SafeName("ipad-1-20261117-101500.p001.tar.gz"));
    }

    [Fact] public void KitPruningLeavesIpadBackupsAlone()
    {
        var dir = Directory.CreateTempSubdirectory("fd-kits-").FullName;
        try
        {
            for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(dir, $"202601{i + 1:00}-000000.tar.gz"), "x");
            var ipad = Path.Combine(dir, "ipad-1-20260101-000000.p001.tar.gz");
            File.WriteAllText(ipad, "x");
            foreach (var f in Directory.GetFiles(dir)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-100));
            Kits.Prune(dir, 14);
            Assert.True(File.Exists(ipad));
            Assert.Equal(14, Directory.GetFiles(dir, "2026*.tar.gz").Length);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>Husstandens ene identitet og backup-kode – til både cfgutil (DER) og pymobiledevice3 (PEM) – og sammenlægningen med USB-udvidelsens gamle.</summary>
public class HouseholdSecretsTests
{
    [Fact] public void PemAndDerAreTheSameIdentity()
    {
        var (cert, key) = SupervisionIdentity.Create("Familien", DateTimeOffset.UtcNow);
        var pem = SupervisionIdentity.ToPem(cert, key);
        Assert.Null(UsbIpadKeybag.Validate(pem));
        var back = SupervisionIdentity.FromPem(pem);
        Assert.NotNull(back);
        Assert.Equal(cert, back.Value.Cert);
        Assert.Equal(key, back.Value.Key);
        Assert.Equal(SupervisionIdentity.Thumbprint(cert), SupervisionIdentity.Thumbprint(back.Value.Cert));
    }

    [Fact] public void AnOldUsbKeybagCanBeReadAsDer()
    {
        // sådan lavede "iPad via USB" sin keybag før sammenlægningen: PKCS#1-nøgle + certifikat med CN
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Fleetdeck", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pem = rsa.ExportRSAPrivateKeyPem() + "\n" + c.ExportCertificatePem() + "\n";
        var id = SupervisionIdentity.FromPem(pem);
        Assert.NotNull(id);
        Assert.True(SupervisionIdentity.Matches(id.Value.Cert, id.Value.Key));
        // PKCS#8 (fx eksporteret fra Apple Configurator via openssl) virker også
        Assert.NotNull(SupervisionIdentity.FromPem(rsa.ExportPkcs8PrivateKeyPem() + "\n" + c.ExportCertificatePem()));
    }

    [Fact] public void AKeyThatDoesNotMatchTheCertificateIsRefused()
    {
        var (cert, _) = SupervisionIdentity.Create("A", DateTimeOffset.UtcNow);
        var (_, otherKey) = SupervisionIdentity.Create("B", DateTimeOffset.UtcNow);
        Assert.Null(SupervisionIdentity.FromPem(SupervisionIdentity.ToPem(cert, otherKey)));
        Assert.Null(SupervisionIdentity.FromPem(""));
        Assert.Null(SupervisionIdentity.FromPem("-----BEGIN CERTIFICATE-----\nnope\n-----END CERTIFICATE-----"));
    }

    [Theory]
    [InlineData(false, false, false, HouseholdSecrets.None)]
    [InlineData(true, false, false, HouseholdSecrets.None)]
    [InlineData(false, true, false, HouseholdSecrets.Adopt)]     // kun USB havde en: den overtages
    [InlineData(true, true, true, HouseholdSecrets.Same)]
    [InlineData(true, true, false, HouseholdSecrets.Conflict)]   // Mac'ens (husstandens) vinder altid – den kan sidde på en overvåget iPad
    public void MigrationNeverReplacesTheHouseholdsOwn(bool own, bool legacy, bool same, string expected) =>
        Assert.Equal(expected, HouseholdSecrets.Decide(own, legacy, same));

    [Fact] public void ReleaseAndRemoveProfileGetTheIdentityAndReleaseAlsoThePassword()
    {
        var id = SupervisionIdentity.Create("Familien", DateTimeOffset.UtcNow);
        var rm = IosJobs.Secrets(IosActions.RemoveProfile, id, "Familien", "pw-pw-pw");
        Assert.NotNull(rm.KeyDer); Assert.Null(rm.BackupPassword);
        var rel = IosJobs.Secrets(IosActions.Release, id, "Familien", "pw-pw-pw");
        Assert.NotNull(rel.KeyDer); Assert.Equal("pw-pw-pw", rel.BackupPassword);
        Assert.Contains(IosActions.Release, IosActions.Erasing);
    }
}
