using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleetdeck.Shared;

/// <summary>
/// Signeret check-in-svar. <see cref="Payload"/> er den nøjagtige JSON-tekst, der er signeret (den kanoniske form):
/// agenten verificerer bytes, før den læser noget af indholdet, og læser derefter kun indholdet herfra.
/// Så afhænger signaturen aldrig af, hvordan en ældre eller nyere agent serialiserer felterne igen.
/// </summary>
public sealed class CommandEnvelope
{
    public string Payload { get; set; } = "";
    /// <summary>ECDSA P-256 / SHA-256 over <see cref="CommandSigning.Domain"/> + Payload (IEEE P1363, base64).</summary>
    public string Signature { get; set; } = "";
    /// <summary>Fingeraftryk af nøglen (kun til log/visning).</summary>
    public string KeyId { get; set; } = "";
    /// <summary>Serverens offentlige jobnøgle (PEM): bruges kun første gang (trust-on-first-use) af agenter uden pinnet nøgle.</summary>
    public string PublicKey { get; set; } = "";
}

/// <summary>Det signerede indhold: alt i svaret, der får agenten til at gøre noget.</summary>
public sealed class SignedCommands
{
    public int V { get; set; } = 1;
    /// <summary>Maskinens id, som det står i agentens token (så et svar til én maskine ikke kan bruges på en anden).</summary>
    public int MachineId { get; set; }
    /// <summary>Agentens engangs-nonce fra check-in (et gammelt svar kan ikke afspilles igen).</summary>
    public string Nonce { get; set; } = "";
    public DateTime Issued { get; set; }
    public DateTime Expires { get; set; }
    public List<AgentJob> Jobs { get; set; } = [];
    /// <summary>Selvopdatering: version (12 tegn) og den fulde SHA-256 af agentfilen, som agenten skal tjekke den hentede fil mod.</summary>
    public string AgentVersion { get; set; } = "";
    public string? AgentSha256 { get; set; }
    public string ServerUrl { get; set; } = "";
    /// <summary>Fastlåst TLS-nøgle (SPKI SHA-256, base64) for https-adressen – til selvsignerede certifikater.</summary>
    public string? TlsPin { get; set; }
    public List<string> KeepRunning { get; set; } = [];
    public List<DriveSpec> Drives { get; set; } = [];
    public ShareInfo? Share { get; set; }
    /// <summary>Mac: "Hold vågen på strøm" (null = ikke en Mac).</summary>
    public KeepAwakeSetting? KeepAwake { get; set; }
    /// <summary>Printere, maskinen skal have (null = ældre server).</summary>
    public List<PrinterSpec>? Printers { get; set; }
    /// <summary>Terminaler, agenten skal åbne. Kun her: et usigneret svar kan aldrig starte en skal.</summary>
    public List<TerminalTicket>? Terminals { get; set; }
    /// <summary>Live-log til siden (kun til agenter, der melder <see cref="LiveLogPolicy.Capability"/>). Null = ingen.</summary>
    public List<LiveLogTicket>? LiveLogs { get; set; }
    /// <summary>Nye beskeder til brugeren (Beskeder/helpdesk). Null = ingen.</summary>
    public List<HelpdeskNote>? Helpdesk { get; set; }
}

/// <summary>Agentens melding om tillid ved check-in (vises pr. maskine i Fleetdeck).</summary>
public sealed class AgentTrustReport
{
    /// <summary>Agenten har pinnet serverens jobnøgle og afviser usignerede jobs.</summary>
    public bool JobKeyPinned { get; set; }
    public string? JobKeyId { get; set; }
    /// <summary>Seneste afvisning (fx "usigneret svar"), null = ingen.</summary>
    public string? Error { get; set; }
    public bool TlsPinned { get; set; }
}

public static class SignedJobsMode
{
    /// <summary>off: serveren signerer ikke. sign: serveren signerer, agenter med pinnet nøgle kræver det. enforce: jobs leveres kun til agenter med pinnet nøgle.</summary>
    public const string Off = "off", Sign = "sign", Enforce = "enforce";
    public static string Normalize(string? v) => v is Off or Enforce ? v : Sign;
}

