using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Reserve-DNS'ens opsætning (uden hemmeligheder): hvor AdGuard-reserven og den ekstra tunnel-connector kører.</summary>
public sealed record ReserveDnsConfig(int? MachineId, string Network, string Ip, string Container, string TunnelContainer, string DataDir, string ApiUrl);

/// <summary>
/// Reserve-DNS: en ekstra AdGuard Home på en anden Docker-vært plus en ekstra cloudflared-connector til SAMME tunnel.
/// Cloudflare sender hver forespørgsel til den nærmeste connector og bruger de andre, hvis en connector forsvinder – så iPad'ernes
/// krypterede DNS virker videre, når hovedserveren er nede. Begge connectorer sender videre til navnet "fleetdeck-dns", som hver
/// vært selv peger på sin egen AdGuard (--add-host / --network-alias). Fleetdeck holder reservens indstillinger i takt med hoved-AdGuard.
/// </summary>
public static partial class ReserveDns
{
    /// <summary>Fast version af AdGuard Home (opdateres med Fleetdeck, ikke af sig selv).</summary>
    public const string Image = "adguard/adguardhome:v0.107.80";
    /// <summary>AdGuards config-skema i den version (bootstrap-filen skrives i det, så der ikke skal migreres).</summary>
    public const int SchemaVersion = 34;
    /// <summary>Navnet, Cloudflare-hostnamet sender til. Hver connector slår det op til sin egen, lokale AdGuard.</summary>
    public const string Alias = "fleetdeck-dns";
    public const string User = "fleetdeck";
    public const string Source = "reserve_dns";
    public const string DefaultContainer = "fleetdeck-dns-reserve", DefaultTunnelContainer = "fleetdeck-tunnel-reserve";
    public const string StartTitle = "Reserve-DNS: start", StopTitle = "Reserve-DNS: stop", SuggestTitle = "Reserve-DNS: find en ledig IP";
    public const string SecretsPath = "/api/agent/reserve-dns/secrets";
    public const string AlertKey = "reservedns";

