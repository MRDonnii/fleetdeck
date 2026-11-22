using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleetdeck.Shared;

// "Anmod om adgang": en maskine uden tilmeldingsnøgle beder administratoren om lov. Agenten laver en hemmelighed, sender den
// kun i POST-body og viser en kort kode. Administratoren sammenligner koden med brugeren (telefon/ved siden af), godkender med en
// profil, og agenten får en engangskode, der kun virker sammen med hemmeligheden. Se VEJLEDNING.md "Anmod om adgang".

/// <summary>Tilstande for en anmodning om adgang.</summary>
public static class AccessStates
{
    public const string Pending = "pending", Approved = "approved", Issued = "issued", Enrolled = "enrolled",
        Rejected = "rejected", Expired = "expired", KeySent = "key_sent", Superseded = "superseded", Unknown = "unknown";
    /// <summary>Anmodningen kan stadig ende med en tilmelding (agenten skal blive ved med at spørge).</summary>
    public static bool Open(string s) => s is Pending or Approved or Issued;
}

/// <summary>Hvad en blokering af "Anmod om adgang" gælder: IP-adressen (IPv6 pr. /64), en MAC-adresse eller maskinnavnet.</summary>
public static class AccessBlockKinds
{
    public const string Ip = "ip", Mac = "mac", Name = "name";
    /// <summary>Højst så længe ad gangen (en blokering skal altid udløbe).</summary>
    public const int MaxHours = 24 * 90;

    /// <summary>"aa:bb:cc:dd:ee:ff", "AA-BB-…" eller "aabb.ccdd.eeff" → "AABBCCDDEEFF". Null = ikke en brugbar adresse (fx kun nuller).</summary>
    public static string? NormalizeMac(string? mac)
    {
        if (mac is null) return null;
        var hex = new string([.. mac.Where(char.IsAsciiHexDigit)]).ToUpperInvariant();
        if (hex.Length != 12 || mac.Count(char.IsAsciiLetterOrDigit) != 12) return null;
        return hex is "000000000000" or "FFFFFFFFFFFF" ? null : hex;
    }

    /// <summary>"AABBCCDDEEFF" → "AA:BB:CC:DD:EE:FF" (til visning).</summary>
    public static string ShowMac(string mac) => mac.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))) : mac;

    /// <summary>Maskinens MAC-adresser fra fakta (normaliserede, uden dubletter).</summary>
    public static List<string> Macs(SystemFacts? facts) =>
        [.. (facts?.Nics ?? []).Select(n => NormalizeMac(n.Mac)).OfType<string>().Distinct().Take(16)];
}

/// <summary>Indstillingen <c>access_requests</c>: fra, kun hjemmenet/VPN eller overalt.</summary>
public static class AccessMode
{
    public const string Off = "off", Lan = "lan", On = "on";
    public static string Normalize(string? v) => v?.Trim() is Off or On ? v.Trim() : Lan;
}

/// <summary>
/// Visningskoden, brugeren og administratoren sammenligner (fx "7K4-Q2M"). Commit-then-reveal: klienten sender først
/// <see cref="Commit"/> (hash af hemmelighed og offentlig nøgle), serveren svarer med en tilfældig nonce, og først derefter afslører klienten
/// hemmeligheden. En mellemmand kan derfor ikke prøve sig frem til en hemmelighed med samme kode: begge sider er låst, før han kender
/// den andens bidrag, så hvert forsøg rammer med 1 til 31^6 (og koster en synlig anmodning).
/// </summary>
public static class AccessCode
{
    /// <summary>Ingen tvetydige tegn (0/O, 1/I/L).</summary>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    /// <summary>Koden i agentens output ("Kode: 7K4-Q2M").</summary>
    public const string Pattern = "[A-Z0-9]{3}-[A-Z0-9]{3}";
    private const string Domain = "fleetdeck-access|v2|", CommitDomain = "fleetdeck-access-commit|";