public static class CommandSigning
{
    /// <summary>Domæneadskillelse: en signatur over et check-in-svar kan aldrig forveksles med andre signaturer.</summary>
    public const string Domain = "fleetdeck-commands-v1\n";
    /// <summary>Hvor længe et signeret svar gælder.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    /// <summary>Tilladt forskel på urene, når udløbet tjekkes.</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static byte[] Message(string payload) => Encoding.UTF8.GetBytes(Domain + payload);

    public static CommandEnvelope Sign(ECDsa key, string publicPem, SignedCommands c)
    {
        var payload = JsonSerializer.Serialize(c, Json);
        return new CommandEnvelope
        {
            Payload = payload,
            Signature = Convert.ToBase64String(key.SignData(Message(payload), HashAlgorithmName.SHA256)),
            KeyId = AgentSigning.Fingerprint(publicPem),
            PublicKey = publicPem,
        };
    }

    public sealed record Result(bool Ok, string Why, SignedCommands? Commands)
    {
        public static Result Fail(string why) => new(false, why, null);
    }

    /// <summary>
    /// Tjek et signeret svar mod den pinnede nøgle. Afviser: manglende/forkert signatur, anden maskine,
    /// forkert nonce (gentaget svar) og – når der ikke er en nonce at binde til – udløbet svar.
    /// Et svar, der bærer agentens egen friske nonce, er beviseligt nyt; så afvises det ikke på grund af et forkert ur på maskinen.
    /// </summary>
    public static Result Verify(string publicPem, CommandEnvelope? env, int machineId, string? expectedNonce, DateTime nowUtc)
    {
        if (env is null || string.IsNullOrEmpty(env.Payload) || string.IsNullOrEmpty(env.Signature)) return Result.Fail("svaret er ikke signeret");
        try
        {
            using var k = ECDsa.Create();
            k.ImportFromPem(publicPem);
            if (!k.VerifyData(Message(env.Payload), Convert.FromBase64String(env.Signature), HashAlgorithmName.SHA256))
                return Result.Fail("signaturen passer ikke med serverens nøgle");
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return Result.Fail("signaturen kunne ikke læses"); }
        SignedCommands? c;
        try { c = JsonSerializer.Deserialize<SignedCommands>(env.Payload, Json); }
        catch (JsonException) { return Result.Fail("indholdet kunne ikke læses"); }
        if (c is null || c.V < 1) return Result.Fail("indholdet kunne ikke læses");
        if (c.MachineId != machineId) return Result.Fail($"svaret er til en anden maskine ({c.MachineId})");
        var fresh = false;
        if (expectedNonce is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c.Nonce), Encoding.UTF8.GetBytes(expectedNonce)))
                return Result.Fail("svaret er gentaget (nonce passer ikke)");
            fresh = true;
        }
        if (!fresh && (c.Expires < c.Issued || nowUtc > c.Expires + Skew)) return Result.Fail("svaret er udløbet");
        return new(true, "", c);
    }

    /// <summary>Ny nonce til et check-in (128 bit).</summary>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>Maskinens id fra tokenet "&lt;id&gt;.&lt;hemmelighed&gt;" (0 = ugyldigt).</summary>
    public static int TokenId(string? token) =>
        token is { } t && t.IndexOf('.') is var dot and > 0 && int.TryParse(t[..dot], out var id) ? id : 0;
}

/// <summary>Agentens husker for tillid til serveren (gemt i agentens konfiguration).</summary>
public interface ITrustStore
{
    /// <summary>Pinnet offentlig jobnøgle (PEM). Sat = agenten kræver signerede jobs.</summary>
    string? JobKey { get; set; }
    string? TlsPin { get; set; }
}