    [GeneratedRegex(@"\$2[aby]\$\d\d\$[./A-Za-z0-9]{53}")] private static partial Regex BcryptRx();
    [GeneratedRegex(@"(?m)^AG_YAML=\S+")] private static partial Regex YamlLineRx();
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))*\z")] private static partial Regex HostRx();

    public static string? ValidateApiUrl(string? url) =>
        string.IsNullOrEmpty(url) || (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Query.Length == 0 && !url.Contains('\''))
            ? null : "Reservens AdGuard-adresse skal være en fuld adresse som http://192.168.1.53 (tom = automatisk)";

    /// <summary>Hele opsætningen; null når den er gyldig.</summary>
    public static string? Validate(ReserveDnsConfig c) =>
        PublicAccess.ValidateNetwork(c.Network) ?? PublicAccess.ValidateIp(c.Ip) ?? PublicAccess.ValidateContainer(c.Container) ?? PublicAccess.ValidateContainer(c.TunnelContainer)
        ?? MdmSidecar.ValidateDataDir(c.DataDir) ?? ValidateApiUrl(c.ApiUrl)
        ?? (c.Network == "bridge" ? "Docker-netværket \"bridge\" kan ikke bruges til reserven (navnet fleetdeck-dns kan ikke slås op der) – brug host, et macvlan-netværk som br0 med fast IP, eller et eget Docker-netværk" : null)
        ?? (c.Ip.Length > 0 && c.Network == "host" ? "En fast IP kan ikke bruges på \"host\" – lad feltet være tomt" : null)
        ?? (string.Equals(c.Container, c.TunnelContainer, StringComparison.OrdinalIgnoreCase) ? "AdGuard og tunnel-connectoren skal have hver sin container" : null);

    /// <summary>Porten på hoved-AdGuard (fra AdGuard-adressen). Reserven lytter på samme port, så Cloudflare-hostnamet passer til begge.</summary>
    public static int PortOf(string? adguardUrl) =>
        Uri.TryCreate(adguardUrl, UriKind.Absolute, out var u) && u.Scheme == "http" ? u.Port : 80;

    /// <summary>Det, Cloudflare-hostnamet skal sende til, når der er en reserve.</summary>
    public static string Service(int port) => $"http://{Alias}:{port}";

    /// <summary>Hoved-AdGuards vært (IP eller navn) fra AdGuard-adressen – tunnelen på hovedserveren peger fleetdeck-dns derhen.</summary>
    public static string? OriginHost(string? adguardUrl) =>
        Uri.TryCreate(adguardUrl, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && HostRx().IsMatch(u.Host) ? u.Host : null;

    public static bool ValidOrigin(string? host) => string.IsNullOrEmpty(host) || HostRx().IsMatch(host);

    /// <summary>Skjul bcrypt-hashen og bootstrap-filen i tekst, der gemmes eller vises.</summary>
    public static string Mask(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return YamlLineRx().Replace(BcryptRx().Replace(text, "***"), "AG_YAML=***");
    }

    /// <summary>
    /// AdGuards opsætningsfil til reserven. Skrives forfra hver gang reserven startes – alt andet (klienter, regler, filtre, DNS-indstillinger)
    /// lægger Fleetdeck på bagefter. Krypteret DNS svarer på almindelig http (tunnelen står for https), og forespørgsler fra private adresser
    /// (cloudflared) må angive klientens rigtige IP.
    /// </summary>
    public static string BootstrapYaml(int port, string bcryptHash, string? dnsBind)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("Ugyldig port");
        if (!BcryptRx().IsMatch(bcryptHash) || bcryptHash.Length != 60) throw new ArgumentException("Ugyldig adgangskode-hash");
        var bind = dnsBind is { Length: > 0 } b && PublicAccess.ValidateIp(b) is null ? b : "0.0.0.0";
        return $"""
            # Fleetdeck: Reserve-DNS. Skrives forfra, når reserven startes – resten synkroniseres fra hoved-AdGuard.
            http:
              address: 0.0.0.0:{port}
              doh:
                insecure_enabled: true
            users:
              - name: {User}
                password: "{bcryptHash}"
            dns:
              bind_hosts:
                - {bind}
              port: 53
              trusted_proxies:
                - 127.0.0.0/8
                - ::1/128
                - 10.0.0.0/8
                - 172.16.0.0/12
                - 192.168.0.0/16
            schema_version: {SchemaVersion}

            """;
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string Head(ReserveDnsConfig c, int port) =>
        $"""
        # Fleetdeck: Reserve-DNS (AdGuard Home + ekstra Cloudflare-connector til samme tunnel)
        NAME={Q(c.Container)}
        TNAME={Q(c.TunnelContainer)}
        NET={Q(c.Network)}
        IP={Q(c.Ip)}
        IMAGE={Q(Image)}
        CFIMAGE={Q(PublicAccess.Image)}
        DATA={Q(c.DataDir)}
        PORT={port}
        ALIAS={Q(Alias)}
        fail() {"{"} echo "✗ $*"; exit 1; {"}"}
        command -v docker >/dev/null 2>&1 || fail "Docker findes ikke på maskinen"
        docker info >/dev/null 2>&1 || fail "Docker kører ikke på maskinen (Unraid: Settings → Docker → Enable Docker)"
        if [ -z "$DATA" ]; then
          if [ -f /etc/unraid-version ]; then DATA=/mnt/user/appdata/$NAME; else DATA=/var/lib/$NAME; fi
        fi

        """;

    /// <summary>
    /// Start (eller genstart) reserven: AdGuard Home med en frisk opsætningsfil og en ekstra connector til den samme tunnel.
    /// Tunnel-nøglen og opsætningsfilen (med adgangskode-hash) hentes af jobbet med en engangskode – de står aldrig i jobbet eller output.
    /// </summary>
    public static JobParams StartJob(ReserveDnsConfig c, string nonce, int port)
    {
        if (Validate(c) is { } err) throw new ArgumentException(err);
        if (port is < 1 or > 65535) throw new ArgumentException("Ugyldig port");
        if (!Regex.IsMatch(nonce, @"^[A-Za-z0-9_-]{16,128}\z")) throw new ArgumentException("Ugyldig engangskode");
        var body = Head(c, port) + $"NONCE={Q(nonce)}\nSECRETS_PATH={Q(SecretsPath)}\n" + """
            CFG=/etc/fleetdeck/agent.json
            command -v curl >/dev/null 2>&1 || fail "curl findes ikke på maskinen"
            command -v base64 >/dev/null 2>&1 || fail "base64 findes ikke på maskinen"
            if [ "$NET" != host ]; then
              docker network inspect "$NET" >/dev/null 2>&1 || fail "Docker-netværket $NET findes ikke. Netværk på maskinen: $(docker network ls --format '{{.Name}}' | tr '\n' ' ')"
              DRIVER=$(docker network inspect -f '{{.Driver}}' "$NET" 2>/dev/null)
              case "$DRIVER" in
                macvlan|ipvlan) [ -n "$IP" ] || fail "På $NET ($DRIVER) skal reserven have en fast IP, så Fleetdeck og hjemmenettet kan nå den – brug Find" ;;
              esac
            fi
            if [ -n "$IP" ]; then
              USED=$(docker network inspect -f '{{range .Containers}}{{.Name}}={{.IPv4Address}} {{end}}' "$NET" 2>/dev/null | tr ' ' '\n' | grep -v "^$NAME=" | grep -F "=$IP/" | head -n 1)
              [ -z "$USED" ] || fail "$IP bruges allerede af containeren ${USED%%=*} på $NET"
              OLD=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$NAME" 2>/dev/null)
              case " $OLD " in
                *" $IP "*) ;;
                *) if ping -c 1 -W 1 "$IP" >/dev/null 2>&1; then fail "$IP svarer allerede på netværket – vælg en ledig adresse"; fi ;;
              esac
            fi

            # tunnel-nøglen og AdGuards opsætningsfil: hentes fra Fleetdeck med agentens eget login (kun til dette job, kun én gang)
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
            SECRETS=$(get_secrets) || fail "Nøglerne kunne ikke hentes fra Fleetdeck (start reserven igen fra Fleetdeck)"
            unset AGENT
            val() { printf '%s\n' "$SECRETS" | sed -n "s/^$1=//p" | head -n 1; }
            TUNNEL_TOKEN=$(val TUNNEL_TOKEN)
            AG_YAML=$(val AG_YAML)
            unset SECRETS
            [ -n "$TUNNEL_TOKEN" ] && [ -n "$AG_YAML" ] || fail "Fleetdeck sendte ikke tunnel-nøgle og opsætning"
            export TUNNEL_TOKEN

            mkdir -p "$DATA/conf" "$DATA/work" || fail "Datamappen $DATA kunne ikke oprettes"
            ( umask 077; printf '%s' "$AG_YAML" | base64 -d > "$DATA/conf/AdGuardHome.yaml.tmp" ) && mv "$DATA/conf/AdGuardHome.yaml.tmp" "$DATA/conf/AdGuardHome.yaml" \
              || fail "AdGuards opsætning kunne ikke gemmes i $DATA/conf"
            unset AG_YAML

            echo "Henter $IMAGE og $CFIMAGE …"
            docker pull -q "$IMAGE" >/dev/null || fail "$IMAGE kunne ikke hentes (er der internet på maskinen?)"
            docker pull -q "$CFIMAGE" >/dev/null || fail "$CFIMAGE kunne ikke hentes (er der internet på maskinen?)"
            for C in "$TNAME" "$NAME"; do
              if docker ps -a --format '{{.Names}}' | grep -Fqx "$C"; then
                echo "Fjerner den gamle container $C …"
                docker rm -f "$C" >/dev/null || fail "Den gamle container $C kunne ikke fjernes"
              fi
            done
            if [ "$NET" = host ]; then
              L=
              if command -v ss >/dev/null 2>&1; then L=$(ss -ltn 2>/dev/null | awk 'NR>1{print $4}')
              elif command -v netstat >/dev/null 2>&1; then L=$(netstat -ltn 2>/dev/null | awk 'NR>2{print $4}'); fi
              if printf '%s\n' "$L" | grep -Eq "[:.]$PORT\$"; then
                fail "Port $PORT er allerede i brug på maskinen – brug et macvlan-netværk (fx br0) med fast IP i stedet for host"
              fi
            fi

            # 1) AdGuard Home
            set -- run -d --name "$NAME" --restart unless-stopped --network "$NET" -v "$DATA/conf:/opt/adguardhome/conf" -v "$DATA/work:/opt/adguardhome/work"
            if [ "$NET" != host ]; then
              set -- "$@" --network-alias "$ALIAS"
              if [ -n "$IP" ]; then set -- "$@" --ip "$IP"; else set -- "$@" -p "$PORT:$PORT/tcp"; fi
            fi
            echo "Starter AdGuard Home ($NAME) på $NET${IP:+ med IP $IP} …"
            docker "$@" "$IMAGE" >/dev/null || fail "Containeren $NAME kunne ikke startes"
            ST=
            i=0
            while [ $i -lt 15 ]; do
              sleep 2; i=$((i+1))
              ST=$(docker inspect -f '{{.State.Status}}' "$NAME" 2>/dev/null)
              case "$ST" in exited|dead) break ;; esac
              if [ $i -ge 3 ] && [ "$ST" = running ]; then break; fi
            done
            if [ "$ST" != running ]; then
              echo "✗ AdGuard ($NAME) kører ikke (status: ${ST:-ukendt}). Seneste log:"
              docker logs --tail 15 "$NAME" 2>&1 | sed -E 's#\$2[aby]\$[0-9]{2}\$[./A-Za-z0-9]{53}#***#g'
              exit 1
            fi

            # 2) den ekstra tunnel-connector: fleetdeck-dns peger på AdGuard ved siden af
            set -- run -d --name "$TNAME" --restart unless-stopped --network "$NET"
            if [ "$NET" = host ]; then set -- "$@" --add-host "$ALIAS:127.0.0.1"
            elif [ -n "$IP" ]; then set -- "$@" --add-host "$ALIAS:$IP"; fi
            echo "Starter den ekstra tunnel-connector ($TNAME) …"
            docker "$@" -e TUNNEL_TOKEN "$CFIMAGE" tunnel --no-autoupdate run >/dev/null || fail "Containeren $TNAME kunne ikke startes"
            unset TUNNEL_TOKEN
            # Unraid: start dem også, når Docker/arrayet startes igen (AdGuard først)
            AUTO=/var/lib/docker/unraid-autostart
            if [ -f /etc/unraid-version ] && [ -d /var/lib/docker ]; then
              touch "$AUTO"
              for C in "$NAME" "$TNAME"; do
                awk -v n="$C" '$1==n{f=1} END{exit !f}' "$AUTO" || echo "$C" >> "$AUTO"
              done
            fi

            logs() { docker logs --tail 15 "$TNAME" 2>&1 | sed -E 's#ey[J][A-Za-z0-9+/_=-]{20,}#***#g'; }
            ST=
            i=0
            while [ $i -lt 20 ]; do
              sleep 2; i=$((i+1))
              ST=$(docker inspect -f '{{.State.Status}}' "$TNAME" 2>/dev/null)
              if docker logs "$TNAME" 2>&1 | grep -q "Registered tunnel connection"; then
                echo "✓ Reserve-DNS kører: AdGuard ($NAME på $NET${IP:+, $IP}, port $PORT) og en ekstra connector til tunnelen ($TNAME). Data: $DATA"
                echo "Fleetdeck lægger indstillingerne fra hoved-AdGuard på inden for et minut."
                exit 0
              fi
              case "$ST" in exited|dead) break ;; esac
            done
            if [ "$ST" = running ]; then
              echo "! AdGuard kører, og $TNAME kører, men har ikke meldt forbindelse til Cloudflare endnu. Seneste log:"
              logs
              exit 0
            fi
            echo "✗ $TNAME kører ikke (status: ${ST:-ukendt}) – er tunnel-nøglen rigtig? Seneste log:"
            logs
            exit 1
            """;
        return new JobParams { Body = body, TimeoutMin = 15 };
    }

    /// <summary>Stop reserven: fjern begge containere (og Unraids autostart af dem). AdGuards data bevares.</summary>
    public static JobParams StopJob(ReserveDnsConfig c)
    {
        if ((PublicAccess.ValidateContainer(c.Container) ?? PublicAccess.ValidateContainer(c.TunnelContainer)) is { } err) throw new ArgumentException(err);
        var body = Head(c with { Network = "host", Ip = "", DataDir = "" }, 80) + """
            for C in "$TNAME" "$NAME"; do
              if docker ps -a --format '{{.Names}}' | grep -Fqx "$C"; then
                docker rm -f "$C" >/dev/null || fail "Containeren $C kunne ikke fjernes"
                echo "✓ Containeren $C er fjernet"
              else
                echo "✓ Containeren $C findes ikke"
              fi
            done
            AUTO=/var/lib/docker/unraid-autostart
            if [ -f "$AUTO" ]; then
              awk -v n="$NAME" -v t="$TNAME" '$1!=n && $1!=t' "$AUTO" > "$AUTO.fd" && cat "$AUTO.fd" > "$AUTO"
              rm -f "$AUTO.fd"
            fi
            echo "✓ Reserve-DNS er stoppet (AdGuards data er bevaret i $DATA)"
            exit 0
            """;
        return new JobParams { Body = body, TimeoutMin = 5 };
    }
}

