using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Xunit;

namespace Fleetdeck.Tests;

public class MdmProfileTests
{
    private static Ipad Pad() => new() { Id = 7, Name = "Børne-iPad Æble", Kind = IpadKinds.Child, ClientId = "fdk7pm23", HiddenApps = ["com.google.ios.youtube"] };

    private static List<Dictionary<string, object>> Payloads(Dictionary<string, object> top) =>
        ((List<object>)top["PayloadContent"]).Cast<Dictionary<string, object>>().ToList();

    [Fact]
    public void Plist_roundtrips_types_and_escapes()
    {
        var xml = Plist.Write(new PDict
        {
            { "s", "<æøå & \"x\">" }, { "i", 42 }, { "l", 5_000_000_000L }, { "r", 0.5 }, { "t", true }, { "f", false },
            { "d", new byte[] { 1, 2, 3 } }, { "a", new List<object> { "x", 1 } }, { "n", new PDict { { "k", "v" } } }, { "skipped", null },
        });
        var d = Plist.ParseDict(xml);
        Assert.Equal("<æøå & \"x\">", d["s"]);
        Assert.Equal(42L, d["i"]);
        Assert.Equal(5_000_000_000L, d["l"]);
        Assert.Equal(0.5, d["r"]);
        Assert.Equal(true, d["t"]);
        Assert.Equal(false, d["f"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, d["d"]);
        Assert.Equal(new List<object> { "x", 1L }, d["a"]);
        Assert.Equal("v", ((Dictionary<string, object>)d["n"])["k"]);
        Assert.False(d.ContainsKey("skipped"));
        Assert.Throws<FormatException>(() => Plist.Parse("<notplist/>"));
        Assert.Throws<FormatException>(() => Plist.Parse("ikke xml"));
    }

    [Fact]
    public void Enrollment_profile_has_signmessage_identity_and_topic()
    {
        var ca = MdmPki.CreateCa("Test-husstand");
        var id = MdmPki.IssueDevice(ca.Pem, ca.KeyPem, 7);
        var xml = MdmEnrollmentProfile.Build(Pad(), "mdm.example.org", "com.apple.mgmt.External.11111111-2222-3333-4444-555555555555", "key_123-abc", id.Pkcs12, id.Password, "Test-husstand");
        var top = Plist.ParseDict(xml);
        Assert.Equal("Configuration", top["PayloadType"]);
        Assert.Equal("dk.fleetdeck.mdm.fdk7pm23", top["PayloadIdentifier"]);
        Assert.Equal("Test-husstand", top["PayloadOrganization"]);
        Assert.Contains("Børne-iPad Æble", (string)top["PayloadDisplayName"]);

        var p12 = Payloads(top).Single(p => (string)p["PayloadType"] == "com.apple.security.pkcs12");
        var mdm = Payloads(top).Single(p => (string)p["PayloadType"] == "com.apple.mdm");
        Assert.Equal(true, mdm["SignMessage"]);
        Assert.Equal(8191L, mdm["AccessRights"]);
        Assert.Equal(true, mdm["CheckOutWhenRemoved"]);
        Assert.Equal("com.apple.mgmt.External.11111111-2222-3333-4444-555555555555", mdm["Topic"]);
        Assert.Equal("https://mdm.example.org/mdm?fd=key_123-abc", mdm["ServerURL"]);
        Assert.Equal(mdm["ServerURL"], mdm["CheckInURL"]);
        Assert.Equal(p12["PayloadUUID"], mdm["IdentityCertificateUUID"]);
        Assert.Equal(id.Password, p12["Password"]);
        Assert.Equal(id.Pkcs12, p12["PayloadContent"]);
        // alle payloads har id under profilens og unikt UUID
        var all = Payloads(top);
        Assert.All(all, p => Assert.StartsWith("dk.fleetdeck.mdm.fdk7pm23.", (string)p["PayloadIdentifier"]));
        Assert.Equal(all.Count, all.Select(p => p["PayloadUUID"]).Distinct().Count());
    }

    [Fact]
    public void Device_identity_is_rsa_pkcs12_chained_to_the_ca()
    {
        var ca = MdmPki.CreateCa("Hjemme");
        var id = MdmPki.IssueDevice(ca.Pem, ca.KeyPem, 12);
        Assert.StartsWith("fd-dev-12-", id.CommonName);
        Assert.True(id.NotAfter > DateTime.UtcNow.AddYears(4));
        using var leaf = X509CertificateLoader.LoadPkcs12(id.Pkcs12, id.Password, X509KeyStorageFlags.Exportable);
        Assert.True(leaf.HasPrivateKey);
        using var rsa = leaf.GetRSAPublicKey();
        Assert.Equal(2048, rsa!.KeySize);
        Assert.Equal($"CN={id.CommonName}", leaf.Subject);
        Assert.Equal(id.Serial, leaf.SerialNumber);
        Assert.Contains(leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.2");

        using var root = X509Certificate2.CreateFromPem(ca.Pem);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(leaf), string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));
        // en anden CA godkender den ikke
        var other = MdmPki.CreateCa("Andet");
        using var otherRoot = X509Certificate2.CreateFromPem(other.Pem);
        using var chain2 = new X509Chain();
        chain2.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain2.ChainPolicy.CustomTrustStore.Add(otherRoot);
        chain2.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.False(chain2.Build(leaf));
    }

