using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.DataProtection;

namespace Fleetdeck.Server.Services;

/// <summary>Tunnelens opsætning (uden nøglen – den står aldrig i et job).</summary>
public sealed record TunnelConfig(int? MachineId, string Network, string Ip, string Container);

/// <summary>
/// Offentlig adgang: en Cloudflare-tunnel (cloudflared i Docker) på en af husets Linux/Unraid-maskiner,
/// så iPad'ernes krypterede DNS (og senere MDM) virker uden for hjemmet.
/// Tunnel-nøglen gemmes krypteret og hentes af selve jobbet med en engangskode – den står aldrig i jobbet, output eller loggen.
/// </summary>
public static partial class PublicAccess
{
    /// <summary>Fast version af cloudflared (opdateres med Fleetdeck, ikke af sig selv).</summary>
    public const string Image = "cloudflare/cloudflared:2026.11.1";
    public const string Source = "public_access";
    public const string DefaultNetwork = "br0", DefaultContainer = "fleetdeck-tunnel";
    public const string StartTitle = "Offentlig adgang: start tunnel", StopTitle = "Offentlig adgang: stop tunnel", SuggestTitle = "Offentlig adgang: find en ledig IP";
    public const string TokenPath = "/api/agent/public-access/token";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\z")] private static partial Regex NetworkRx();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}\z")] private static partial Regex ContainerRx();
    [GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}\z")] private static partial Regex Ipv4Rx();
    [GeneratedRegex(@"eyJ[A-Za-z0-9+/_-]{20,}={0,2}")] private static partial Regex TokenLike();
    [GeneratedRegex(@"^[A-Za-z0-9+/_-]{40,4000}={0,2}\z")] private static partial Regex TokenRx();
    [GeneratedRegex(@"(?m)^FORSLAG=(\S+)\s*$")] private static partial Regex SuggestRx();

    /// <summary>Docker-netværket: navnet som i "docker network ls" (fx br0, host, bridge).</summary>
    public static string? ValidateNetwork(string? net)
    {
        if (string.IsNullOrWhiteSpace(net)) return "Skriv Docker-netværket (fx br0)";
        if (!NetworkRx().IsMatch(net)) return "Docker-netværket må kun indeholde bogstaver, tal, punktum, bindestreg og understreg";
        if (net == "none") return "Netværket \"none\" har ingen forbindelse – vælg fx br0 eller host";
        return null;
    }

    /// <summary>Fast IPv4-adresse til containeren (tom = Docker vælger selv).</summary>
    public static string? ValidateIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        if (!Ipv4Rx().IsMatch(ip)) return "Den faste IP skal være en IPv4-adresse som 192.168.1.250";
        var o = ip.Split('.').Select(int.Parse).ToArray();
        if (o[0] is 0 or 127 || o[0] >= 224) return "Den faste IP kan ikke bruges (0.x, 127.x og multicast er ikke tilladt)";
        if (o[3] is 0 or 255) return "Den faste IP må ikke ende på .0 eller .255";
        return null;
    }

    public static string? ValidateContainer(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Skriv et navn til containeren";
        return ContainerRx().IsMatch(name) ? null : "Containerens navn må kun indeholde bogstaver, tal, punktum, bindestreg og understreg (højst 63 tegn)";
    }

    /// <summary>Hele opsætningen; null når den er gyldig.</summary>
    public static string? Validate(TunnelConfig c) =>
        ValidateNetwork(c.Network) ?? ValidateIp(c.Ip) ?? ValidateContainer(c.Container)
        ?? (c.Ip.Length > 0 && c.Network is "host" or "bridge" ? $"En fast IP kan kun bruges på et macvlan/ipvlan-netværk (fx br0), ikke på \"{c.Network}\" – lad feltet være tomt" : null);

    /// <summary>
    /// Nøglen fra Cloudflare. Man kopierer ofte hele kommandoen ("cloudflared service install eyJ…"), så selve nøglen tages ud af den.
    /// Null, hvis det ikke ligner en tunnel-nøgle.
    /// </summary>
    public static string? CleanToken(string? input)
    {
        var t = (input ?? "").Trim();
        if (TokenLike().Match(t) is { Success: true } m) t = m.Value;
        else if (t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts) t = parts[^1];
        return TokenRx().IsMatch(t) ? t : null;
    }

    /// <summary>Skjul nøglen (og alt, der ligner en tunnel-nøgle) i tekst, der gemmes eller vises.</summary>
    public static string Mask(string text, string? token = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (token is { Length: >= 8 }) text = text.Replace(token, "***", StringComparison.Ordinal);
        return TokenLike().Replace(text, "***");
    }

    /// <summary>IP-forslaget fra "find en ledig IP"-jobbets output.</summary>
    public static string? SuggestedIp(string? output) =>
        SuggestRx().Matches(output ?? "").LastOrDefault() is { } m && ValidateIp(m.Groups[1].Value) is null ? m.Groups[1].Value : null;

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Opsætning, der er fælles for scriptene (kun kontrollerede værdier).</summary>
    private static string Head(TunnelConfig c) =>
        $"""
        # Fleetdeck: offentlig adgang (Cloudflare-tunnel)
        NAME={Q(c.Container)}
        NET={Q(c.Network)}
        IP={Q(c.Ip)}
        IMAGE={Q(Image)}
        fail() {"{"} echo "✗ $*"; exit 1; {"}"}
        command -v docker >/dev/null 2>&1 || fail "Docker findes ikke på maskinen"
        docker info >/dev/null 2>&1 || fail "Docker kører ikke på maskinen (Unraid: Settings → Docker → Enable Docker)"

        """;

    /// <summary>
    /// Start (eller genstart) tunnelen. Scriptet henter selv nøglen fra Fleetdeck med agentens login og engangskoden,
    /// og giver den videre til containeren som miljøvariabel – så den står hverken i jobbet, på kommandolinjen eller i output.
    /// </summary>
    /// <param name="origin">Hoved-AdGuards IP eller navn: tunnelen får navnet fleetdeck-dns peget derhen, så Cloudflare-hostnamet
    /// kan sende til http://fleetdeck-dns:port – det samme navn, som Reserve-DNS'ens connector peger på sin egen AdGuard.</param>
    public static JobParams StartJob(TunnelConfig c, string nonce, string? origin = null)
    {
        if (Validate(c) is { } err) throw new ArgumentException(err);
        if (!Regex.IsMatch(nonce, @"^[A-Za-z0-9_-]{16,128}\z")) throw new ArgumentException("Ugyldig engangskode");
        if (!ReserveDns.ValidOrigin(origin)) throw new ArgumentException("Ugyldig AdGuard-adresse");
        var body = Head(c) + $"NONCE={Q(nonce)}\nTOKEN_PATH={Q(TokenPath)}\nORIGIN={Q(origin ?? "")}\nALIAS={Q(ReserveDns.Alias)}\n" + """
            CFG=/etc/fleetdeck/agent.json
            command -v curl >/dev/null 2>&1 || fail "curl findes ikke på maskinen"
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

            # tunnel-nøglen: hentes fra Fleetdeck med agentens eget login (kun til dette job, kun én gang)
            [ -r "$CFG" ] || fail "Agentens opsætning ($CFG) kan ikke læses"
            SERVER=$(sed -n 's/^[[:space:]]*"Server":[[:space:]]*"\([^"]*\)".*/\1/p' "$CFG" | head -n 1)
            AGENT=$(sed -n 's/^[[:space:]]*"Token":[[:space:]]*"\([^"]*\)".*/\1/p' "$CFG" | head -n 1)
            [ -n "$SERVER" ] && [ -n "$AGENT" ] || fail "Agentens opsætning ($CFG) mangler server eller login"
            case "$SERVER" in
              unix:*) SOCK=${SERVER#unix:}; BASE=http://localhost ;;
              *) SOCK=; BASE=${SERVER%/} ;;
            esac
            get_token() {
              if [ -n "$SOCK" ]; then
                printf 'header = "Authorization: Bearer %s"\n' "$AGENT" | curl -fsS --max-time 20 --unix-socket "$SOCK" -K - "$BASE$TOKEN_PATH?nonce=$NONCE"
              else
                printf 'header = "Authorization: Bearer %s"\n' "$AGENT" | curl -fsS --max-time 20 -K - "$BASE$TOKEN_PATH?nonce=$NONCE"
              fi
            }
            TUNNEL_TOKEN=$(get_token) || fail "Tunnel-nøglen kunne ikke hentes fra Fleetdeck (start tunnelen igen fra Fleetdeck)"
            unset AGENT
            [ -n "$TUNNEL_TOKEN" ] || fail "Fleetdeck sendte ingen tunnel-nøgle"
            export TUNNEL_TOKEN

            echo "Henter $IMAGE …"
            docker pull -q "$IMAGE" >/dev/null || fail "$IMAGE kunne ikke hentes (er der internet på maskinen?)"
            if docker ps -a --format '{{.Names}}' | grep -Fqx "$NAME"; then
              echo "Fjerner den gamle container $NAME …"
              docker rm -f "$NAME" >/dev/null || fail "Den gamle container $NAME kunne ikke fjernes"
            fi
            set -- run -d --name "$NAME" --restart unless-stopped --network "$NET"
            if [ -n "$IP" ]; then set -- "$@" --ip "$IP"; fi
            # navnet fleetdeck-dns = hoved-AdGuard (bruges, når der også er en Reserve-DNS med sin egen connector)
            if [ -n "$ORIGIN" ]; then
              case "$ORIGIN" in
                *[!0-9.]*) OIP=$(getent ahostsv4 "$ORIGIN" 2>/dev/null | awk '{print $1; exit}') ;;
                *) OIP=$ORIGIN ;;
              esac
              if [ -n "$OIP" ]; then set -- "$@" --add-host "$ALIAS:$OIP"
              else echo "! $ORIGIN kunne ikke slås op – navnet $ALIAS virker ikke i tunnelen"; fi
            fi
            echo "Starter $NAME på netværket $NET${IP:+ med IP $IP} …"
            docker "$@" -e TUNNEL_TOKEN "$IMAGE" tunnel --no-autoupdate run >/dev/null || fail "Containeren $NAME kunne ikke startes"
            unset TUNNEL_TOKEN
            # Unraid: start den også, når Docker/arrayet startes igen
            AUTO=/var/lib/docker/unraid-autostart
            if [ -f /etc/unraid-version ] && [ -d /var/lib/docker ]; then
              touch "$AUTO"
              awk -v n="$NAME" '$1==n{f=1} END{exit !f}' "$AUTO" || echo "$NAME" >> "$AUTO"
            fi

            logs() { docker logs --tail 15 "$NAME" 2>&1 | sed -E 's#ey[J][A-Za-z0-9+/_=-]{20,}#***#g'; }
            ST=
            i=0
            while [ $i -lt 20 ]; do
              sleep 2; i=$((i+1))
              ST=$(docker inspect -f '{{.State.Status}}' "$NAME" 2>/dev/null)
              if docker logs "$NAME" 2>&1 | grep -q "Registered tunnel connection"; then
                echo "✓ Tunnelen kører og er forbundet til Cloudflare ($NAME på $NET${IP:+, $IP})"
                exit 0
              fi
              case "$ST" in exited|dead) break ;; esac
            done
            if [ "$ST" = running ]; then
              echo "! $NAME kører, men har ikke meldt forbindelse til Cloudflare endnu. Seneste log:"
              logs
              exit 0
            fi
            echo "✗ $NAME kører ikke (status: ${ST:-ukendt}) – er tunnel-nøglen rigtig? Seneste log:"
            logs
            exit 1
            """;
        return new JobParams { Body = body, TimeoutMin = 15 };
    }

    /// <summary>Stop tunnelen: fjern containeren (og Unraids autostart af den).</summary>
    public static JobParams StopJob(TunnelConfig c)
    {
        if (ValidateContainer(c.Container) is { } err) throw new ArgumentException(err);
        var body = Head(c with { Network = DefaultNetwork, Ip = "" }) + """
            if docker ps -a --format '{{.Names}}' | grep -Fqx "$NAME"; then
              docker rm -f "$NAME" >/dev/null || fail "Containeren $NAME kunne ikke fjernes"
              echo "✓ Tunnelen er stoppet, og containeren $NAME er fjernet"
            else
              echo "✓ Containeren $NAME findes ikke – tunnelen kører ikke"
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

    /// <summary>
    /// Find en ledig IP på Docker-netværkets subnet: oppefra, springer containere, gateway og maskinens egne adresser over,
    /// og prøver med ping (og ARP-tabellen, hvis en enhed ikke svarer på ping). Svaret står som FORSLAG=x.x.x.x i output.
    /// </summary>
    public static JobParams SuggestJob(TunnelConfig c)
    {
        if (ValidateNetwork(c.Network) is { } err) throw new ArgumentException(err);
        var body = Head(c with { Ip = "" }) + """
            CIDR=$(docker network inspect -f '{{range .IPAM.Config}}{{.Subnet}} {{end}}' "$NET" 2>/dev/null | tr ' ' '\n' | grep -E '^[0-9]+(\.[0-9]+){3}/[0-9]+$' | head -n 1)
            [ -n "$CIDR" ] || fail "Docker-netværket $NET findes ikke eller har intet IPv4-subnet (fast IP bruges på macvlan/ipvlan-netværk som br0)"
            GW=$(docker network inspect -f '{{range .IPAM.Config}}{{.Gateway}} {{end}}' "$NET" 2>/dev/null)
            CIPS=$(docker network inspect -f '{{range .Containers}}{{.IPv4Address}} {{end}}' "$NET" 2>/dev/null | sed 's#/[0-9]*##g')
            OWN=$(ip -4 -o addr show 2>/dev/null | awk '{split($4,a,"/"); printf "%s ", a[1]}')
            USED=" $GW $CIPS $OWN "
            toint() { set -- $(echo "$1" | tr . ' '); echo $(( ($1<<24) + ($2<<16) + ($3<<8) + $4 )); }
            toip() { echo "$(( ($1>>24)&255 )).$(( ($1>>16)&255 )).$(( ($1>>8)&255 )).$(( $1&255 ))"; }
            BITS=${CIDR#*/}
            [ "$BITS" -ge 16 ] && [ "$BITS" -le 29 ] || fail "Subnettet $CIDR kan der ikke søges i"
            SIZE=$(( 1 << (32-BITS) ))
            FIRST=$(( $(toint "${CIDR%/*}") / SIZE * SIZE ))
            C=$(( FIRST + SIZE - 6 ))
            N=0
            echo "Leder efter en ledig adresse på $CIDR …"
            while [ $C -gt $(( FIRST + 1 )) ] && [ $N -lt 40 ]; do
              CAND=$(toip $C); C=$((C-1))
              case "$USED" in *" $CAND "*) continue ;; esac
              N=$((N+1))
              if ping -c 1 -W 1 "$CAND" >/dev/null 2>&1; then continue; fi
              if ip neigh show "$CAND" 2>/dev/null | grep -q lladdr; then continue; fi
              echo "✓ $CAND svarer ikke og ser ledig ud. Vælg en adresse uden for routerens DHCP-område."
              echo "FORSLAG=$CAND"
              exit 0
            done
            fail "Fandt ingen ledig adresse blandt de øverste på $CIDR – skriv en selv"
            """;
        return new JobParams { Body = body, TimeoutMin = 5 };
    }
}

/// <summary>Tunnelens indstillinger. Nøglen gemmes krypteret (DataProtection) som AdGuard-adgangskoden.</summary>
public sealed class PublicAccessSettings(SettingsService s, IDataProtectionProvider dp)
{
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.Tunnel.Token");

    public async Task<TunnelConfig> Config() => new(
        int.TryParse(await s.Get("tunnel_host_machine_id"), out var id) && id > 0 ? id : null,
        await s.Get("tunnel_network") is { Length: > 0 } n ? n : PublicAccess.DefaultNetwork,
        await s.Get("tunnel_ip"),
        await s.Get("tunnel_container") is { Length: > 0 } c ? c : PublicAccess.DefaultContainer);

    public async Task Save(TunnelConfig c)
    {
        await s.Set("tunnel_host_machine_id", c.MachineId?.ToString() ?? "");
        await s.Set("tunnel_network", c.Network);
        await s.Set("tunnel_ip", c.Ip);
        await s.Set("tunnel_container", c.Container);
    }

    public async Task<bool> HasToken() => (await s.Get("tunnel_token")).Length > 0;

    public async Task<string?> Token()
    {
        var enc = await s.Get("tunnel_token");
        if (enc.Length == 0) return null;
        try { return protector.Unprotect(enc); } catch (CryptographicException) { return null; }
    }

    public Task SetToken(string token) => s.Set("tunnel_token", token.Length == 0 ? "" : protector.Protect(token));

    /// <summary>Engangskoden til start-jobbet: kun dens SHA-256 og jobbets id gemmes.</summary>
    public Task SetNonce(int jobId, string nonce) => s.Set("tunnel_nonce", $"{jobId}:{Crypto.Sha256(nonce)}");
    public Task ClearNonce() => s.Set("tunnel_nonce", "");

    /// <summary>Brug engangskoden: giver jobbets id og sletter koden, eller null hvis den ikke passer.</summary>
    public async Task<int?> TakeNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return null;
        var p = (await s.Get("tunnel_nonce")).Split(':', 2);
        if (p.Length != 2 || !int.TryParse(p[0], out var jobId) || !Crypto.FixedEquals(p[1], Crypto.Sha256(nonce))) return null;
        await ClearNonce();
        return jobId;
    }

    public async Task<string> Mask(string text) => PublicAccess.Mask(text, await Token());
}

/// <summary>Krypteret DNS (DoH): en rigtig DNS-forespørgsel (example.com, type A) til https://&lt;adresse&gt;/dns-query.</summary>
public static class DohProbe
{
    public static async Task<(bool Ok, string Text)> Run(string host, CancellationToken ct)
    {
        if (host.Length == 0) return (false, "Offentlig DNS-adresse mangler");
        try
        {
            byte[] q = [0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0, 0, 1, 0, 1];
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var res = await http.GetAsync($"https://{host}/dns-query?dns={Convert.ToBase64String(q).TrimEnd('=').Replace('+', '-').Replace('/', '_')}", ct);
            var body = await res.Content.ReadAsByteArrayAsync(ct);
            var good = res.IsSuccessStatusCode && res.Content.Headers.ContentType?.MediaType == "application/dns-message" && body.Length > 12;
            return (good, good ? $"Krypteret DNS svarer på https://{host}/dns-query" : $"https://{host}/dns-query svarede {(int)res.StatusCode} ({res.Content.Headers.ContentType?.MediaType})");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (false, $"https://{host} kunne ikke nås: {e.Message}"); }
    }
}