    /// <summary>
    /// 6 tegn af HMAC-SHA256(hemmelighed, nonce | TLS-fingeraftryk | offentlig nøgle). Klienten regner med det fingeraftryk, den selv ser
    /// (tomt ved http); serveren med sit eget. En mellemmand med et andet certifikat eller en anden nøgle giver to forskellige koder.
    /// </summary>
    public static string Display(string secret, string nonce, string? pin, string? publicKey)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret ?? ""),
            Encoding.UTF8.GetBytes($"{Domain}{nonce}|{(pin ?? "").Trim()}|{publicKey ?? ""}"));
        var v = BitConverter.ToUInt64(mac, 0);
        var sb = new StringBuilder(7);
        for (var i = 0; i < 6; i++)
        {
            if (i == 3) sb.Append('-');
            sb.Append(Alphabet[(int)(v % (ulong)Alphabet.Length)]);
            v /= (ulong)Alphabet.Length;
        }
        return sb.ToString();
    }

    /// <summary>Det, klienten binder sig til, før den kender serverens nonce (hex SHA-256).</summary>
    public static string Commit(string secret, string publicKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CommitDomain + secret + "|" + publicKey)));

    /// <summary>Ny hemmelighed (32 tilfældige byte, base64url). Forlader kun maskinen i POST-body.</summary>
    public static string NewSecret() => B64.Url(RandomNumberGenerator.GetBytes(32));
    /// <summary>Serverens bidrag (16 tilfældige byte).</summary>
    public static string NewNonce() => B64.Url(RandomNumberGenerator.GetBytes(16));
}

internal static class B64
{
    public static string Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromUrl(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>
/// Engangskoden fra godkendelsen krypteres til agentens offentlige nøgle (ECDH P-256, nøgle afledt med HKDF, HMAC-SHA256 over det hele),
/// så en, der lytter med (fx over http), hverken kan bruge den eller tilmelde sin egen maskine i stedet.
/// </summary>
public static class AccessSeal
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("fleetdeck-access-seal");

    /// <summary>Nyt nøglepar: offentlig nøgle (SubjectPublicKeyInfo) og privat nøgle (PKCS#8), begge base64.</summary>
    public static (string PublicKey, string PrivateKey) NewKeyPair()
    {
        using var k = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(k.ExportPkcs8PrivateKey()));
    }

    /// <summary>En gyldig P-256-nøgle (højst 200 tegn)?</summary>
    public static bool ValidPublicKey(string? publicKey)
    {
        if (publicKey is not { Length: > 0 and <= 200 }) return false;
        try
        {
            using var k = ECDiffieHellman.Create();
            k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return k.KeySize == 256;
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return false; }
    }

    public static string Seal(string publicKey, string plain, string context)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var epub = eph.ExportSubjectPublicKeyInfo();
        var data = Encoding.UTF8.GetBytes(plain);
        var (pad, macKey) = Keys(eph.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256, null, Encoding.UTF8.GetBytes(context)), epub, data.Length);
        for (var i = 0; i < data.Length; i++) data[i] ^= pad[i];
        var tag = HMACSHA256.HashData(macKey, (byte[])[.. epub, .. data]);
        return $"{B64.Url(epub)}.{B64.Url(data)}.{B64.Url(tag)}";
    }

    /// <summary>Åbner en forseglet engangskode. Forkert nøgle, kontekst eller ændret indhold giver <see cref="CryptographicException"/>.</summary>
    public static string Open(string privateKey, string sealedText, string context)
    {
        var parts = sealedText.Split('.');
        if (parts.Length != 3) throw new CryptographicException("Ugyldig forseglet nøgle");
        byte[] epub, data, tag;
        try { epub = B64.FromUrl(parts[0]); data = B64.FromUrl(parts[1]); tag = B64.FromUrl(parts[2]); }
        catch (FormatException) { throw new CryptographicException("Ugyldig forseglet nøgle"); }
        using var me = ECDiffieHellman.Create();
        me.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(epub, out _);
        var (pad, macKey) = Keys(me.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, null, Encoding.UTF8.GetBytes(context)), epub, data.Length);
        if (!CryptographicOperations.FixedTimeEquals(tag, HMACSHA256.HashData(macKey, (byte[])[.. epub, .. data])))
            throw new CryptographicException("Den forseglede nøgle passer ikke");
        for (var i = 0; i < data.Length; i++) data[i] ^= pad[i];
        return Encoding.UTF8.GetString(data);
    }

    private static (byte[] Pad, byte[] MacKey) Keys(byte[] shared, byte[] salt, int length)
    {
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, length + 32, salt, Info);
        return (okm[..length], okm[length..]);
    }

    /// <summary>Konteksten, nøglen er bundet til (anmodningens id).</summary>
    public static string Context(int requestId) => $"fleetdeck-access-key|{requestId}";
}