    [Fact]
    public void Each_identity_gets_its_own_password_and_serial()
    {
        var ca = MdmPki.CreateCa("");
        var a = MdmPki.IssueDevice(ca.Pem, ca.KeyPem, 1);
        var b = MdmPki.IssueDevice(ca.Pem, ca.KeyPem, 1);
        Assert.NotEqual(a.Password, b.Password);
        Assert.NotEqual(a.Serial, b.Serial);
    }

    [Fact]
    public void Ca_is_a_ca_and_lives_ten_years()
    {
        var ca = MdmPki.CreateCa("Familie, \"Test\"");
        using var c = X509Certificate2.CreateFromPem(ca.Pem);
        Assert.True(c.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.True(c.NotAfter > DateTime.UtcNow.AddYears(9));
        Assert.Contains("O=Familie Test", c.Subject);   // tegn, der kan bryde et DN, fjernes
    }

    [Fact]
    public void Push_csr_is_rsa2048_and_signed()
    {
        var r = MdmPki.PushCsr("Husstand");
        Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", r.Pem);
        var req = CertificateRequest.LoadSigningRequestPem(r.Pem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default);
        Assert.Contains("CN=Fleetdeck MDM push", req.SubjectName.Name);
        using var key = RSA.Create();
        key.ImportFromPem(r.KeyPem);
        Assert.Equal(2048, key.KeySize);
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), req.PublicKey.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Et "Apple"-certifikat til nøglen fra CSR'en, med topic i UID.</summary>
    private static string FakeApplePem(string keyPem, string? uid = "com.apple.mgmt.External.abcd")
    {
        using var issuer = RSA.Create(2048);
        using var key = RSA.Create();
        key.ImportFromPem(keyPem);
        var dn = new X500DistinguishedNameBuilder();
        if (uid is not null) dn.Add(MdmPki.UidOid, uid, UniversalTagNumber.UTF8String);
        dn.AddCommonName("APSP:abcd");
        var req = new CertificateRequest(dn.Build(), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var issuerReq = new CertificateRequest("CN=Apple Application Integration 2", issuer, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var issuerCert = issuerReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        using var cert = req.Create(issuerCert.SubjectName, X509SignatureGenerator.CreateForRSA(issuer, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365), [1, 2, 3, 4]);
        return cert.ExportCertificatePem();
    }

    [Fact]
    public void Push_cert_is_matched_to_key_and_topic_is_read()
    {
        var csr = MdmPki.PushCsr("");
        var info = MdmPki.ParsePushCert("Bag Attributes\n" + FakeApplePem(csr.KeyPem), csr.KeyPem);
        Assert.Equal("com.apple.mgmt.External.abcd", info.Topic);
        Assert.InRange(info.NotAfter, DateTime.UtcNow.AddDays(360), DateTime.UtcNow.AddDays(366));

        var other = MdmPki.PushCsr("");
        Assert.Contains("passer ikke", Assert.Throws<FormatException>(() => MdmPki.ParsePushCert(FakeApplePem(csr.KeyPem), other.KeyPem)).Message);
        Assert.Throws<FormatException>(() => MdmPki.ParsePushCert(FakeApplePem(csr.KeyPem, null), csr.KeyPem));
        Assert.Throws<FormatException>(() => MdmPki.ParsePushCert(FakeApplePem(csr.KeyPem, "com.example.other"), csr.KeyPem));
        Assert.Throws<FormatException>(() => MdmPki.ParsePushCert("ingen pem her", csr.KeyPem));
    }

    /// <summary>Byg en PKCS#7 EnvelopedData som mdmcert.download (RSA PKCS#1 v1.5 + 3DES-CBC eller AES-CBC).</summary>
    private static byte[] Envelope(byte[] content, string certPem, bool aes, bool constructed)
    {
        using var cert = X509Certificate2.CreateFromPem(certPem);
        using var rsa = cert.GetRSAPublicKey()!;
        using SymmetricAlgorithm sym = aes ? Aes.Create() : TripleDES.Create();
        sym.GenerateKey(); sym.GenerateIV();
        var enc = sym.EncryptCbc(content, sym.IV, PaddingMode.PKCS7);
        var w = new AsnWriter(AsnEncodingRules.BER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier("1.2.840.113549.1.7.3");
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (w.PushSequence())
            {
                w.WriteInteger(0);
                using (w.PushSetOf())
                using (w.PushSequence())
                {
                    w.WriteInteger(0);
                    w.WriteEncodedValue(Seq(cert.IssuerName.RawData, cert.SerialNumberBytes.ToArray()));
                    using (w.PushSequence()) { w.WriteObjectIdentifier("1.2.840.113549.1.1.1"); w.WriteNull(); }
                    w.WriteOctetString(rsa.Encrypt(sym.Key, RSAEncryptionPadding.Pkcs1));
                }
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier("1.2.840.113549.1.7.1");
                    using (w.PushSequence()) { w.WriteObjectIdentifier(aes ? "2.16.840.1.101.3.4.1.42" : "1.2.840.113549.3.7"); w.WriteOctetString(sym.IV); }
                    if (constructed)
                        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                        {
                            w.WriteOctetString(enc.AsSpan(0, 8));
                            w.WriteOctetString(enc.AsSpan(8));
                        }
                    else w.WriteOctetString(enc, new Asn1Tag(TagClass.ContextSpecific, 0));
                }
            }
        }
        return w.Encode();

        static byte[] Seq(byte[] issuer, byte[] serialLittleEndian)
        {
            var s = new AsnWriter(AsnEncodingRules.DER);
            using (s.PushSequence())
            {
                s.WriteEncodedValue(issuer);
                s.WriteInteger(new System.Numerics.BigInteger(serialLittleEndian, isUnsigned: true));
            }
            return s.Encode();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Mdmcert_reply_is_decrypted(bool aes, bool constructed)
    {
        var pair = MdmPki.EncryptionPair();
        var request = Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes("<plist>underskrevet anmodning</plist>")));
        var hex = Convert.ToHexStringLower(Envelope(request, pair.Pem, aes, constructed));
        Assert.Equal(request, MdmPki.DecryptSignedRequest(hex + "\n", pair.Pem, pair.KeyPem));
        // til en anden nøgle: tydelig fejl
        var other = MdmPki.EncryptionPair();
        Assert.Throws<FormatException>(() => MdmPki.DecryptSignedRequest(hex, other.Pem, other.KeyPem));
        Assert.Throws<FormatException>(() => MdmPki.DecryptSignedRequest("ikke en p7-fil", pair.Pem, pair.KeyPem));
    }

    [Fact]
    public void Locked_profile_via_mdm_has_same_payloads_without_removal_lock()
    {
        var p = Pad();
        var stat = Plist.ParseDict(LockedProfile.Build(p, "dns.example.org", "abcd-efgh", 4));
        var mdm = Plist.ParseDict(LockedProfile.Build(p, "dns.example.org", "", 4, mdm: true));
        Assert.Equal(stat["PayloadIdentifier"], mdm["PayloadIdentifier"]);   // én profil-id i iPad'ens levetid
        Assert.False(mdm.ContainsKey("PayloadRemovalDisallowed"));
        Assert.DoesNotContain(Payloads(mdm), x => (string)x["PayloadType"] == "com.apple.profileRemovalPassword");
        foreach (var type in new[] { "com.apple.dnsSettings.managed", "com.apple.applicationaccess" })
        {
            var a = Payloads(stat).Single(x => (string)x["PayloadType"] == type);
            var b = Payloads(mdm).Single(x => (string)x["PayloadType"] == type);
            Assert.Equal(Plist.Write(ToPDict(a)), Plist.Write(ToPDict(b)));
        }
    }

    private static PDict ToPDict(Dictionary<string, object> d)
    {
        var o = new PDict();
        foreach (var (k, v) in d.OrderBy(x => x.Key, StringComparer.Ordinal)) o.Add(k, v is Dictionary<string, object> dd ? ToPDict(dd) : v);
        return o;
    }

    [Fact]
    public void Commands_have_uuid_and_request_type()
    {
        var c = MdmCommands.DeviceLock("  Ring til mor  ", "12345678");
        var d = Plist.ParseDict(c.Xml);
        Assert.Equal(c.Uuid, d["CommandUUID"]);
        var cmd = (Dictionary<string, object>)d["Command"];
        Assert.Equal("DeviceLock", cmd["RequestType"]);
        Assert.Equal("Ring til mor", cmd["Message"]);
        Assert.Equal("12345678", cmd["PhoneNumber"]);
        Assert.False(((Dictionary<string, object>)Plist.ParseDict(MdmCommands.DeviceLock(null, " ").Xml)["Command"]).ContainsKey("Message"));

        var info = (Dictionary<string, object>)Plist.ParseDict(MdmCommands.DeviceInformation().Xml)["Command"];
        Assert.Contains("BatteryLevel", (List<object>)info["Queries"]);
        var inst = (Dictionary<string, object>)Plist.ParseDict(MdmCommands.InstallProfile("<plist/>").Xml)["Command"];
        Assert.Equal("<plist/>"u8.ToArray(), inst["Payload"]);
        Assert.NotEqual(MdmCommands.EraseDevice().Uuid, MdmCommands.EraseDevice().Uuid);
    }

    [Fact]
    public void Os_update_declaration_uses_local_deadline_without_zone()
    {
        var decl = System.Text.Json.JsonSerializer.Serialize(MdmDeclarations.OsUpdate(Pad(), "26.1", "23B85", new DateTime(2026, 12, 1, 20, 0, 0)));
        Assert.Contains("\"Type\":\"com.apple.configuration.softwareupdate.enforcement.specific\"", decl);
        Assert.Contains("\"TargetOSVersion\":\"26.1\"", decl);
        Assert.Contains("\"TargetLocalDateTime\":\"2026-12-01T20:00:00\"", decl);
        Assert.Contains("\"TargetBuildVersion\":\"23B85\"", decl);
        Assert.Null(MdmDeclarations.ValidateVersion("26.1.2"));
        Assert.NotNull(MdmDeclarations.ValidateVersion("26"));
        Assert.NotNull(MdmDeclarations.ValidateVersion("26.1; rm"));
        Assert.NotNull(MdmDeclarations.ValidateBuild("23B 85"));
    }
}

public class MdmWebhookTests
{
    private static string Ev(string topic, string kind, string payloadXml, string extra = "") =>
        $$$"""
        {"topic":"{{{topic}}}","event_id":"e1","created_at":"2026-11-17T20:00:00Z",
         "{{{kind}}}":{"udid":"UDID-1","enrollment_id":"UDID-1","url_params":{"fd":"key_1"}{{{extra}}},
         "raw_payload":"{{{Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadXml))}}}"}}
        """;

    private const string AuthPayload = """
        <?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict>
        <key>MessageType</key><string>Authenticate</string><key>UDID</key><string>UDID-1</string>
        <key>SerialNumber</key><string>DMPXYZ123</string><key>DeviceName</key><string>Søsters iPad – æøå</string>
        <key>OSVersion</key><string>26.1</string><key>BuildVersion</key><string>23B85</string><key>ProductName</key><string>iPad14,1</string>
        <key>Topic</key><string>com.apple.mgmt.External.abcd</string></dict></plist>
        """;

    [Fact]
    public void Authenticate_event_is_parsed_with_url_params_and_unicode()
    {
        var e = MdmHook.Parse(Ev(MdmHook.Authenticate, "checkin_event", AuthPayload));
        Assert.Equal("mdm.Authenticate", e.Topic);
        Assert.Equal("key_1", e.EnrollKey);
        Assert.Equal("UDID-1", e.Udid);
        Assert.Equal("UDID-1", e.EnrollmentId);
        var d = new MdmDevice();
        MdmHook.ApplyCheckin(d, e.Payload!);
        Assert.Equal("Søsters iPad – æøå", d.DeviceName);
        Assert.Equal("DMPXYZ123", d.Serial);
        Assert.Equal("26.1", d.OsVersion);
        Assert.Equal("iPad14,1", d.ProductName);
    }

    [Fact]
    public void TokenUpdate_tally_and_missing_params()
    {
        var json = $$$"""{"topic":"mdm.TokenUpdate","checkin_event":{"udid":"U2","token_update_tally":1,"raw_payload":"{{{Convert.ToBase64String("not a plist"u8.ToArray())}}}"}}""";
        var e = MdmHook.Parse(json);
        Assert.Equal(1, e.TokenUpdateTally);
        Assert.Null(e.EnrollKey);
        Assert.Null(e.Payload);   // en ulæselig plist vælter ikke hændelsen
        Assert.Equal("U2", e.Udid);
    }

    [Fact]
    public void DeviceInformation_result_updates_the_device()
    {
        var report = """
            <?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict>
            <key>CommandUUID</key><string>CMD-1</string><key>Status</key><string>Acknowledged</string><key>UDID</key><string>UDID-1</string>
            <key>QueryResponses</key><dict>
              <key>DeviceName</key><string>Æblets iPad</string><key>OSVersion</key><string>26.2</string><key>BuildVersion</key><string>23C55</string>
              <key>ModelName</key><string>iPad</string><key>SerialNumber</key><string>DMPXYZ123</string>
              <key>DeviceCapacity</key><real>119.2</real><key>AvailableDeviceCapacity</key><real>80.55</real>
              <key>BatteryLevel</key><real>0.81</real><key>IsSupervised</key><true/><key>IsActivationLockEnabled</key><false/>
            </dict></dict></plist>
            """;
        var e = MdmHook.Parse(Ev(MdmHook.Connect, "acknowledge_event", report, ""","status":"Acknowledged","command_uuid":"CMD-1" """));
        Assert.Equal("CMD-1", e.CommandUuid);
        Assert.Equal("Acknowledged", e.Status);
        var d = new MdmDevice();
        var cmd = new MdmCommand { CommandUuid = "CMD-1", RequestType = "DeviceInformation", Status = MdmCommandStates.Sent };
        var now = DateTime.UtcNow;
        MdmService.ApplyResult(d, cmd, e.Status, e.Payload, now);
        Assert.Equal(MdmCommandStates.Acknowledged, cmd.Status);
        Assert.Equal(now, cmd.Completed);
        Assert.Equal("Æblets iPad", d.DeviceName);
        Assert.Equal("26.2", d.OsVersion);
        Assert.Equal(119.2, d.StorageTotalGb);
        Assert.Equal(80.6, d.StorageFreeGb);
        Assert.Equal(0.81, d.BatteryLevel);
        Assert.True(d.Supervised);
        Assert.False(d.ActivationLock);
        Assert.Equal(now, d.LastInventory);
        Assert.Contains("Æblets iPad", System.Text.RegularExpressions.Regex.Unescape(cmd.ResultJson!));
        // samme svar igen ændrer intet (idempotent)
        MdmService.ApplyResult(d, cmd, e.Status, e.Payload, now.AddMinutes(1));
        Assert.Equal(now, cmd.Completed);
    }

    [Fact]
    public void Error_and_notnow_results()
    {
        var report = Plist.ParseDict(Plist.Write(new PDict
        {
            { "Status", "Error" }, { "CommandUUID", "C2" },
            { "ErrorChain", new List<object> { new PDict { { "ErrorCode", 4001 }, { "ErrorDomain", "MCProfileErrorDomain" }, { "LocalizedDescription", "Profilen kunne ikke installeres" } } } },
        }));
        var d = new MdmDevice();
        var cmd = new MdmCommand { RequestType = "InstallProfile", Status = MdmCommandStates.Sent, Detail = "Låst profil v3", PayloadHash = "h3" };
        MdmService.ApplyResult(d, cmd, "NotNow", null, DateTime.UtcNow);
        Assert.Equal(MdmCommandStates.NotNow, cmd.Status);
        MdmService.ApplyResult(d, cmd, "Error", report, DateTime.UtcNow);
        Assert.Equal(MdmCommandStates.Error, cmd.Status);
        Assert.Equal("Profilen kunne ikke installeres", cmd.Error);
        Assert.Null(d.LockedInstalledHash);
        // "Idle" er ikke et svar på en kommando
        var c2 = new MdmCommand { Status = MdmCommandStates.Sent };
        MdmService.ApplyResult(d, c2, "Idle", null, DateTime.UtcNow);
        Assert.Equal(MdmCommandStates.Sent, c2.Status);
    }

    [Fact]
    public void Locked_profile_install_and_removal_are_tracked()
    {
        var d = new MdmDevice();
        MdmService.ApplyResult(d, new MdmCommand { RequestType = "InstallProfile", Detail = "Låst profil v5", PayloadHash = "abc" }, "Acknowledged", null, DateTime.UtcNow);
        Assert.Equal("abc", d.LockedInstalledHash);
        Assert.Equal(5, d.LockedInstalledVersion);
        MdmService.ApplyResult(d, new MdmCommand { RequestType = "RemoveProfile", Detail = MdmService.LockedRemoved }, "Acknowledged", null, DateTime.UtcNow);
        Assert.Null(d.LockedInstalledHash);
        Assert.Null(d.LockedInstalledVersion);
    }

    [Fact]
    public void Hmac_signature_is_checked()
    {
        var body = Encoding.UTF8.GetBytes("""{"topic":"mdm.Connect"}""");
        var sig = Convert.ToBase64String(HMACSHA256.HashData("hemmelig"u8.ToArray(), body));
        Assert.True(MdmHook.HmacValid(body, "hemmelig", sig));
        Assert.False(MdmHook.HmacValid(body, "forkert", sig));
        Assert.False(MdmHook.HmacValid(body, "hemmelig", null));
        Assert.False(MdmHook.HmacValid(body, "hemmelig", "ikke base64!"));
        Assert.False(MdmHook.HmacValid([.. body, 32], "hemmelig", sig));
    }

    [Fact]
    public void Invalid_json_is_refused()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => MdmHook.Parse("ikke json"));
        Assert.Throws<FormatException>(() => MdmHook.Parse("""{"event_id":"x"}"""));
    }

