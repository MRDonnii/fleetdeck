using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Fejl, der kan vises direkte for brugeren.</summary>
public sealed class MdmException(string message) : InvalidOperationException(message);

/// <summary>MDM-serverens container (NanoHUB) og hvor den kører – uden hemmeligheder.</summary>
public sealed record MdmSidecarConfig(int? MachineId, string Network, string Ip, string Container, string Image, string DataDir);

/// <summary>
/// MDM-serveren (NanoHUB: NanoMDM + KMFDDM i én container) startes, opdateres og stoppes af Fleetdeck på en Docker-vært, som tunnelen.
/// API-nøglen og webhook-adressen hentes af jobbet med en engangskode – de står aldrig i jobbet, output eller loggen.
/// </summary>
public static partial class MdmSidecar
{
    /// <summary>NanoHUB v0.2.0, låst til billedets digest (kan ændres i Indstillinger for at opdatere).</summary>
    public const string Image = "ghcr.io/micromdm/nanohub:0.2.0@sha256:3ea7fc5cee6396ed70c2fe26d5c72d57dc9547205d1be2fab0f804bf3de7f7ae";
    public const int Port = 9004;
    public const string Source = "apple_mdm";
    public const string DefaultContainer = "fleetdeck-mdm";
    public const string StartTitle = "Apple-styring: start MDM-server", StopTitle = "Apple-styring: stop MDM-server", SuggestTitle = "Apple-styring: find en ledig IP";
    public const string SecretsPath = "/api/agent/mdm/secrets";
    public const string CaFile = "fleetdeck-ca.pem";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/:@-]{2,250}\z")] private static partial Regex ImageRx();
    [GeneratedRegex(@"^/[A-Za-z0-9._/-]{1,200}\z")] private static partial Regex DirRx();
    [GeneratedRegex(@"/api/mdm/hook/[A-Za-z0-9_-]+")] private static partial Regex HookRx();

    public static string? ValidateImage(string? image) =>
        !string.IsNullOrWhiteSpace(image) && ImageRx().IsMatch(image) ? null : "Billedet skal skrives som fx ghcr.io/micromdm/nanohub:0.2.0";

    public static string? ValidateDataDir(string? dir) =>
        string.IsNullOrEmpty(dir) || (DirRx().IsMatch(dir) && !dir.Split('/').Contains("..") && dir != "/") ? null
            : "Datamappen skal være en fuld sti som /mnt/user/appdata/fleetdeck-mdm (tom = automatisk)";

    public static string? Validate(MdmSidecarConfig c) =>
        PublicAccess.ValidateNetwork(c.Network) ?? PublicAccess.ValidateIp(c.Ip) ?? PublicAccess.ValidateContainer(c.Container) ?? ValidateImage(c.Image) ?? ValidateDataDir(c.DataDir)
        ?? (c.Ip.Length > 0 && c.Network is "host" or "bridge" ? $"En fast IP kan kun bruges på et macvlan/ipvlan-netværk (fx br0), ikke på \"{c.Network}\" – lad feltet være tomt" : null);

    /// <summary>Skjul webhook-hemmeligheden (og API-nøglen) i tekst, der gemmes eller vises.</summary>
    public static string Mask(string text, string? apiKey = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (apiKey is { Length: >= 8 }) text = text.Replace(apiKey, "***", StringComparison.Ordinal);
        return HookRx().Replace(text, "/api/mdm/hook/***");
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string Head(MdmSidecarConfig c) =>
        $"""
        # Fleetdeck: Apple-styring (MDM-server, NanoHUB)
        NAME={Q(c.Container)}
        NET={Q(c.Network)}
        IP={Q(c.Ip)}
        IMAGE={Q(c.Image)}
        DATA={Q(c.DataDir)}
        fail() {"{"} echo "✗ $*"; exit 1; {"}"}
        command -v docker >/dev/null 2>&1 || fail "Docker findes ikke på maskinen"
        docker info >/dev/null 2>&1 || fail "Docker kører ikke på maskinen (Unraid: Settings → Docker → Enable Docker)"
        if [ -z "$DATA" ]; then
          if [ -f /etc/unraid-version ]; then DATA=/mnt/user/appdata/$NAME; else DATA=/var/lib/$NAME; fi
        fi

        """;

    /// <summary>Start, genstart eller opdatér MDM-serveren (henter altid billedet igen). Enheds-CA'ens offentlige certifikat lægges i datamappen.</summary>
    public static JobParams StartJob(MdmSidecarConfig c, string nonce, string caCertPem)
    {
        if (Validate(c) is { } err) throw new ArgumentException(err);
        if (!Regex.IsMatch(nonce, @"^[A-Za-z0-9_-]{16,128}\z")) throw new ArgumentException("Ugyldig engangskode");
        if (!caCertPem.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)) throw new ArgumentException("Enheds-CA'en mangler");
        var ca = Convert.ToBase64String(Encoding.ASCII.GetBytes(caCertPem));
        var body = Head(c) + $"NONCE={Q(nonce)}\nSECRETS_PATH={Q(SecretsPath)}\nCA_B64={Q(ca)}\nPORT={Port}\nCAFILE={Q(CaFile)}\n" + """
            CFG=/etc/fleetdeck/agent.json
            command -v curl >/dev/null 2>&1 || fail "curl findes ikke på maskinen"
            command -v base64 >/dev/null 2>&1 || fail "base64 findes ikke på maskinen"
            docker network inspect "$NET" >/dev/null 2>&1 || fail "Docker-netværket $NET findes ikke. Netværk på maskinen: $(docker network ls --format '{{.Name}}' | tr '\n' ' ')"
            if [ -n "$IP" ]; then
              USED=$(docker network inspect -f '{{range .Containers}}{{.Name}}={{.IPv4Address}} {{end}}' "$NET" 2>/dev/null | tr ' ' '\n' | grep -v "^$NAME=" | grep -F "=$IP/" | head -n 1)
              [ -z "$USED" ] || fail "$IP bruges allerede af containeren ${USED%%=*} på $NET"
              OLD=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$NAME" 2>/dev/null)
              case " $OLD " in
                *" $IP "*) ;;
                *) if ping -c 1 -W 1 "$IP" >/dev/null 2>&1; then fail "$IP svarer allerede på netværket – vælg en ledig adresse"; fi ;;
              esac
            fi

            # datamappen: NanoHUBs data (push-certifikat, kø, DDM) og enheds-CA'ens offentlige certifikat
            mkdir -p "$DATA/data" || fail "Datamappen $DATA kunne ikke oprettes"
            printf '%s' "$CA_B64" | base64 -d > "$DATA/$CAFILE.tmp" && mv "$DATA/$CAFILE.tmp" "$DATA/$CAFILE" || fail "Enheds-CA'en kunne ikke gemmes i $DATA"

            # hemmelighederne: hentes fra Fleetdeck med agentens eget login (kun til dette job, kun én gang)
            [ -r "$CFG" ] || fail "Agentens opsætning ($CFG) kan ikke læses"
            SERVER=$(sed -n 's/^[[:space:]]*"Server":[[:space:]]*"\([^"]*\)".*/\1/p' "$CFG" | head -n 1)
            AGENT=$(sed -n 's/^[[:space:]]*"Token":[[:space:]]*"\([^"]*\)".*/\1/p' "$CFG" | head -n 1)
            [ -n "$SERVER" ] && [ -n "$AGENT" ] || fail "Agentens opsætning ($CFG) mangler server eller login"
            case "$SERVER" in
              unix:*) SOCK=${SERVER#unix:}; BASE=http://localhost ;;
              *) SOCK=; BASE=${SERVER%/} ;;
            esac
            get_secrets() {
              if [ -n "$SOCK" ]; then
                printf 'header = "Authorization: Bearer %s"\n' "$AGENT" | curl -fsS --max-time 20 --unix-socket "$SOCK" -K - "$BASE$SECRETS_PATH?nonce=$NONCE"
              else
                printf 'header = "Authorization: Bearer %s"\n' "$AGENT" | curl -fsS --max-time 20 -K - "$BASE$SECRETS_PATH?nonce=$NONCE"
              fi
            }
            SECRETS=$(get_secrets) || fail "Nøglerne kunne ikke hentes fra Fleetdeck (start MDM-serveren igen fra Fleetdeck)"
            unset AGENT
            val() { printf '%s\n' "$SECRETS" | sed -n "s/^$1=//p" | head -n 1; }
            NANOHUB_API_KEY=$(val NANOHUB_API_KEY)
            NANOHUB_WEBHOOK_URL=$(val NANOHUB_WEBHOOK_URL)
            NANOHUB_WEBHOOK_HMAC_KEY=$(val NANOHUB_WEBHOOK_HMAC_KEY)
            unset SECRETS
            [ -n "$NANOHUB_API_KEY" ] && [ -n "$NANOHUB_WEBHOOK_URL" ] || fail "Fleetdeck sendte ikke alle nøgler"
            export NANOHUB_API_KEY NANOHUB_WEBHOOK_URL NANOHUB_WEBHOOK_HMAC_KEY
            mask() { sed -e "s#/api/mdm/hook/[A-Za-z0-9_-]*#/api/mdm/hook/***#g" -e "s#$NANOHUB_API_KEY#***#g"; }

            echo "Henter $IMAGE …"
            docker pull -q "$IMAGE" >/dev/null || fail "$IMAGE kunne ikke hentes (er der internet på maskinen?)"
            if docker ps -a --format '{{.Names}}' | grep -Fqx "$NAME"; then
              echo "Fjerner den gamle container $NAME (data bevares i $DATA) …"
              docker rm -f "$NAME" >/dev/null || fail "Den gamle container $NAME kunne ikke fjernes"
            fi
            set -- run -d --name "$NAME" --restart unless-stopped --network "$NET" -v "$DATA:/app/db"
            if [ -n "$IP" ]; then set -- "$@" --ip "$IP"; fi
            if [ "$NET" = bridge ]; then set -- "$@" -p "$PORT:$PORT"; fi
            echo "Starter $NAME på netværket $NET${IP:+ med IP $IP} …"
            docker "$@" -e NANOHUB_API_KEY -e NANOHUB_WEBHOOK_URL -e NANOHUB_WEBHOOK_HMAC_KEY \
              -e NANOHUB_CA="/app/db/$CAFILE" -e NANOHUB_STORAGE=file -e NANOHUB_STORAGE_DSN=/app/db/data -e NANOHUB_LISTEN=":$PORT" \
              "$IMAGE" >/dev/null || fail "Containeren $NAME kunne ikke startes"
            # Unraid: start den også, når Docker/arrayet startes igen
            AUTO=/var/lib/docker/unraid-autostart
            if [ -f /etc/unraid-version ] && [ -d /var/lib/docker ]; then
              touch "$AUTO"
              awk -v n="$NAME" '$1==n{f=1} END{exit !f}' "$AUTO" || echo "$NAME" >> "$AUTO"
            fi
            sleep 6
            ST=$(docker inspect -f '{{.State.Status}}' "$NAME" 2>/dev/null)
            LOG=$(docker logs --tail 15 "$NAME" 2>&1 | mask)
            unset NANOHUB_API_KEY NANOHUB_WEBHOOK_URL NANOHUB_WEBHOOK_HMAC_KEY
            if [ "$ST" = running ]; then
              echo "✓ MDM-serveren kører ($NAME på $NET${IP:+, $IP}, port $PORT). Data: $DATA"
              [ -n "$LOG" ] && { echo "Seneste log:"; echo "$LOG"; }
              exit 0
            fi
            echo "✗ $NAME kører ikke (status: ${ST:-ukendt}). Seneste log:"
            echo "$LOG"
            exit 1
            """;
        return new JobParams { Body = body, TimeoutMin = 15 };
    }

    /// <summary>Stop MDM-serveren: fjern containeren (data i datamappen bevares).</summary>
    public static JobParams StopJob(MdmSidecarConfig c)
    {
        if (PublicAccess.ValidateContainer(c.Container) is { } err) throw new ArgumentException(err);
        var body = Head(c with { Network = PublicAccess.DefaultNetwork, Ip = "", Image = Image, DataDir = "" }) + """
            if docker ps -a --format '{{.Names}}' | grep -Fqx "$NAME"; then
              docker rm -f "$NAME" >/dev/null || fail "Containeren $NAME kunne ikke fjernes"
              echo "✓ MDM-serveren er stoppet, og containeren $NAME er fjernet (data er bevaret)"
            else
              echo "✓ Containeren $NAME findes ikke – MDM-serveren kører ikke"
            fi
            AUTO=/var/lib/docker/unraid-autostart
            if [ -f "$AUTO" ]; then
              awk -v n="$NAME" '$1!=n' "$AUTO" > "$AUTO.fd" && cat "$AUTO.fd" > "$AUTO"
              rm -f "$AUTO.fd"
            fi
            exit 0
            """;
        return new JobParams { Body = body, TimeoutMin = 5 };
    }
}