/// <summary>Et kald, der bringer reserven i takt med hoved-AdGuard.</summary>
public sealed record SyncOp(string Method, string Path, JsonNode? Body, string What);

/// <summary>Det, der synkroniseres, som AdGuard viser det i sit API.</summary>
public sealed record AgSnapshot(JsonObject Dns, JsonObject Filtering, JsonObject BlockedServices, JsonArray Clients, JsonArray Rewrites, JsonObject Access,
    bool SafeBrowsing, bool Parental, JsonObject SafeSearch, JsonObject QueryLog, JsonObject Stats)
{
    public static async Task<AgSnapshot> Read(AdGuardClient ag, CancellationToken ct)
    {
        async Task<JsonNode?> Get(string p) => await ag.Call(HttpMethod.Get, p, null, ct);
        async Task<JsonObject> Obj(string p) => await Get(p) as JsonObject ?? [];
        return new(
            await Obj("/control/dns_info"), await Obj("/control/filtering/status"), await Obj("/control/blocked_services/get"),
            (await Obj("/control/clients"))["clients"] as JsonArray ?? [], await Get("/control/rewrite/list") as JsonArray ?? [], await Obj("/control/access/list"),
            (await Obj("/control/safebrowsing/status"))["enabled"]?.GetValue<bool>() == true, (await Obj("/control/parental/status"))["enabled"]?.GetValue<bool>() == true,
            await Obj("/control/safesearch/status"), await Obj("/control/querylog/config"), await Obj("/control/stats/config"));
    }
}