    [Fact]
    public void NanoHub_results_are_parsed()
    {
        var r = NanoHubClient.ParseResult(207, """{"status":{"A":{"push_result":"apns-id"},"B":{"push_error":"no push info"}},"command_uuid":"C","request_type":"DeviceLock"}""");
        Assert.Equal("C", r.CommandUuid);
        Assert.Equal("apns-id", r.PerId["A"].PushResult);
        Assert.Equal("no push info", r.PerId["B"].PushError);
        var vals = NanoHubClient.ParseStatusValues("""{"UDID-1":[{"path":".StatusItems.softwareupdate.install-state","value":"downloading","status_received":"2026-11-17T20:00:00Z"}]}""");
        Assert.Equal(".StatusItems.softwareupdate.install-state", vals.Single().Path);
        Assert.Equal("downloading", vals.Single().Value);
    }

    [Fact]
    public void Push_expiry_warns_at_30_14_7_1_and_0_days()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(MdmEngine.ExpiryThreshold(now.AddDays(45), now));
        Assert.Equal(30, MdmEngine.ExpiryThreshold(now.AddDays(29), now));
        Assert.Equal(14, MdmEngine.ExpiryThreshold(now.AddDays(10), now));
        Assert.Equal(7, MdmEngine.ExpiryThreshold(now.AddDays(7), now));
        Assert.Equal(1, MdmEngine.ExpiryThreshold(now.AddHours(20), now));
        Assert.Equal(0, MdmEngine.ExpiryThreshold(now.AddHours(-1), now));
    }
}