/// <summary>Den offentlige MDM-adresse (fx mdm.eksempel.dk). På den adresse svarer Fleetdeck kun på /enroll/… – aldrig på brugerfladen eller /api.</summary>
public static class MdmPublicHost
{
    private static volatile string current = "";
    public static string Current => current;
    public static void Set(string? host) => current = (host ?? "").Trim().ToLowerInvariant();

    public static bool IsPublic(HttpRequest req)
    {
        var h = current;
        if (h.Length == 0) return false;
        if (string.Equals(req.Host.Host, h, StringComparison.OrdinalIgnoreCase)) return true;
        return req.Headers["X-Forwarded-Host"].ToString().Split(',').Any(x => string.Equals(x.Trim().Split(':')[0], h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Det eneste, der må nås på den offentlige MDM-adresse hos Fleetdeck.</summary>
    public static bool Allowed(string? path) => path is { } x && (x.Equals("/enroll", StringComparison.OrdinalIgnoreCase) || x.StartsWith("/enroll/", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Opsætningen af Apple-styring. Hemmeligheder (nøgler, API-nøgle, webhook) gemmes krypteret med DataProtection.</summary>
public sealed class MdmSettings(SettingsService s, IDataProtectionProvider dp)
{
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.Mdm.Secrets");
    public const string HostRx = @"^(?=.{4,200}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$";

    public Task<string> Get(string key) => s.Get(key);
    public Task Set(string key, string value) => s.Set(key, value);

    public async Task<string?> Secret(string key)
    {
        var enc = await s.Get(key);
        if (enc.Length == 0) return null;
        try { return protector.Unprotect(enc); } catch (CryptographicException) { return null; }
    }
    public Task SetSecret(string key, string? value) => s.Set(key, string.IsNullOrEmpty(value) ? "" : protector.Protect(value));

    public async Task<string> PublicHost() => (await s.Get("mdm_public_host")).Trim().ToLowerInvariant();
    public async Task<bool> HostLocked() => await s.Get("mdm_host_locked") == "1";
    public async Task<string> Org() => (await s.Get("mdm_org")).Trim() is { Length: > 0 } o ? o : "Fleetdeck";

    public async Task<MdmSidecarConfig> Sidecar()
    {
        int? mid = int.TryParse(await s.Get("mdm_machine_id"), out var id) && id > 0 ? id : null;
        if (mid is null && int.TryParse(await s.Get("tunnel_host_machine_id"), out var tid) && tid > 0) mid = tid;   // samme vært som tunnelen
        return new(mid,
            await s.Get("mdm_network") is { Length: > 0 } n ? n : PublicAccess.DefaultNetwork,
            await s.Get("mdm_ip"),
            await s.Get("mdm_container") is { Length: > 0 } c ? c : MdmSidecar.DefaultContainer,
            await s.Get("mdm_image") is { Length: > 0 } i ? i : MdmSidecar.Image,
            await s.Get("mdm_data_dir"));
    }

    public async Task SaveSidecar(MdmSidecarConfig c)
    {
        await s.Set("mdm_machine_id", c.MachineId?.ToString() ?? "");
        await s.Set("mdm_network", c.Network);
        await s.Set("mdm_ip", c.Ip);
        await s.Set("mdm_container", c.Container);
        await s.Set("mdm_image", c.Image == MdmSidecar.Image ? "" : c.Image);
        await s.Set("mdm_data_dir", c.DataDir);
    }

    /// <summary>NanoHUBs interne adresse: angivet, ellers http://&lt;fast IP&gt;:9004.</summary>
    public async Task<string?> ApiUrl()
    {
        if ((await s.Get("mdm_api_url")).Trim().TrimEnd('/') is { Length: > 0 } u) return u;
        var ip = await s.Get("mdm_ip");
        return ip.Length > 0 ? $"http://{ip}:{MdmSidecar.Port}" : null;
    }

    /// <summary>Adressen, NanoHUB sender webhooks til (Fleetdecks adresse på hjemmenettet).</summary>
    public async Task<string?> FleetdeckLanUrl()
    {
        var b = (await s.Get("mdm_webhook_base")).Trim().TrimEnd('/');
        if (b.Length == 0) b = (await s.Get("server_url")).Trim().TrimEnd('/');
        return Uri.TryCreate(b, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? b : null;
    }

    /// <summary>API-nøgle, webhook-hemmelighed og HMAC-nøgle laves første gang, de skal bruges.</summary>
    public async Task<(string ApiKey, string HookSecret, string HmacKey)> EnsureSecrets()
    {
        var api = await Secret("mdm_api_key");
        if (api is null) { api = Crypto.Token(36); await SetSecret("mdm_api_key", api); }
        var hook = await Secret("mdm_hook_secret");
        if (hook is null) { hook = Crypto.Token(32); await SetSecret("mdm_hook_secret", hook); }
        var hmac = await Secret("mdm_hmac_key");
        if (hmac is null) { hmac = Crypto.Token(32); await SetSecret("mdm_hmac_key", hmac); }
        return (api, hook, hmac);
    }

    public async Task<NanoHubClient?> Client() =>
        await ApiUrl() is { } url && await Secret("mdm_api_key") is { } key ? new NanoHubClient(url, key) : null;

    /// <summary>Enheds-CA'en (laves første gang). Nøglen gemmes krypteret.</summary>
    public async Task<(string CertPem, string KeyPem)> EnsureCa()
    {
        var cert = await s.Get("mdm_ca_cert");
        var key = await Secret("mdm_ca_key");
        if (cert.Length > 0 && key is not null) return (cert, key);
        var ca = MdmPki.CreateCa(await Org());
        await SetSecret("mdm_ca_key", ca.KeyPem);
        await s.Set("mdm_ca_cert", ca.Pem);
        return (ca.Pem, ca.KeyPem);
    }

    // ---- engangskoden til start-jobbet (som tunnelen)
    public Task SetNonce(int jobId, string nonce) => s.Set("mdm_nonce", $"{jobId}:{Crypto.Sha256(nonce)}");
    public Task ClearNonce() => s.Set("mdm_nonce", "");
    public async Task<int?> TakeNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return null;
        var p = (await s.Get("mdm_nonce")).Split(':', 2);
        if (p.Length != 2 || !int.TryParse(p[0], out var jobId) || !Crypto.FixedEquals(p[1], Crypto.Sha256(nonce))) return null;
        await ClearNonce();
        return jobId;
    }

    // ---- push-certifikatet
    public sealed record PushState(string? Topic, DateTime? Expires, string? AppleId, bool HasCert, bool HasPendingCsr, bool HasEncryption, bool HasSignedRequest, string? Subject);

    public async Task<PushState> Push() => new(
        await s.Get("push_topic") is { Length: > 0 } t ? t : null,
        DateTime.TryParse(await s.Get("push_expires"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var e) ? e : null,
        await s.Get("push_apple_id") is { Length: > 0 } a ? a : null,
        (await s.Get("push_cert")).Length > 0,
        (await s.Get("push_csr")).Length > 0,
        (await s.Get("push_enc_cert")).Length > 0,
        (await s.Get("push_signed_request")).Length > 0,
        await s.Get("push_subject") is { Length: > 0 } sub ? sub : null);
}

/// <summary>NanoHUBs API (Basic-login "nanohub" + API-nøglen). Stierne står kun her (NanoHUB v0.2: /api/v1/nanomdm og /api/v1/ddm).</summary>
public sealed class NanoHubClient(string baseUrl, string apiKey)
{
    public const string NanoMdm = "/api/v1/nanomdm", Ddm = "/api/v1/ddm";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record IdResult(string? PushError, string? PushResult, string? CommandError);
    public sealed record ApiResult(int Code, string? PushError, string? CommandError, string? CommandUuid, Dictionary<string, IdResult> PerId);

    private HttpRequestMessage Req(HttpMethod m, string path, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, baseUrl.TrimEnd('/') + path) { Content = body };
        r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"nanohub:{apiKey}")));
        return r;
    }

    private static async Task<string> Read(HttpResponseMessage res, CancellationToken ct, bool allowMulti = false)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new MdmException("MDM-serveren afviste API-nøglen (start MDM-serveren igen fra Fleetdeck)");
        if (!res.IsSuccessStatusCode && !(allowMulti && (int)res.StatusCode is 207 or 500 && text.TrimStart().StartsWith('{')))
            throw new MdmException($"MDM-serveren svarede {(int)res.StatusCode}: {text.Trim()[..Math.Min(200, text.Trim().Length)]}");
        return text;
    }

    private static string Ids(IEnumerable<string> ids) => string.Join(',', ids.Select(Uri.EscapeDataString));

    public async Task<string> Version(CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Get, "/version"), ct);
        var text = await Read(res, ct);
        try { return JsonNode.Parse(text)?["version"]?.GetValue<string>() ?? text.Trim(); } catch (JsonException) { return text.Trim(); }
    }