/// <param name="Commit">Se <see cref="AccessCode.Commit"/>. Hemmeligheden følger først i <see cref="AccessReveal"/>.</param>
/// <param name="RequestedBy">Den bruger, der beder (fx den indloggede bruger), så administratoren ved, hvem han skal ringe til.</param>
/// <param name="ObservedPin">TLS-fingeraftrykket, klienten så (tom ved http). Bruges kun, hvis serveren ikke kender sit eget.</param>
/// <param name="Replaces">Maskinens egen tidligere anmodning ("Anmod igen"): den lukkes, når den nye er oprettet (hemmeligheden beviser det).</param>
public sealed record AccessRequestCreate(string Commit, string Name, string? Platform, SystemFacts? Facts, string? RequestedBy, string? Message, string? ObservedPin,
    AccessReplace? Replaces = null);
public sealed record AccessReplace(int Id, string Secret);
/// <summary>Svaret på første skridt: et håndtag og serverens nonce. Hemmeligheden skal afsløres inden for <paramref name="RevealSeconds"/>.</summary>
public sealed record AccessRequestStarted(string Handle, string Nonce, int RevealSeconds);
public sealed record AccessReveal(string Handle, string Secret, string PublicKey);
public sealed record AccessRequestCreated(int Id, string Code, DateTime ExpiresAt, int PollSeconds);
public sealed record AccessPollRequest(string Secret);
/// <param name="SealedKey">Kun når anmodningen er godkendt: en engangskode, krypteret til agentens nøgle (<see cref="AccessSeal"/>), der kun virker sammen med hemmeligheden.</param>
/// <param name="Name">Navnet, administratoren har godkendt maskinen under.</param>
public sealed record AccessPollResult(string State, string? SealedKey = null, string? Reason = null, string? Name = null);

/// <summary>Til ikonet/menulinjen og opsætningsvinduet: en anmodning, der venter (ingen hemmeligheder).</summary>
public sealed class TrayAccessRequest
{
    public string Code { get; set; } = "";
    public string State { get; set; } = AccessStates.Pending;
    public string? Reason { get; set; }
    public string? Name { get; set; }
    public DateTime? Since { get; set; }
    public DateTime? Expires { get; set; }
    /// <summary>Serveren kan ikke nås lige nu (flere fejl i træk): den sidste fejl. Agenten prøver selv igen.</summary>
    public string? Problem { get; set; }

    /// <summary>Teksten, brugeren ser.</summary>
    public string Text => State switch
    {
        _ when Problem is { Length: > 0 } p && AccessStates.Open(State) => $"Kan ikke nå serveren – prøver igen (kode {Code}): {p}",
        AccessStates.Rejected => "Afvist" + (Reason is { Length: > 0 } r ? $": {r}" : ""),
        AccessStates.Expired => "Anmodningen er udløbet – anmod igen",
        AccessStates.KeySent => "Administratoren sender dig en nøgle i stedet",
        AccessStates.Superseded or AccessStates.Unknown => "Anmodningen findes ikke længere – anmod igen",
        // kun når agenten ikke selv har fået sin nøgle (ellers vises anmodningen slet ikke)
        AccessStates.Enrolled => "Tilmeldt på serveren, men maskinen fik ikke sin nøgle – anmod igen",
        AccessStates.Approved or AccessStates.Issued => $"Godkendt – tilmelder (kode {Code})",
        _ => $"Venter på godkendelse – kode {Code}",
    };
}