public class MdmPermissionTests
{
    private static FamilyScope Parent(params string[] perms) => new(false, false, [3], [.. perms]);

    [Theory]
    [InlineData(MdmPerms.Refresh, FamilyPerms.Update)]
    [InlineData(MdmPerms.OsUpdate, FamilyPerms.Update)]
    [InlineData(MdmPerms.Lock, FamilyPerms.Lock)]
    [InlineData(MdmPerms.Erase, FamilyPerms.Erase)]
    [InlineData(MdmPerms.LockedProfile, FamilyPerms.Rules)]
    public void Actions_map_to_family_permissions(string action, string perm)
    {
        Assert.Equal(perm, MdmPerms.For(action));
        // forældre kan kun styre barnet (pause, regler) – enheden (opdatering, lås, sletning) er administratorens, også hvis de har fået lov før
        Assert.Equal(FamilyPerms.Parent.Contains(perm) ? null : 403, MdmPerms.Check(Parent(perm), 3, action)?.Status);
        Assert.Equal(403, MdmPerms.Check(Parent(FamilyPerms.All.Where(x => x != perm).ToArray()), 3, action)?.Status);
    }

    [Fact]
    public void Parent_cannot_reach_other_ipads_and_admin_can_do_all()
    {
        Assert.Equal(404, MdmPerms.Check(Parent(FamilyPerms.All), 4, MdmPerms.Lock)?.Status);
        var admin = new FamilyScope(true, true, [], [.. FamilyPerms.All]);
        foreach (var a in new[] { MdmPerms.Refresh, MdmPerms.Lock, MdmPerms.Erase, MdmPerms.LockedProfile, MdmPerms.OsUpdate }) Assert.Null(MdmPerms.Check(admin, 99, a));
        Assert.Throws<ArgumentException>(() => MdmPerms.For("noget-andet"));
    }