/// <summary>
/// Agentens port for alt, der kommer fra serveren ved check-in. Uden pinnet nøgle opfører den sig som før (gamle servere virker).
/// Med pinnet nøgle bruges kun det signerede indhold; er det usigneret eller ugyldigt, fjernes jobs, opdatering,
/// serverflytning, drev og tjenester fra svaret, og årsagen meldes (log, ikon og Fleetdeck).
/// </summary>
public static class CommandGate
{
    public sealed record Outcome(bool Trusted, bool Pinned, bool ConfigChanged, string? Error, string? AgentSha256, string? TlsPin)
    {
        /// <summary>Terminaler fra et svar, der er bekræftet med den allerede pinnede nøgle. Aldrig fra første kontakt (trust on first use).</summary>
        public IReadOnlyList<TerminalTicket> Terminals { get; init; } = [];
        /// <summary>Live-log fra samme slags svar (aldrig fra første kontakt).</summary>
        public IReadOnlyList<LiveLogTicket> LiveLogs { get; init; } = [];
    }

    public static Outcome Apply(CheckinResponse r, ITrustStore store, int machineId, string nonce, DateTime nowUtc)
    {
        var env = r.Signed;
        if (store.JobKey is not { Length: > 0 } pinned)
        {
            if (env is null) return new(true, false, false, null, null, null);                 // server uden signering: som før
            // Trust on first use: nøglen i svaret, men kun hvis svaret også er gyldigt signeret med den
            if (env.PublicKey is not { Length: > 0 } offered) return new(true, false, false, null, null, null);
            var first = CommandSigning.Verify(offered, env, machineId, nonce, nowUtc);
            // ikke pinnet endnu: et ugyldigt signeret svar pinner intet, og agenten fortsætter som før (aldrig strandet på en fejl)
            if (!first.Ok) return new(true, false, false, $"Signeret svar kunne ikke bekræftes: {first.Why}", null, null);
            store.JobKey = offered;
            Use(r, first.Commands!);
            return new(true, true, true, null, first.Commands!.AgentSha256, first.Commands.TlsPin);
        }
        var v = CommandSigning.Verify(pinned, env, machineId, nonce, nowUtc);
        if (!v.Ok) { Strip(r); return new(false, true, false, $"Afviste jobs fra serveren: {v.Why}", null, null); }
        Use(r, v.Commands!);
        return new(true, true, false, null, v.Commands!.AgentSha256, v.Commands.TlsPin) { Terminals = v.Commands.Terminals ?? [], LiveLogs = v.Commands.LiveLogs ?? [] };
    }

    private static void Use(CheckinResponse r, SignedCommands c)
    {
        r.Jobs = c.Jobs; r.AgentVersion = c.AgentVersion; r.ServerUrl = c.ServerUrl;
        r.KeepRunning = c.KeepRunning; r.Drives = c.Drives; r.Share = c.Share; r.KeepAwake = c.KeepAwake;
        r.Helpdesk = c.Helpdesk ?? [];
        r.Printers = c.Printers;
    }

    /// <summary>Intet, der får agenten til at handle: ingen jobs, ingen opdatering, ingen flytning.</summary>
    private static void Strip(CheckinResponse r)
    {
        r.Jobs = []; r.AgentVersion = ""; r.ServerUrl = ""; r.KeepRunning = []; r.Checks = []; r.Helpdesk = [];
        r.KeepAwake = null;   // "Hold vågen": agenten beholder den sidst kendte indstilling
        r.Printers = null;    // printere: rør ingenting, når svaret ikke kan bekræftes
    }

    /// <summary>
    /// Selvopdatering: med pinnet nøgle skal den hentede fil passe med den fulde SHA-256 fra det signerede svar.
    /// Uden pinnet nøgle (eller fra en ældre server) gælder den gamle kontrol (filens egen signatur).
    /// </summary>
    public static (bool Ok, string Why) CheckUpdate(string file, bool pinned, string? signedSha256)
    {
        if (signedSha256 is not { Length: 64 })
            return pinned ? (false, "opdateringen er ikke signeret i check-in-svaret") : (true, "");
        string got;
        using (var fs = File.OpenRead(file)) got = Convert.ToHexStringLower(SHA256.HashData(fs));
        return string.Equals(got, signedSha256, StringComparison.OrdinalIgnoreCase) ? (true, "") : (false, "filen passer ikke med den signerede kontrolsum");
    }
}