    /// <summary>Læg push-certifikatet og nøglen på MDM-serveren. Svarer med topic og udløb.</summary>
    public async Task<(string Topic, DateTime NotAfter)> PutPushCert(string certPem, string keyPem, CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Put, $"{NanoMdm}/pushcert", new StringContent(certPem.Trim() + "\n" + keyPem.Trim() + "\n", Encoding.UTF8, "text/plain")), ct);
        var n = JsonNode.Parse(await Read(res, ct));
        return (n?["topic"]?.GetValue<string>() ?? "", n?["not_after"]?.GetValue<DateTime>() ?? default);
    }

    public async Task<ApiResult> Enqueue(IEnumerable<string> ids, string plist, bool noPush, CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Put, $"{NanoMdm}/enqueue/{Ids(ids)}{(noPush ? "?nopush=1" : "")}", new StringContent(plist, Encoding.UTF8, "text/plain")), ct);
        return ParseResult((int)res.StatusCode, await Read(res, ct, allowMulti: true));
    }

    public async Task<ApiResult> Push(IEnumerable<string> ids, CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Get, $"{NanoMdm}/push/{Ids(ids)}"), ct);
        return ParseResult((int)res.StatusCode, await Read(res, ct, allowMulti: true));
    }

    public static ApiResult ParseResult(int code, string json)
    {
        var n = JsonNode.Parse(json);
        string? S(JsonNode? x, string k) => x?[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        var per = new Dictionary<string, IdResult>(StringComparer.Ordinal);
        if (n?["status"] is JsonObject st)
            foreach (var (id, v) in st) per[id] = new(S(v, "push_error"), S(v, "push_result"), S(v, "command_error"));
        return new(code, S(n, "push_error"), S(n, "command_error"), S(n, "command_uuid"), per);
    }

    // ---- DDM (KMFDDM)
    public async Task PutDeclaration(object declaration, CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Put, $"{Ddm}/declarations?nonotify=1", JsonContent(declaration)), ct);
        await Read(res, ct);
    }

    public async Task PutSetDeclaration(string set, string declaration, bool notify, CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Put, $"{Ddm}/set-declarations/{Uri.EscapeDataString(set)}?declaration={Uri.EscapeDataString(declaration)}{(notify ? "" : "&nonotify=1")}"), ct);
        await Read(res, ct);
    }

    public async Task DeleteSetDeclaration(string set, string declaration, CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Delete, $"{Ddm}/set-declarations/{Uri.EscapeDataString(set)}?declaration={Uri.EscapeDataString(declaration)}"), ct);
        await Read(res, ct);
    }

    public async Task PutEnrollmentSet(string enrollmentId, string set, CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Put, $"{Ddm}/enrollment-sets/{Uri.EscapeDataString(enrollmentId)}?set={Uri.EscapeDataString(set)}"), ct);
        await Read(res, ct);
    }

    /// <summary>Statusværdier, iPad'en har meldt via DDM (fx softwareupdate.install-state).</summary>
    public async Task<List<(string Path, string Value, DateTime? At)>> StatusValues(string enrollmentId, CancellationToken ct = default)
    {
        using var res = await Http.SendAsync(Req(HttpMethod.Get, $"{Ddm}/status-values/{Uri.EscapeDataString(enrollmentId)}"), ct);
        return ParseStatusValues(await Read(res, ct));
    }

    public static List<(string Path, string Value, DateTime? At)> ParseStatusValues(string json)
    {
        var o = new List<(string, string, DateTime?)>();
        if (JsonNode.Parse(json) is not JsonObject root) return o;
        foreach (var (_, arr) in root)
            foreach (var x in arr as JsonArray ?? [])
            {
                var path = x?["path"]?.ToString() ?? "";
                if (path.Length == 0) continue;
                DateTime? at = DateTime.TryParse(x?["status_received"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
                o.Add((path, x?["value"]?.ToString() ?? "", at));
            }
        return o;
    }

    private static StringContent JsonContent(object o)
    {
        var c = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8);
        c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return c;
    }
}