    [Fact]
    public void Parents_only_ever_get_child_control()
    {
        var p = Parent(FamilyPerms.All);
        Assert.Equal(FamilyPerms.Parent, p.PermList);
        Assert.True(p.Can(FamilyPerms.Pause)); Assert.True(p.Can(FamilyPerms.Rules));
        Assert.False(p.Can(FamilyPerms.Update)); Assert.False(p.Can(FamilyPerms.Lock)); Assert.False(p.Can(FamilyPerms.Erase));
    }

    [Fact]
    public void Erase_is_not_a_default_parent_permission() => Assert.DoesNotContain(FamilyPerms.Erase, FamilyPerms.Default);
}

public class MdmSidecarTests
{
    private static MdmSidecarConfig Cfg(string ip = "192.168.1.60") => new(1, "br0", ip, "fleetdeck-mdm", MdmSidecar.Image, "");

    [Fact]
    public void Start_job_never_contains_secrets_and_fetches_them_with_a_nonce()
    {
        var ca = MdmPki.CreateCa("");
        var job = MdmSidecar.StartJob(Cfg(), "Nonce_1234567890abcdef", ca.Pem);
        Assert.Contains(MdmSidecar.SecretsPath, job.Body);
        Assert.Contains("Nonce_1234567890abcdef", job.Body);
        Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", job.Body);
        Assert.DoesNotContain(ca.KeyPem.Split('\n')[1], job.Body);
        Assert.Contains("-e NANOHUB_API_KEY -e NANOHUB_WEBHOOK_URL", job.Body);   // værdierne kommer fra miljøet, ikke kommandolinjen
        Assert.Contains("NANOHUB_CA=", job.Body);
        Assert.Contains(MdmSidecar.Image, job.Body);
        Assert.Contains("--ip \"$IP\"", job.Body);
    }