/// <summary>Anmodningen, agenten husker mellem genstarter (hemmeligheden beskyttes af agenten selv: DPAPI eller fil med mode 600).</summary>
public sealed class PendingAccess
{
    public int Id { get; set; }
    public string Secret { get; set; } = "";
    /// <summary>Den private nøgle (PKCS#8, base64), der åbner den forseglede engangskode. Beskyttes som hemmeligheden.</summary>
    public string PrivateKey { get; set; } = "";
    public string Code { get; set; } = "";
    /// <summary>Kun ved et certifikat, systemet ikke stoler på: fingeraftrykket fra anmodningen, som senere svar skal have.</summary>
    public string? Pin { get; set; }
    /// <summary>Certifikatet var ikke et, systemet stoler på: fingeraftrykket pinnes ved tilmeldingen (bekræftet af koden).</summary>
    public bool PinNeeded { get; set; }
    public string Name { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Expires { get; set; }
}

public sealed class AccessRequestException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Ser serverens TLS-certifikat. Ved selve anmodningen (<paramref name="firstContact"/>) godtages certifikatet (det bekræftes af koden,
/// som administratoren sammenligner), og resten af anmodningen skal have samme fingeraftryk. Bagefter: et fastlåst fingeraftryk
/// (<paramref name="expectedPin"/>, kun ved selvsignerede certifikater) eller almindelig certifikatkontrol.
/// </summary>
public sealed class AccessTlsObserver(string? expectedPin, bool firstContact = false)
{
    public string? Pin { get; private set; }
    public bool SystemTrusted { get; private set; }

    public bool Validate(object? sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is null) return false;
        string pin;
        try { pin = TlsPinning.Of(cert); } catch (CryptographicException) { return false; }
        if (expectedPin is { Length: > 0 }) { if (pin != expectedPin) return false; }
        else if (Pin is not null) { if (pin != Pin) return false; }   // samme server gennem hele anmodningen
        else if (!firstContact && errors != SslPolicyErrors.None) return false;
        Pin ??= pin; SystemTrusted = errors == SslPolicyErrors.None;
        return true;
    }

    /// <summary>En HttpClient, der bruger observatøren (til almindelige http/https-adresser; ikke socket-filer).</summary>
    public HttpClient Client(TimeSpan? timeout = null) =>
        new(new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = Validate } }) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
}

/// <summary>
/// Hvorfor serveren afviste en anmodning om adgang (403), og hvad brugeren kan gøre. Grundene: "outside" (kun hjemmenet, kaldet kom
/// udefra) og "off" (slået fra – en maskine, administratoren har blokeret, får også "off", så den ikke kan se, at den er blokeret).
/// </summary>
public static class AccessRefusal
{
    /// <summary>Grunden i fejlsvaret ("reason"): serveren tager kun imod fra hjemmenettet/VPN, og kaldet kom udefra.</summary>
    public const string Outside = "outside";

    /// <summary>Grunden i fejlsvaret ("reason"): "Anmod om adgang" er slået fra på serveren (eller maskinen er blokeret).</summary>
    public const string Off = "off";

    public const string OffText = "Serveren tager ikke imod anmodninger om adgang – det er slået fra. " +
        "Bed administratoren slå det til under Indstillinger → Agent-sikkerhed → Anmod om adgang, eller om en engangskode.";

    public const string Generic = "Serveren tager ikke imod anmodninger om adgang lige nu. Bed administratoren om en engangskode eller en tilmeldingsnøgle.";

    public static string OutsideText(string host) =>
        $"Serveren tager kun imod anmodninger fra hjemmenettet, og du har forbundet via dens offentlige adresse ({host}). " +
        "Skriv serverens adresse på hjemmenettet i stedet (fx http://192.168.1.10:8585), eller bed administratoren om en engangskode.";