/// <summary>Kommandoer, tilmelding og webhooks for Apple-styring (én forespørgsel ad gangen).</summary>
public sealed class MdmService(AppDb db, MdmSettings ms, FamilySettings fs, LockedProfiles lp, Notifier notifier, Live live, IDataProtectionProvider dp, ILogger<MdmService> log)
{
    private readonly IDataProtector profileProtector = dp.CreateProtector("Fleetdeck.Mdm.EnrollProfile");
    public const string LockedDetailPrefix = "Låst profil v", LockedRemoved = "Låst profil fjernet", Unenroll = "Apple-styring fjernet";
    public static readonly TimeSpan LinkTtl = TimeSpan.FromMinutes(30);
    public const int MaxFetches = 5;

    /// <summary>Send en kommando til iPad'en via NanoHUB og gem den i historikken.</summary>
    public async Task<MdmCommand> Send(Ipad p, MdmDevice d, MdmCommands.Built b, string by, string? detail, string? hash = null, bool push = true, CancellationToken ct = default)
    {
        if (d.EnrollmentId is null || d.State is not (MdmStates.Enrolled or MdmStates.Authenticated)) throw new MdmException("iPad'en er ikke tilmeldt Apple-styring");
        if (!d.Approved) throw new MdmException("iPad'en venter på, at en administrator godkender den");
        var client = await ms.Client() ?? throw new MdmException("MDM-serveren er ikke sat op (Indstillinger → Apple-styring)");
        var c = new MdmCommand
        {
            CommandUuid = b.Uuid, IpadId = p.Id, RequestType = b.RequestType, RequestedBy = by.Length > 80 ? by[..80] : by,
            Detail = detail is { Length: > 300 } ? detail[..300] : detail, PayloadHash = hash,
        };
        db.MdmCommands.Add(c);
        await db.SaveChangesAsync(ct);
        try
        {
            var r = await client.Enqueue([d.EnrollmentId], b.Xml, !push, ct);
            var per = r.PerId.GetValueOrDefault(d.EnrollmentId);
            if ((per?.CommandError ?? r.CommandError) is { } ce) { c.Status = MdmCommandStates.Failed; c.Error = ce; }
            else
            {
                c.Status = MdmCommandStates.Sent;
                var pe = per?.PushError ?? (push ? r.PushError : null);
                d.PushError = pe;
                if (pe is not null) c.Error = $"Kommandoen venter i køen, men push fejlede: {pe}";
            }
        }
        catch (Exception e) when (e is MdmException or HttpRequestException or TaskCanceledException or JsonException)
        {
            c.Status = MdmCommandStates.Failed; c.Error = e is HttpRequestException or TaskCanceledException ? $"MDM-serveren kunne ikke nås: {e.Message}" : e.Message;
        }
        c.Updated = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<MdmDevice> Device(Ipad p)
    {
        var d = await db.MdmDevices.FirstOrDefaultAsync(x => x.IpadId == p.Id);
        if (d is not null) return d;
        d = new MdmDevice { IpadId = p.Id, EnrollKey = Crypto.Token(18) };
        db.MdmDevices.Add(d);
        return d;
    }

    /// <summary>Tilmeldingsprofil med en ny identitet til iPad'en (gemmer certifikatets serienummer).</summary>
    public async Task<string> BuildEnrollProfile(Ipad p, MdmDevice d)
    {
        var host = await ms.PublicHost();
        if (host.Length == 0) throw new MdmException("Skriv den offentlige MDM-adresse først (Indstillinger → Apple-styring)");
        var push = await ms.Push();
        if (push.Topic is null) throw new MdmException("Push-certifikatet fra Apple mangler (Indstillinger → Apple-styring)");
        var (caCert, caKey) = await ms.EnsureCa();
        var id = MdmPki.IssueDevice(caCert, caKey, p.Id);
        d.IdentitySerial = id.Serial; d.IdentityExpires = id.NotAfter;
        if (d.State == MdmStates.CheckedOut) { d.State = MdmStates.Pending; d.LockedInstalledHash = null; d.LockedInstalledVersion = null; }
        return MdmEnrollmentProfile.Build(p, host, push.Topic, d.EnrollKey, id.Pkcs12, id.Password, await ms.Org());
    }

    /// <summary>Nyt engangslink (30 min.). Profilen laves nu og gemmes krypteret, så forhåndsvisninger og gentagne hentninger giver den samme.</summary>
    public async Task<(string Url, DateTime Expires)> CreateLink(Ipad p, string? expectedSerial)
    {
        if ((await ms.PublicHost()).Trim().Length == 0)
            throw new MdmException("Apple-styring (MDM) er ikke sat op endnu – brug i stedet \"Lav QR-kode til profilen\" under Opsætning for DNS-styringen.");
        var d = await Device(p);
        if (expectedSerial is not null) d.ExpectedSerial = expectedSerial.Trim().ToUpperInvariant() is { Length: > 0 } es ? es[..Math.Min(40, es.Length)] : null;
        var xml = await BuildEnrollProfile(p, d);
        var token = Crypto.Token(32);
        // ældre, ubrugte links til samme iPad holder op med at virke
        await db.MdmEnrollTokens.Where(t => t.IpadId == p.Id && t.UsedAt == null).ExecuteUpdateAsync(x => x.SetProperty(t => t.UsedAt, DateTime.UtcNow));
        var row = new MdmEnrollToken { TokenHash = Crypto.Sha256(token), IpadId = p.Id, Expires = DateTime.UtcNow.Add(LinkTtl), ProfileEnc = profileProtector.Protect(xml) };
        db.MdmEnrollTokens.Add(row);
        await db.SaveChangesAsync();
        return ($"https://{await ms.PublicHost()}/enroll/{token}", row.Expires);
    }

    /// <summary>Find et gyldigt link (uden at bruge det). Null = ukendt, udløbet eller brugt.</summary>
    public async Task<MdmEnrollToken?> FindLink(string token)
    {
        if (token.Length is < 20 or > 100) return null;
        var hash = Crypto.Sha256(token);
        var t = await db.MdmEnrollTokens.FirstOrDefaultAsync(x => x.TokenHash == hash);
        return t is null || t.UsedAt is not null || t.Expires < DateTime.UtcNow || t.Fetches >= MaxFetches ? null : t;
    }

    public string? LinkProfile(MdmEnrollToken t)
    {
        try { return profileProtector.Unprotect(t.ProfileEnc); } catch (CryptographicException) { return null; }
    }

    /// <summary>Behandl én webhook-hændelse fra NanoHUB. Idempotent: samme hændelse to gange giver samme tilstand.</summary>
    public async Task<bool> Handle(MdmEvent e)
    {
        var now = DateTime.UtcNow;
        await ms.Set("mdm_last_hook", now.ToString("O"));
        MdmDevice? d = null;
        if (e.EnrollKey is { } k) d = await db.MdmDevices.FirstOrDefaultAsync(x => x.EnrollKey == k);
        if (d is null && e.Udid is { Length: > 0 } u) d = await db.MdmDevices.FirstOrDefaultAsync(x => x.Udid == u);
        if (d is null) { log.LogInformation("Apple-styring: hændelse {Topic} fra en ukendt enhed ignoreres", e.Topic); return false; }
        var p = await db.Ipads.FirstOrDefaultAsync(x => x.Id == d.IpadId);
        if (p is null) return false;
        d.LastCheckin = now; d.NotSeenWarned = false;
        var pl = e.Payload;
        switch (e.Topic)
        {
            case MdmHook.Authenticate:
            {
                var serial = pl?.Str("SerialNumber");
                var udidChanged = d.Udid is { Length: > 0 } && e.Udid is { Length: > 0 } && d.Udid != e.Udid;
                var serialMismatch = d.ExpectedSerial is { Length: > 0 } exp && serial is { Length: > 0 } && !exp.Equals(serial, StringComparison.OrdinalIgnoreCase);
                if (udidChanged || serialMismatch)
                {
                    d.Approved = false;
                    d.ApprovalNote = serialMismatch ? $"Serienummer {serial} – forventede {d.ExpectedSerial}" : "En anden enhed har brugt iPad'ens tilmeldingsprofil";
                    notifier.Send($"🍏 {p.Name}: ukendt enhed vil tilmeldes\n{d.ApprovalNote}. Godkend den i Fleetdeck, hvis det er rigtigt.", "warning");
                }
                d.Udid = e.Udid ?? d.Udid;
                d.EnrollmentId = e.EnrollmentId ?? d.EnrollmentId;
                if (pl is not null) MdmHook.ApplyCheckin(d, pl);
                if (d.State != MdmStates.Enrolled) d.State = MdmStates.Authenticated;
                d.CheckedOutAt = null;
                await db.MdmEnrollTokens.Where(t => t.IpadId == p.Id && t.UsedAt == null).ExecuteUpdateAsync(x => x.SetProperty(t => t.UsedAt, now));
                break;
            }
            case MdmHook.TokenUpdate:
            {
                d.Udid ??= e.Udid;
                d.EnrollmentId ??= e.EnrollmentId;
                if (pl is not null) MdmHook.ApplyCheckin(d, pl);
                var first = d.State != MdmStates.Enrolled;
                d.State = MdmStates.Enrolled; d.EnrolledAt ??= now; d.PushError = null; d.CheckedOutAt = null;
                if (first)
                {
                    d.LastInventory = null;   // motoren henter oplysningerne
                    await ms.Set("mdm_host_locked", "1");
                    if (d.Approved) notifier.Send($"🍏 {p.Name} er tilmeldt Apple-styring", "info");
                }
                break;
            }
            case MdmHook.CheckOut:
                if (d.State != MdmStates.CheckedOut)
                {
                    d.State = MdmStates.CheckedOut; d.CheckedOutAt = now; d.LockedInstalledHash = null; d.LockedInstalledVersion = null;
                    notifier.Send($"🔴 {p.Name}: Apple-styring er fjernet\nMDM-profilen er fjernet på iPad'en. Profiler installeret via MDM er dermed også væk.", "critical");
                    await db.MdmCommands.Where(c => c.IpadId == p.Id && MdmCommandStates.Open.Contains(c.Status))
                        .ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, MdmCommandStates.Unknown).SetProperty(c => c.Error, "Apple-styring blev fjernet, før kommandoen blev udført").SetProperty(c => c.Updated, now));
                }
                break;
            case MdmHook.Connect:
                if (d.State is MdmStates.Pending or MdmStates.Authenticated && e.Status is not null and not "Idle") d.State = MdmStates.Enrolled;   // TokenUpdate gik tabt
                if (e.CommandUuid is { Length: > 0 } uuid && await db.MdmCommands.FirstOrDefaultAsync(c => c.CommandUuid == uuid && c.IpadId == p.Id) is { } cmd)
                {
                    ApplyResult(d, cmd, e.Status, pl, now);
                    // apps på iPad'en (Apps-fanen)
                    if (cmd.RequestType == AppStore.MdmRequestType && e.Status == "Acknowledged" && pl is not null)
                        await IpadAppStore.Apply(db, p.Id, IpadAppStore.ViaMdm, AppStore.MdmApps(pl), now);
                    else if (cmd.RequestType == AppStore.MdmRequestType && cmd.Status == MdmCommandStates.Error)
                        await IpadAppStore.Failed(db, p.Id, cmd.Error ?? "iPad'en meldte en fejl");
                }
                break;
        }
        await db.SaveChangesAsync();
        await live.Changed("ipads");
        return true;
    }

    /// <summary>Svaret på en kommando: status, fejl og resultat (DeviceInformation, profil installeret/fjernet).</summary>
    public static void ApplyResult(MdmDevice d, MdmCommand cmd, string? status, Dictionary<string, object>? report, DateTime now)
    {
        switch (status)
        {
            case "Acknowledged":
                cmd.Status = MdmCommandStates.Acknowledged; cmd.Completed ??= now; cmd.Error = null;
                if (cmd.RequestType == "DeviceInformation" && report?.Dict("QueryResponses") is { } q)
                {
                    MdmHook.ApplyInfo(d, q);
                    d.LastInventory = now;
                    cmd.ResultJson = MdmHook.ResultJson(report);
                }
                else if (cmd.RequestType == "InstallProfile" && cmd.Detail?.StartsWith(LockedDetailPrefix, StringComparison.Ordinal) == true)
                {
                    d.LockedInstalledHash = cmd.PayloadHash;
                    d.LockedInstalledVersion = int.TryParse(cmd.Detail[LockedDetailPrefix.Length..].Split(' ')[0], out var v) ? v : null;
                }
                else if (cmd.RequestType == "RemoveProfile" && cmd.Detail == LockedRemoved) { d.LockedInstalledHash = null; d.LockedInstalledVersion = null; }
                break;
            case "Error" or "CommandFormatError":
                cmd.Status = MdmCommandStates.Error; cmd.Completed ??= now;
                cmd.Error = (report is null ? null : MdmCommands.ErrorText(report)) ?? "iPad'en meldte en fejl";
                break;
            case "NotNow":
                if (cmd.Status != MdmCommandStates.Acknowledged) cmd.Status = MdmCommandStates.NotNow;
                break;
            default: return;
        }
        cmd.Updated = now;
    }

    /// <summary>OS-opdatering via DDM: iPad'en skal have <paramref name="version"/> senest <paramref name="localDeadline"/> (iPad'ens lokale tid).</summary>
    public async Task<MdmCommand> SetOsUpdate(Ipad p, MdmDevice d, string version, string? build, DateTime localDeadline, string by, CancellationToken ct = default)
    {
        if (d.EnrollmentId is null || d.State != MdmStates.Enrolled) throw new MdmException("iPad'en er ikke tilmeldt Apple-styring");
        if (!d.Approved) throw new MdmException("iPad'en venter på, at en administrator godkender den");
        var client = await ms.Client() ?? throw new MdmException("MDM-serveren er ikke sat op (Indstillinger → Apple-styring)");
        var set = MdmDeclarations.DeviceSet(p);
        var cmd = new MdmCommand
        {
            CommandUuid = Guid.NewGuid().ToString().ToUpperInvariant(), IpadId = p.Id, RequestType = "DDM: OS-opdatering", RequestedBy = by,
            Detail = $"iPadOS {version}{(build is { Length: > 0 } ? $" ({build})" : "")} senest {localDeadline:dd-MM-yyyy HH:mm}",
        };
        db.MdmCommands.Add(cmd);
        try
        {
            await client.PutDeclaration(MdmDeclarations.Status(), ct);
            await client.PutDeclaration(MdmDeclarations.OsUpdate(p, version, build, localDeadline), ct);
            await client.PutSetDeclaration(set, MdmDeclarations.StatusIdentifier, false, ct);
            await client.PutSetDeclaration(set, MdmDeclarations.OsUpdateIdentifier(p), false, ct);
            await client.PutEnrollmentSet(d.EnrollmentId, set, ct);   // giver iPad'en besked (DeclarativeManagement)
            cmd.Status = MdmCommandStates.Declared;
            d.OsTargetVersion = version; d.OsTargetBuild = build is { Length: > 0 } ? build : null;
            d.OsTargetLocal = localDeadline.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is MdmException or HttpRequestException or TaskCanceledException)
        {
            cmd.Status = MdmCommandStates.Failed; cmd.Error = e.Message;
        }
        cmd.Updated = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return cmd;
    }

    public async Task<MdmCommand> ClearOsUpdate(Ipad p, MdmDevice d, string by, CancellationToken ct = default)
    {
        var client = await ms.Client() ?? throw new MdmException("MDM-serveren er ikke sat op (Indstillinger → Apple-styring)");
        var cmd = new MdmCommand { CommandUuid = Guid.NewGuid().ToString().ToUpperInvariant(), IpadId = p.Id, RequestType = "DDM: OS-opdatering", RequestedBy = by, Detail = "Krav om OS-version fjernet" };
        db.MdmCommands.Add(cmd);
        try
        {
            await client.DeleteSetDeclaration(MdmDeclarations.DeviceSet(p), MdmDeclarations.OsUpdateIdentifier(p), ct);
            cmd.Status = MdmCommandStates.Declared;
            d.OsTargetVersion = null; d.OsTargetBuild = null; d.OsTargetLocal = null;
        }
        catch (Exception e) when (e is MdmException or HttpRequestException or TaskCanceledException) { cmd.Status = MdmCommandStates.Failed; cmd.Error = e.Message; }
        cmd.Updated = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return cmd;
    }

    /// <summary>Den låste profil, som den ville blive sendt via MDM lige nu (null = intet at sende).</summary>
    public async Task<(string Xml, string Hash)?> LockedForMdm(Ipad p) => lp.BuildForMdm(p, await fs.DohHost());
}