/// <summary>
/// Synkroniseringen af reserven. iPad-klienterne og Fleetdecks regel-afsnit lægges på ud fra Fleetdecks ønskede tilstand (også når hoved-AdGuard er nede);
/// resten (DNS-indstillinger, filterlister, egne regler, spærrede tjenester, andre klienter, omskrivninger, adgang, SafeSearch, log/statistik) kopieres fra hoved-AdGuard.
/// Kun forskelle sendes.
/// </summary>
public static class ReserveDnsSync
{
    /// <summary>Felter i /control/dns_info, der ikke kan/skal sendes tilbage (læse-felter, en fil på hovedserveren, en midlertidig pause).</summary>
    private static readonly HashSet<string> DnsSkip = ["default_local_ptr_upstreams", "protection_disabled_until", "upstream_dns_file"];
    /// <summary>Felter i en klient, AdGuard kun viser (ikke tager imod).</summary>
    private static readonly HashSet<string> ClientSkip = ["disallowed", "disallowed_rule", "whois_info"];
    private const string IpadPrefix = "fd-ipad-";

    public static List<SyncOp> Plan(AgSnapshot? primary, AgSnapshot replica, IReadOnlyList<JsonObject> ipadClients, IReadOnlyList<string> ipadRules)
    {
        var ops = new List<SyncOp>();
        if (primary is not null)
        {
            var dns = new JsonObject();
            foreach (var (k, v) in primary.Dns) if (!DnsSkip.Contains(k)) dns[k] = v?.DeepClone();
            if (!Covers(dns, replica.Dns)) ops.Add(new("POST", "/control/dns_config", dns, "DNS-indstillinger"));

            var fc = new JsonObject { ["enabled"] = primary.Filtering["enabled"]?.DeepClone(), ["interval"] = primary.Filtering["interval"]?.DeepClone() };
            if (!Covers(fc, replica.Filtering)) ops.Add(new("POST", "/control/filtering/config", fc, "Filtrering"));
            FilterLists(ops, primary.Filtering["filters"] as JsonArray, replica.Filtering["filters"] as JsonArray, false);
            FilterLists(ops, primary.Filtering["whitelist_filters"] as JsonArray, replica.Filtering["whitelist_filters"] as JsonArray, true);
        }

        // egne regler: hoved-AdGuards (eller reservens egne, hvis hoved-AdGuard ikke svarer) med Fleetdecks iPad-afsnit
        var rules = AdGuardClient.ReplaceSection(Strings((primary ?? replica).Filtering["user_rules"]), ipadRules);
        if (!rules.SequenceEqual(Strings(replica.Filtering["user_rules"])))
            ops.Add(new("POST", "/control/filtering/set_rules", new JsonObject { ["rules"] = new JsonArray([.. rules.Select(r => (JsonNode)r)]) }, "Egne regler"));

        if (primary is not null)
        {
            if (!Same(primary.BlockedServices, replica.BlockedServices))
                ops.Add(new("PUT", "/control/blocked_services/update", primary.BlockedServices.DeepClone(), "Spærrede tjenester"));
            if (primary.SafeBrowsing != replica.SafeBrowsing)
                ops.Add(new("POST", primary.SafeBrowsing ? "/control/safebrowsing/enable" : "/control/safebrowsing/disable", null, "Sikker browsing"));
            if (primary.Parental != replica.Parental)
                ops.Add(new("POST", primary.Parental ? "/control/parental/enable" : "/control/parental/disable", null, "Børnefilter"));
            if (!Same(primary.SafeSearch, replica.SafeSearch))
                ops.Add(new("PUT", "/control/safesearch/settings", primary.SafeSearch.DeepClone(), "SafeSearch"));
            if (!Same(primary.QueryLog, replica.QueryLog))
                ops.Add(new("PUT", "/control/querylog/config/update", primary.QueryLog.DeepClone(), "Forespørgselslog"));
            if (!Same(primary.Stats, replica.Stats))
                ops.Add(new("PUT", "/control/stats/config/update", primary.Stats.DeepClone(), "Statistik"));
            Rewrites(ops, primary.Rewrites, replica.Rewrites);
            var acc = new JsonObject();
            foreach (var k in new[] { "allowed_clients", "disallowed_clients", "blocked_hosts" }) acc[k] = primary.Access[k]?.DeepClone() ?? new JsonArray();
            if (!Same(acc, replica.Access)) ops.Add(new("POST", "/control/access/set", acc, "Adgang"));
        }

        Clients(ops, primary, replica, ipadClients);
        return ops;
    }