    /// <summary>
    /// Teksten til et 403. Grunden "outside" giver hjemmenet-hjælpen, "off" forklarer, at det er slået fra; uden grund (servere 3.41–3.44 sender ingen) gives den også,
    /// hvis adressen, agenten brugte, er offentlig. Ellers den generelle tekst.
    /// </summary>
    public static string Explain(string? reason, string? baseUrl)
    {
        var host = HostOf(baseUrl);
        if (reason == Outside) return OutsideText(host ?? "den offentlige adresse");
        if (reason == Off) return OffText;
        if (reason is null && host is not null && IsPublicHost(host)) return OutsideText(host);
        return Generic;
    }

    /// <summary>"reason" fra et ProblemDetails-svar, eller null.</summary>
    public static string? ReasonOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Værtsnavnet i adressen (uden port), eller null hvis den ikke kan læses (fx en socket-fil).</summary>
    public static string? HostOf(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var u = baseUrl.Trim();
        if (u.StartsWith('/') || u.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!u.Contains("://")) u = "http://" + u;
        return Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? uri.Host.Trim('[', ']') : null;
    }

    /// <summary>
    /// Er værten en offentlig adresse? Private net (RFC 1918), CGNAT/VPN (100.64/10), link-local, loopback, IPv6 ULA/link-local,
    /// navne uden punktum og .local/.lan/.home/.home.arpa/.internal/localhost regnes som hjemmenet.
    /// </summary>
    public static bool IsPublicHost(string host)
    {
        var h = host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        if (h.Length == 0) return false;
        if (IPAddress.TryParse(h, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return false;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6None));
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 127 || b[0] == 0
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
        }
        if (!h.Contains('.')) return false;
        string[] local = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localhost"];
        return !local.Any(s => h.EndsWith(s, StringComparison.Ordinal));
    }
}