/// <summary>
/// Motoren for Apple-styring: holder øje med MDM-serveren, lægger push-certifikatet på den, sender den låste profil, når den ændres,
/// henter oplysninger hver 6. time, fanger tabte webhooks og advarer før push-certifikatet udløber. Fejl må aldrig vælte Fleetdeck.
/// </summary>
public sealed class MdmEngine(IServiceScopeFactory scopes, Notifier notifier, ILogger<MdmEngine> log) : BackgroundService
{
    private readonly Channel<bool> kick = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    public static readonly int[] ExpiryWarnDays = [30, 14, 7, 1, 0];
    public static readonly TimeSpan InventoryEvery = TimeSpan.FromHours(6), NotSeen = TimeSpan.FromHours(48), Backoff = TimeSpan.FromHours(6), StaleAfter = TimeSpan.FromHours(24);
    public bool? ServerOk { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? ServerError { get; private set; }
    public DateTime? ServerChecked { get; private set; }
    public string? LastError { get; private set; }
    private DateTime lastReconcile = DateTime.MinValue;

    public void Kick() => kick.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await Run(stop); LastError = null; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)   // bevidst bred: en fejl her må ikke stoppe hele serveren
            {
                LastError = e.Message; log.LogWarning(e, "Apple-styring: {Msg}", e.Message);
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
            wait.CancelAfter(TimeSpan.FromSeconds(60));
            try { await kick.Reader.ReadAsync(wait.Token); await Task.Delay(1000, stop); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        }
    }