/// <summary>
/// TLS med selvsigneret certifikat: agenten accepterer et certifikat, som systemet stoler på, eller et, hvis offentlige nøgle
/// passer med det pinnede fingeraftryk (SHA-256 af SubjectPublicKeyInfo, base64 – samme format som curl --pinnedpubkey sha256//…).
/// </summary>
public static class TlsPinning
{
    private static readonly Lock L = new();
    private static readonly HashSet<string> Pins = [];

    /// <summary>De fingeraftryk, der accepteres lige nu (den gemte og evt. en ny under flytning).</summary>
    public static void Set(params string?[] pins)
    {
        lock (L) { Pins.Clear(); foreach (var p in pins) if (p is { Length: > 0 }) Pins.Add(p.Trim()); }
    }
    public static void Add(string? pin) { if (pin is { Length: > 0 }) lock (L) Pins.Add(pin.Trim()); }

    public static string Of(X509Certificate cert)
    {
        // certifikatet fra SslStream må ikke frigives her – kun en kopi, vi selv har lavet
        if (cert is X509Certificate2 c2) return Convert.ToBase64String(SHA256.HashData(c2.PublicKey.ExportSubjectPublicKeyInfo()));
        using var c = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
        return Convert.ToBase64String(SHA256.HashData(c.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Normaliserer input: "sha256//abc=", hex eller base64 → base64. Null ved ugyldigt.</summary>
    public static string? Normalize(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return null;
        var p = pin.Trim();
        if (p.StartsWith("sha256//", StringComparison.OrdinalIgnoreCase)) p = p[8..];
        var hex = p.Replace(":", "");
        if (hex.Length == 64 && hex.All(Uri.IsHexDigit)) return Convert.ToBase64String(Convert.FromHexString(hex));
        try { return Convert.FromBase64String(p).Length == 32 ? p : null; } catch (FormatException) { return null; }
    }

    public static bool Accept(X509Certificate? cert, SslPolicyErrors errors, IReadOnlyCollection<string> pins)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null || pins.Count == 0) return false;
        try { return pins.Contains(Of(cert)); } catch (CryptographicException) { return false; }
    }

    /// <summary>Til HttpClient/WebSocket (RemoteCertificateValidationCallback).</summary>
    public static bool Validate(object? sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        string[] pins; lock (L) pins = [.. Pins];
        return Accept(cert, errors, pins);
    }
}

/// <summary>Ved tilmelding: pin jobnøglen og TLS-fingeraftrykket fra serverens svar (eller behold de gamle ved geninstallation).</summary>
public static class TrustSetup
{
    public static void FromEnroll(ITrustStore cfg, EnrollResponse res, string? cliPin = null)
    {
        if (res.JobKey is { Length: > 0 } k) cfg.JobKey = k.Trim();
        cfg.TlsPin = TlsPinning.Normalize(cliPin) ?? TlsPinning.Normalize(res.TlsPin) ?? cfg.TlsPin;
    }

    /// <summary>Pins fra en tidligere installation på samme maskine (en geninstallation må ikke slippe en pinnet nøgle).</summary>
    public static void Keep(ITrustStore cfg, ITrustStore? old)
    {
        if (old is null) return;
        cfg.JobKey ??= old.JobKey;
        cfg.TlsPin ??= old.TlsPin;
    }

    public static AgentTrustReport Report(ITrustStore cfg, string? error) => new()
    {
        JobKeyPinned = cfg.JobKey is { Length: > 0 },
        JobKeyId = cfg.JobKey is { Length: > 0 } k ? SafeFingerprint(k) : null,
        Error = error,
        TlsPinned = cfg.TlsPin is { Length: > 0 },
    };

    private static string? SafeFingerprint(string pem)
    {
        try { return AgentSigning.Fingerprint(pem); }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return null; }
    }
}