/// <summary>Kaldene til serveren (anonyme, alt i POST-body).</summary>
public static class AccessRequestClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Første skridt: forpligtelsen (uden hemmeligheden). Svaret er serverens nonce.</summary>
    public static async Task<AccessRequestStarted> Start(HttpClient http, string baseUrl, AccessRequestCreate req, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync(baseUrl.TrimEnd('/') + "/api/agent/access-requests", req, Json, ct);
        if (!res.IsSuccessStatusCode) throw new AccessRequestException((int)res.StatusCode, CreateError(res.StatusCode, await Reason(res, ct), baseUrl));
        return (await res.Content.ReadFromJsonAsync<AccessRequestStarted>(Json, ct))!;
    }

    /// <summary>Andet skridt: hemmeligheden og den offentlige nøgle. Først nu oprettes anmodningen hos administratoren.</summary>
    public static async Task<AccessRequestCreated> Reveal(HttpClient http, string baseUrl, AccessReveal req, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync(baseUrl.TrimEnd('/') + "/api/agent/access-requests/reveal", req, Json, ct);
        if (!res.IsSuccessStatusCode) throw new AccessRequestException((int)res.StatusCode, CreateError(res.StatusCode, await Reason(res, ct), baseUrl));
        return (await res.Content.ReadFromJsonAsync<AccessRequestCreated>(Json, ct))!;
    }

    /// <summary>
    /// Teksten til en afvist anmodning. <paramref name="reason"/>: serverens maskinlæsbare grund ved 403 (fra 3.44.1, se
    /// <see cref="AccessRefusal"/>); <paramref name="baseUrl"/>: adressen, agenten brugte.
    /// </summary>
    public static string CreateError(HttpStatusCode s, string? reason = null, string? baseUrl = null) => s switch
    {
        HttpStatusCode.Forbidden => AccessRefusal.Explain(reason, baseUrl),
        HttpStatusCode.TooManyRequests => "Der er for mange anmodninger lige nu. Prøv igen senere, eller bed administratoren om en nøgle.",
        HttpStatusCode.BadRequest => "Maskinnavnet kan ikke bruges (kun A-Z, 0-9, - og _).",
        HttpStatusCode.NotFound => "Serveren kender ikke \"Anmod om adgang\" (for gammel version). Bed administratoren om en nøgle.",
        HttpStatusCode.Gone => "Anmodningen tog for lang tid at sende. Prøv igen.",
        _ => $"Serveren svarede {(int)s}.",
    };

    /// <summary>"reason" fra serverens fejlsvar (ProblemDetails-JSON). Null, hvis svaret ikke har en (ældre servere) eller ikke kan læses.</summary>
    private static async Task<string?> Reason(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            return AccessRefusal.ReasonOf(body);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { return null; }
    }

    /// <summary>Spørger, hvordan det går. Ukendt anmodning eller forkert hemmelighed = <see cref="AccessStates.Unknown"/>.</summary>
    public static async Task<AccessPollResult> Poll(HttpClient http, string baseUrl, int id, string secret, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/api/agent/access-requests/{id}/poll", new AccessPollRequest(secret), Json, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return new(AccessStates.Unknown);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<AccessPollResult>(Json, ct))!;
    }

    /// <summary>
    /// Opretter anmodningen og returnerer det, agenten skal huske. <paramref name="observer"/> er den, <paramref name="http"/> bruger
    /// (null ved socket-fil eller http – så er fingeraftrykket tomt); den skal være lavet med <c>firstContact: true</c>.
    /// <paramref name="replaces"/>: maskinens egen tidligere anmodning, som den nye erstatter.
    /// </summary>
    public static async Task<PendingAccess> Request(HttpClient http, AccessTlsObserver? observer, string baseUrl, string name, string? platform,
        SystemFacts? facts, string? requestedBy, string? message, AccessReplace? replaces = null, CancellationToken ct = default)
    {
        var secret = AccessCode.NewSecret();
        var (publicKey, privateKey) = AccessSeal.NewKeyPair();
        // første kald: et let kald, så fingeraftrykket kendes, før noget sendes (og koden kan regnes ud)
        if (observer is not null && baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            try { using var _ = await http.GetAsync(baseUrl.TrimEnd('/') + "/api/health", ct); } catch (HttpRequestException) { }
        var pin = observer?.Pin ?? "";
        var started = await Start(http, baseUrl, new AccessRequestCreate(AccessCode.Commit(secret, publicKey), name, platform, facts, Trim(requestedBy, 64),
            Trim(message, 200), pin, replaces), ct);
        var created = await Reveal(http, baseUrl, new AccessReveal(started.Handle, secret, publicKey), ct);
        var pinNeeded = observer is { SystemTrusted: false, Pin: not null };
        return new PendingAccess
        {
            Id = created.Id, Secret = secret, PrivateKey = privateKey, Code = AccessCode.Display(secret, started.Nonce, pin, publicKey),
            // kun et selvsigneret certifikat fastlåses; et certifikat, systemet stoler på, kontrolleres almindeligt (det kan skifte)
            Pin = pinNeeded && pin.Length > 0 ? pin : null, PinNeeded = pinNeeded,
            Name = name, Created = DateTime.UtcNow, Expires = created.ExpiresAt,
        };
    }

    /// <summary>Åbner engangskoden fra en godkendt anmodning (null = den kunne ikke åbnes, fx ændret undervejs).</summary>
    public static string? OpenKey(PendingAccess a, AccessPollResult r)
    {
        if (r.SealedKey is not { Length: > 0 } sealedKey || a.PrivateKey is not { Length: > 0 } priv) return null;
        try { return AccessSeal.Open(priv, sealedKey, AccessSeal.Context(a.Id)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    /// <summary>Hvor mange forespørgsler der må fejle i træk, før brugeren får at vide, at serveren ikke kan nås.</summary>
    public const int ShowProblemAfter = 3;

    private static string? Trim(string? s, int max) => s?.Trim() is { Length: > 0 } t ? t.Length > max ? t[..max] : t : null;

    /// <summary>Ventetid mellem forespørgsler: 30 s, langsommere ved fejl (op til 5 min).</summary>
    public static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(failures, 4))));
}