    [Fact]
    public void Jobs_refuse_invalid_input()
    {
        var ca = MdmPki.CreateCa("").Pem;
        Assert.Throws<ArgumentException>(() => MdmSidecar.StartJob(Cfg("192.168.1.300"), "Nonce_1234567890abcdef", ca));
        Assert.Throws<ArgumentException>(() => MdmSidecar.StartJob(Cfg() with { Image = "x; rm -rf /" }, "Nonce_1234567890abcdef", ca));
        Assert.Throws<ArgumentException>(() => MdmSidecar.StartJob(Cfg() with { DataDir = "/mnt/../etc" }, "Nonce_1234567890abcdef", ca));
        Assert.Throws<ArgumentException>(() => MdmSidecar.StartJob(Cfg(), "kort", ca));
        Assert.Throws<ArgumentException>(() => MdmSidecar.StartJob(Cfg(), "Nonce_1234567890abcdef", "ikke et cert"));
        Assert.Throws<ArgumentException>(() => MdmSidecar.StopJob(Cfg() with { Container = "a b" }));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("/mnt/user/appdata/fleetdeck-mdm", true)]
    [InlineData("relativ/sti", false)]
    [InlineData("/", false)]
    [InlineData("/a/../b", false)]
    [InlineData("/a b", false)]
    public void Data_dir_is_validated(string dir, bool ok) => Assert.Equal(ok, MdmSidecar.ValidateDataDir(dir) is null);

    [Fact]
    public void Image_is_pinned_by_digest() => Assert.Matches(@"@sha256:[0-9a-f]{64}$", MdmSidecar.Image);

    [Fact]
    public void Webhook_secret_and_api_key_are_masked()
    {
        var t = MdmSidecar.Mask("POST http://192.168.1.5:8585/api/mdm/hook/AbC_123-xyz failed; key=supersecretkey", "supersecretkey");
        Assert.DoesNotContain("AbC_123-xyz", t);
        Assert.DoesNotContain("supersecretkey", t);
    }

    [Fact]
    public void Public_host_only_allows_enrollment_paths()
    {
        Assert.True(MdmPublicHost.Allowed("/enroll/abc"));
        Assert.False(MdmPublicHost.Allowed("/api/health"));
        Assert.False(MdmPublicHost.Allowed("/api/mdm/hook/x"));
        Assert.False(MdmPublicHost.Allowed("/"));
        Assert.False(MdmPublicHost.Allowed("/enrollx"));
    }
}