    private static void Clients(List<SyncOp> ops, AgSnapshot? primary, AgSnapshot replica, IReadOnlyList<JsonObject> ipadClients)
    {
        var want = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (primary is not null)
            foreach (var c in primary.Clients.OfType<JsonObject>())
            {
                if (Name(c) is not { Length: > 0 } n || n.StartsWith(IpadPrefix, StringComparison.Ordinal)) continue;
                var copy = new JsonObject();
                foreach (var (k, v) in c) if (!ClientSkip.Contains(k)) copy[k] = v?.DeepClone();
                want[n] = copy;
            }
        foreach (var d in ipadClients) if (Name(d) is { Length: > 0 } n) want[n] = (JsonObject)d.DeepClone();

        var have = replica.Clients.OfType<JsonObject>().Where(c => Name(c) is { Length: > 0 }).ToDictionary(c => Name(c)!, StringComparer.Ordinal);
        // først sletning (frigiver id'er), så rettelser, så nye
        foreach (var n in have.Keys.Where(n => !want.ContainsKey(n) && (primary is not null || n.StartsWith(IpadPrefix, StringComparison.Ordinal))).Order(StringComparer.Ordinal))
            ops.Add(new("POST", "/control/clients/delete", new JsonObject { ["name"] = n }, $"Klient {n} (slet)"));
        foreach (var (n, c) in want.Where(x => have.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
            if (!Covers(c, have[n])) ops.Add(new("POST", "/control/clients/update", new JsonObject { ["name"] = n, ["data"] = c }, $"Klient {n}"));
        foreach (var (n, c) in want.Where(x => !have.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
            ops.Add(new("POST", "/control/clients/add", c, $"Klient {n} (ny)"));
    }

    private static void FilterLists(List<SyncOp> ops, JsonArray? primary, JsonArray? replica, bool white)
    {
        static Dictionary<string, JsonObject> ByUrl(JsonArray? a, bool remoteOnly) =>
            (a ?? []).OfType<JsonObject>().Select(f => (Url: f["url"]?.GetValue<string>() ?? "", F: f))
                .Where(x => x.Url.Length > 0 && (!remoteOnly || x.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || x.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => x.Url).ToDictionary(g => g.Key, g => g.First().F);
        var p = ByUrl(primary, true);       // lokale filer på hovedserveren kan ikke hentes af reserven
        var r = ByUrl(replica, false);
        var kind = white ? "Tilladelsesliste" : "Filterliste";
        foreach (var url in r.Keys.Where(u => !p.ContainsKey(u)).Order(StringComparer.Ordinal))
            ops.Add(new("POST", "/control/filtering/remove_url", new JsonObject { ["url"] = url, ["whitelist"] = white }, $"{kind} {url} (fjern)"));
        foreach (var (url, f) in p.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var name = f["name"]?.GetValue<string>() ?? "";
            var enabled = f["enabled"]?.GetValue<bool>() ?? true;
            JsonObject Set() => new() { ["url"] = url, ["whitelist"] = white, ["data"] = new JsonObject { ["name"] = name, ["url"] = url, ["enabled"] = enabled } };
            if (!r.TryGetValue(url, out var x))
            {
                ops.Add(new("POST", "/control/filtering/add_url", new JsonObject { ["name"] = name, ["url"] = url, ["whitelist"] = white }, $"{kind} {name} (ny)"));
                if (!enabled) ops.Add(new("POST", "/control/filtering/set_url", Set(), $"{kind} {name} (slået fra)"));
            }
            else if ((x["name"]?.GetValue<string>() ?? "") != name || (x["enabled"]?.GetValue<bool>() ?? true) != enabled)
                ops.Add(new("POST", "/control/filtering/set_url", Set(), $"{kind} {name}"));
        }
    }

    private static void Rewrites(List<SyncOp> ops, JsonArray primary, JsonArray replica)
    {
        static string Key(JsonObject o) => $"{o["domain"]?.GetValue<string>()}\n{o["answer"]?.GetValue<string>()}\n{o["enabled"]?.GetValue<bool>() ?? true}";
        var p = primary.OfType<JsonObject>().GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var r = replica.OfType<JsonObject>().GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var (k, o) in r.Where(x => !p.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
            ops.Add(new("POST", "/control/rewrite/delete", o.DeepClone(), $"Omskrivning {o["domain"]} (fjern)"));
        foreach (var (k, o) in p.Where(x => !r.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
            ops.Add(new("POST", "/control/rewrite/add", o.DeepClone(), $"Omskrivning {o["domain"]}"));
    }

    private static string? Name(JsonObject c) => c["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static List<string> Strings(JsonNode? n) => (n as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").ToList();

    /// <summary>Begge veje ens (bortset fra rækkefølge i tekstlister og tomme/manglende felter).</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => Covers(a, b) && Covers(b, a);

    /// <summary>
    /// Har <paramref name="actual"/> alt det, <paramref name="expected"/> siger? Felter, der kun findes i actual, ignoreres; manglende felter tæller som tomme
    /// (AdGuard udelader tomme lister og false), og lister af tekst sammenlignes uden rækkefølge.
    /// </summary>
    public static bool Covers(JsonNode? expected, JsonNode? actual)
    {
        if (IsEmpty(expected)) return IsEmpty(actual);
        switch (expected)
        {
            case JsonObject eo:
                return actual is JsonObject ao && eo.All(kv => Covers(kv.Value, ao[kv.Key]));
            case JsonArray ea:
                if (actual is not JsonArray aa) return false;
                if (ea.All(x => x is JsonValue v && v.TryGetValue<string>(out _)) && aa.All(x => x is JsonValue v && v.TryGetValue<string>(out _)))
                    return ea.Select(x => x!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(aa.Select(x => x!.GetValue<string>()).Order(StringComparer.Ordinal));
                return ea.Count == aa.Count && ea.Zip(aa).All(p => Covers(p.First, p.Second));
            case JsonValue ev:
                if (actual is not JsonValue av) return false;
                if (ev.TryGetValue<double>(out var ed) && av.TryGetValue<double>(out var ad)) return ed == ad;
                return JsonNode.DeepEquals(ev, av);
            default:
                return false;
        }
    }

    private static bool IsEmpty(JsonNode? n) => n switch
    {
        null => true,
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        JsonValue v => (v.TryGetValue<bool>(out var b) && !b) || (v.TryGetValue<string>(out var s) && s.Length == 0) || (v.TryGetValue<double>(out var d) && d == 0),
        _ => false,
    };

    /// <summary>Kør kaldene på reserven. Et kald, AdGuard afviser, stopper ikke resten; den første fejl meldes.</summary>
    public static async Task<(int Done, string? Error)> Run(AdGuardClient replica, IEnumerable<SyncOp> ops, CancellationToken ct)
    {
        var done = 0; string? error = null;
        foreach (var op in ops)
        {
            try { await replica.Call(new HttpMethod(op.Method), op.Path, op.Body?.DeepClone(), ct); done++; }
            catch (InvalidOperationException e) when (e.Message != "AdGuard afviste brugernavn/adgangskode") { error ??= $"{op.What}: {e.Message}"; }
        }
        return (done, error);
    }
}

/// <summary>Reservens tilstand, som IpadEngine sidst så den (i hukommelsen – nulstilles, når Fleetdeck genstarter).</summary>
public sealed record ReserveDnsHealth(int MachineId, bool Up, DateTime LastAttempt, DateTime? LastOk, DateTime? DownSince, string? Error,
    DateTime? LastSync, string? SyncError, int LastChanges, DateTime? LastChange, bool PrimaryOk, string? Version);

public static class ReserveDnsStatus
{
    /// <summary>Så længe må reserven være væk, før der slås alarm (en genstart skal ikke give besked).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(3);
    private static volatile ReserveDnsHealth? current;
    public static ReserveDnsHealth? Current { get => current; set => current = value; }

    /// <summary>Siden hvornår reserven har været væk længe nok til en alarm, ellers null.</summary>
    public static DateTime? Alarm(ReserveDnsHealth? h, DateTime now) => h is { Up: false, DownSince: { } since } && now - since >= Grace ? since : null;

    public static Finding? Finding(int machineId, DateTime now) =>
        Current is { } h && h.MachineId == machineId && Alarm(h, now) is { } since
            ? new(ReserveDns.AlertKey, "warning", "Reserve-DNS svarer ikke",
                $"AdGuard-reserven har ikke svaret siden {since.ToLocalTime():dd/MM HH:mm}: {h.Error}. Går hovedserveren ned, mister iPads internettet.")
            : null;
}

/// <summary>Reservens indstillinger. Adgangskoden til reservens AdGuard laves af Fleetdeck og gemmes krypteret.</summary>
public sealed class ReserveDnsSettings(SettingsService s, IDataProtectionProvider dp)
{
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.ReserveDns.Password");

    public async Task<ReserveDnsConfig> Config() => new(
        int.TryParse(await s.Get("reserve_machine_id"), out var id) && id > 0 ? id : null,
        await s.Get("reserve_network") is { Length: > 0 } n ? n : PublicAccess.DefaultNetwork,
        await s.Get("reserve_ip"),
        await s.Get("reserve_container") is { Length: > 0 } c ? c : ReserveDns.DefaultContainer,
        await s.Get("reserve_tunnel_container") is { Length: > 0 } t ? t : ReserveDns.DefaultTunnelContainer,
        await s.Get("reserve_data_dir"),
        await s.Get("reserve_api_url"));

    public async Task Save(ReserveDnsConfig c)
    {
        await s.Set("reserve_machine_id", c.MachineId?.ToString() ?? "");
        await s.Set("reserve_network", c.Network);
        await s.Set("reserve_ip", c.Ip);
        await s.Set("reserve_container", c.Container);
        await s.Set("reserve_tunnel_container", c.TunnelContainer);
        await s.Set("reserve_data_dir", c.DataDir);
        await s.Set("reserve_api_url", c.ApiUrl);
    }

    /// <summary>Startet fra Fleetdeck (og ikke stoppet igen): så synkroniseres og overvåges reserven.</summary>
    public async Task<bool> Enabled() => await s.Get("reserve_enabled") == "1";
    public Task SetEnabled(bool on) => s.Set("reserve_enabled", on ? "1" : "");

    public async Task<int> Port() => ReserveDns.PortOf(await s.Get("adguard_url"));

    public async Task<string> EnsurePassword()
    {
        if (await Password() is { } pw) return pw;
        pw = Crypto.Token(24);
        await s.Set("reserve_pass", protector.Protect(pw));
        return pw;
    }

    public async Task<string?> Password()
    {
        var enc = await s.Get("reserve_pass");
        if (enc.Length == 0) return null;
        try { return protector.Unprotect(enc); } catch (CryptographicException) { return null; }
    }

    /// <summary>Reservens AdGuard-adresse: angivet, ellers den faste IP, ellers maskinens egen IP (host-netværk eller udgivet port).</summary>
    public static string? ApiUrl(ReserveDnsConfig c, string? machineIp, int port)
    {
        if (c.ApiUrl.Trim().TrimEnd('/') is { Length: > 0 } u) return u;
        if (c.Ip.Length > 0) return $"http://{c.Ip}:{port}";
        return machineIp is { Length: > 0 } ip && PublicAccess.ValidateIp(ip) is null ? $"http://{ip}:{port}" : null;
    }

    /// <summary>Klienten til reservens AdGuard, når reserven er startet fra Fleetdeck.</summary>
    public async Task<(AdGuardClient Client, int MachineId)?> Client(AppDb db, CancellationToken ct = default)
    {
        if (!await Enabled()) return null;
        var c = await Config();
        if (c.MachineId is not int mid || await Password() is not { } pw) return null;
        var ip = await db.Machines.AsNoTracking().Where(m => m.Id == mid).Select(m => m.Ip).FirstOrDefaultAsync(ct);
        return ApiUrl(c, ip, await Port()) is { } url ? (new AdGuardClient(url, ReserveDns.User, pw), mid) : null;
    }

    // ---- engangskoden til start-jobbet (som tunnelen)
    public Task SetNonce(int jobId, string nonce) => s.Set("reserve_nonce", $"{jobId}:{Crypto.Sha256(nonce)}");
    public Task ClearNonce() => s.Set("reserve_nonce", "");
    public async Task<int?> TakeNonce(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return null;
        var p = (await s.Get("reserve_nonce")).Split(':', 2);
        if (p.Length != 2 || !int.TryParse(p[0], out var jobId) || !Crypto.FixedEquals(p[1], Crypto.Sha256(nonce))) return null;
        await ClearNonce();
        return jobId;
    }

    /// <summary>Det, start-jobbet henter: tunnel-nøglen og AdGuards opsætningsfil (base64) på hver sin linje.</summary>
    public static string Secrets(string tunnelToken, string yaml) =>
        $"TUNNEL_TOKEN={tunnelToken}\nAG_YAML={Convert.ToBase64String(Encoding.UTF8.GetBytes(yaml))}\n";

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, 10);
}