    /// <summary>Hvilken advarselsgrænse (dage) push-certifikatet er nået, eller null.</summary>
    public static int? ExpiryThreshold(DateTime expires, DateTime now)
    {
        var days = (expires - now).TotalDays;
        int? hit = null;
        foreach (var t in ExpiryWarnDays) if (days <= t) hit = t;
        return hit;
    }

    private async Task Run(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDb>();
        var ms = sp.GetRequiredService<MdmSettings>();
        var svc = sp.GetRequiredService<MdmService>();
        var now = DateTime.UtcNow;

        await PushExpiry(ms, now);
        var client = await ms.Client();
        if (client is null) { ServerOk = null; return; }

        // 1. er MDM-serveren oppe?
        try { ServerVersion = await client.Version(ct); ServerOk = true; ServerError = null; }
        catch (Exception e) when (e is MdmException or HttpRequestException or TaskCanceledException) { ServerOk = false; ServerError = e.Message; }
        ServerChecked = now;
        if (ServerOk != true) return;

        // 2. push-certifikatet på MDM-serveren (igen efter en ny container)
        var cert = await ms.Get("push_cert");
        if (cert.Length > 0 && await ms.Secret("push_key") is { } key)
        {
            var fp = Crypto.Sha256(cert)[..16];
            var lastSync = DateTime.TryParse(await ms.Get("push_synced_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ls) ? ls : DateTime.MinValue;
            if (await ms.Get("push_synced") != fp || now - lastSync > TimeSpan.FromHours(1))
            {
                await client.PutPushCert(cert, key, ct);   // idempotent; dækker også en ny, tom container
                await ms.Set("push_synced", fp);
                await ms.Set("push_synced_at", now.ToString("O"));
            }
        }

        // 3. gamle kommandoer uden svar (fx tabte webhooks)
        await db.MdmCommands.Where(c => MdmCommandStates.Open.Contains(c.Status) && c.Created < now - StaleAfter)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, MdmCommandStates.Unknown).SetProperty(c => c.Updated, now), ct);

        var devices = await db.MdmDevices.Where(d => d.State != MdmStates.Pending && d.State != MdmStates.CheckedOut && d.Approved && d.EnrollmentId != null).ToListAsync(ct);
        if (devices.Count == 0) return;
        var ids = devices.Select(d => d.IpadId).ToList();
        var ipads = await db.Ipads.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var open = await db.MdmCommands.AsNoTracking().Where(c => MdmCommandStates.Open.Contains(c.Status) && ids.Contains(c.IpadId))
            .Select(c => new { c.IpadId, c.RequestType, c.PayloadHash, c.Detail }).ToListAsync(ct);

        // 4. tabte webhooks: står en iPad som "godkendt" uden push-token i over 10 min., prøves et push (lykkes det, er den tilmeldt)
        if (now - lastReconcile > TimeSpan.FromMinutes(15))
        {
            lastReconcile = now;
            var waiting = devices.Where(d => d.State == MdmStates.Authenticated && d.LastCheckin < now.AddMinutes(-10)).ToList();
            if (waiting.Count > 0)
            {
                var r = await client.Push(waiting.Select(d => d.EnrollmentId!), ct);
                foreach (var d in waiting)
                    if (r.PerId.GetValueOrDefault(d.EnrollmentId!) is { PushError: null, PushResult: { Length: > 0 } }) { d.State = MdmStates.Enrolled; d.EnrolledAt ??= now; }
            }
        }

        var pushIds = new List<string>();
        foreach (var d in devices.Where(d => d.State == MdmStates.Enrolled))
        {
            if (!ipads.TryGetValue(d.IpadId, out var p)) continue;
            var mine = open.Where(c => c.IpadId == d.IpadId).ToList();

            // 5. den låste profil via MDM: send, når indholdet er ændret (og ikke hvis den samme lige er afvist)
            if (d.LockedViaMdm)
            {
                if (await svc.LockedForMdm(p) is { } built && built.Hash != d.LockedInstalledHash
                    && !mine.Any(c => c.RequestType == "InstallProfile" && c.PayloadHash == built.Hash)
                    && !await db.MdmCommands.AnyAsync(c => c.IpadId == p.Id && c.PayloadHash == built.Hash && c.Created > now - Backoff
                        && (c.Status == MdmCommandStates.Error || c.Status == MdmCommandStates.Failed), ct))
                {
                    await svc.Send(p, d, MdmCommands.InstallProfile(built.Xml), "Fleetdeck", $"{MdmService.LockedDetailPrefix}{p.LockedProfileVersion}", built.Hash, push: false, ct);
                    pushIds.Add(d.EnrollmentId!);
                }
            }
            else if (d.LockedInstalledHash is not null && !mine.Any(c => c.RequestType == "RemoveProfile" && c.Detail == MdmService.LockedRemoved)
                && !await db.MdmCommands.AnyAsync(c => c.IpadId == p.Id && c.Detail == MdmService.LockedRemoved && c.Created > now - Backoff
                    && (c.Status == MdmCommandStates.Error || c.Status == MdmCommandStates.Failed), ct))
            {
                await svc.Send(p, d, MdmCommands.RemoveProfile(LockedProfile.Identifier(p)), "Fleetdeck", MdmService.LockedRemoved, push: false, ct: ct);
                pushIds.Add(d.EnrollmentId!);
            }

            // 6. oplysninger hver 6. time
            if ((d.LastInventory is null || d.LastInventory < now - InventoryEvery) && !mine.Any(c => c.RequestType == "DeviceInformation")
                && !await db.MdmCommands.AnyAsync(c => c.IpadId == p.Id && c.RequestType == "DeviceInformation" && c.Created > now - TimeSpan.FromHours(1), ct))
            {
                await svc.Send(p, d, MdmCommands.DeviceInformation(), "Fleetdeck", "Automatisk opdatering af oplysninger", push: false, ct: ct);
                pushIds.Add(d.EnrollmentId!);
            }

            // 7. ikke set i 48 timer (kun når MDM-serveren selv er oppe – ellers ville alle iPads melde på én gang)
            if (d.LastCheckin is { } lc && lc < now - NotSeen && !d.NotSeenWarned)
            {
                d.NotSeenWarned = true;
                notifier.Send($"🟠 {p.Name}: ikke set i {(now - lc).TotalHours:0} timer\niPad'en har ikke kontaktet Apple-styring siden {lc.ToLocalTime():d. MMM HH:mm}. Er den slukket, eller er MDM fjernet?", "warning");
            }
        }
        if (pushIds.Count > 0)
        {
            var r = await client.Push(pushIds.Distinct(), ct);
            foreach (var d in devices) if (r.PerId.TryGetValue(d.EnrollmentId!, out var x)) d.PushError = x.PushError;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task PushExpiry(MdmSettings ms, DateTime now)
    {
        var push = await ms.Push();
        if (push.Expires is not { } exp) return;
        if (ExpiryThreshold(exp, now) is not { } t) return;
        if (int.TryParse(await ms.Get("push_warned"), out var warned) && warned <= t) return;
        await ms.Set("push_warned", t.ToString(CultureInfo.InvariantCulture));
        var days = Math.Max(0, (int)Math.Floor((exp - now).TotalDays));
        notifier.Send(exp <= now
            ? "🔴 Apple-styring: push-certifikatet er udløbet\nFleetdeck kan ikke længere nå iPad'erne. Forny det under Indstillinger → Apple-styring (Renew hos Apple med samme Apple-konto)."
            : $"🟠 Apple-styring: push-certifikatet udløber om {days} dage\nUdløber {exp.ToLocalTime():d. MMMM yyyy}. Forny det under Indstillinger → Apple-styring (Renew hos Apple med samme Apple-konto).",
            exp <= now ? "critical" : "warning");
    }
}
