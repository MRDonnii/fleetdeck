using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>Versionsnummer efter SemVer: 3.16.0, 3.16.0-beta.2 (et "v" foran og "+byggedata" bagved ignoreres).</summary>
public readonly partial record struct SemVer(int Major, int Minor, int Patch, string Pre) : IComparable<SemVer>
{
    [GeneratedRegex(@"^[vV]?(\d{1,6})\.(\d{1,6})(?:\.(\d{1,6}))?(?:\.(\d{1,6}))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]*)?\z")]
    private static partial Regex Rx();

    public static SemVer? Parse(string? s)
    {
        if (s is null || Rx().Match(s.Trim()) is not { Success: true } m) return null;
        return new SemVer(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0, m.Groups[5].Success ? m.Groups[5].Value : "");
    }

    public bool IsPrerelease => Pre.Length > 0;
    /// <summary>Uden "-beta.1" – det, serverens /api/health melder som "version".</summary>
    public string Base => $"{Major}.{Minor}.{Patch}";
    public override string ToString() => Pre.Length > 0 ? $"{Base}-{Pre}" : Base;

    public int CompareTo(SemVer o)
    {
        var c = Major.CompareTo(o.Major);
        if (c == 0) c = Minor.CompareTo(o.Minor);
        if (c == 0) c = Patch.CompareTo(o.Patch);
        if (c != 0) return c;
        // en udgivelse er nyere end dens forhåndsudgaver: 3.16.0 > 3.16.0-beta.9
        if (Pre.Length == 0 || o.Pre.Length == 0) return (Pre.Length == 0 ? 1 : 0) - (o.Pre.Length == 0 ? 1 : 0);
        var a = Pre.Split('.'); var b = o.Pre.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return Math.Sign(c);
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
}

/// <summary>Én udgivelse af Fleetdeck (fra GitHub eller den lokale build-mappe).</summary>
public sealed record ReleaseInfo(
    string Version, string Tag, string Name, string Notes, bool Prerelease, DateTime? Published, string? Url,
    string? Image = null,           // Docker-billede angivet i udgivelsesnoterne ("Docker-billede: ghcr.io/…") – går forud for indstillingen
    string? ImageAssetUrl = null,   // billedet som fil (docker save) vedhæftet udgivelsen: fleetdeck-image-<version>.tar(.gz)
    string? LocalDir = null)        // lokal build: mappen med koden (som serveren ser den)
{
    [System.Text.Json.Serialization.JsonIgnore] public SemVer Sem => SemVer.Parse(Version) ?? default;
}

/// <summary>Udgivelser fra GitHub Releases og den lokale build-mappe.</summary>
public static partial class ReleaseFeed
{
    public const string Stable = "stable", Beta = "beta";

    [GeneratedRegex(@"(?im)^\s*(?:[-*]\s*)?(?:\*\*)?(?:docker-billede|docker-image|image|billede)(?:\*\*)?\s*:\s*`?([^\s`]+)`?\s*$")]
    private static partial Regex ImageLineRx();
    [GeneratedRegex(@"^fleetdeck-image[A-Za-z0-9._-]*\.tar(\.gz)?\z", RegexOptions.IgnoreCase)] private static partial Regex AssetRx();
    [GeneratedRegex(@"<Version>\s*([^<\s]+)\s*</Version>")] private static partial Regex PropsVersionRx();

    /// <summary>GitHubs /repos/{ejer}/{repo}/releases. Kladder og tags, der ikke er et versionsnummer, springes over.</summary>
    public static List<ReleaseInfo> ParseGitHub(string json)
    {
        var list = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Svaret fra GitHub er ikke en liste af udgivelser");
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            if (B("draft")) continue;
            var tag = S("tag_name");
            if (SemVer.Parse(tag) is not { } ver) continue;
            var notes = S("body").Replace("\r\n", "\n");
            if (notes.Length > 20000) notes = notes[..20000] + "\n…";
            string? image = ImageLineRx().Match(notes) is { Success: true } im && UpdateScript.ValidateImage(im.Groups[1].Value) is null ? im.Groups[1].Value : null;
            string? asset = null;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    if (AssetRx().IsMatch(name) && UpdateScript.ValidateAssetUrl(url) is null) { asset = url; break; }
                }
            var url0 = S("html_url");
            list.Add(new ReleaseInfo(ver.ToString(), tag, S("name") is { Length: > 0 } nm ? nm : tag, notes,
                B("prerelease") || ver.IsPrerelease,
                DateTime.TryParse(S("published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var pub) ? pub : null,
                url0.StartsWith("https://", StringComparison.Ordinal) ? url0 : null, image, asset));
        }
        return list.OrderByDescending(x => x.Sem).ToList();
    }

    /// <summary>Den nyeste udgivelse på kanalen: Stabil = kun udgivelser, der ikke er markeret som forhåndsudgave på GitHub.</summary>
    public static ReleaseInfo? Latest(IEnumerable<ReleaseInfo> releases, string channel) =>
        releases.Where(r => channel == Beta || !r.Prerelease).OrderByDescending(r => r.Sem).FirstOrDefault();

    /// <summary>Lokal build-mappe (fx /winsetup/Fleetdeck, som deploy/deploy.sh --source-only lægger koden i).</summary>
    public static ReleaseInfo ReadLocal(string dir)
    {
        var props = Path.Combine(dir, "Directory.Build.props");
        if (!File.Exists(props)) throw new FileNotFoundException($"Der er ingen Fleetdeck-kode i {dir} (Directory.Build.props mangler)");
        if (!File.Exists(Path.Combine(dir, "Dockerfile"))) throw new FileNotFoundException($"Der er ingen Dockerfile i {dir}");
        var ver = PropsVersionRx().Match(File.ReadAllText(props)) is { Success: true } m ? SemVer.Parse(m.Groups[1].Value) : null;
        if (ver is null) throw new InvalidDataException($"Fandt intet gyldigt versionsnummer i {props}");
        var notesFile = new[] { "RELEASE-NOTES.md", "BUILD-INFO.txt" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
        var notes = notesFile is null ? $"Lokal build fra {dir}." : File.ReadAllText(notesFile);
        if (notes.Length > 20000) notes = notes[..20000] + "\n…";
        return new ReleaseInfo(ver.Value.ToString(), "lokal", $"Lokal build {ver}", notes, false, File.GetLastWriteTimeUtc(props), null, LocalDir: dir);
    }
}

/// <summary>Hvor Fleetdeck henter opdateringer fra, og hvordan de lægges på.</summary>
public sealed record UpdateConfig(
    string Source, string Repo, string Channel, string ImageTemplate, string LocalDir,
    bool Auto, int AutoHour, int? MachineId, string Container, int HealthMinutes);

/// <summary>Fundet Fleetdeck-container på en maskine (fra agentens Unraid-rapport).</summary>
public sealed record UpdateHost(int MachineId, string Machine, string Container, string Image, int Score, string Why);

/// <summary>
/// Hvor opdateringen skal køre. OnHost: containerens navn er hverken skrevet eller fundet i agentens liste, så scriptet finder den selv
/// på værten ud fra serverens værtsnavn (= containerens id). Problem: hvorfor opdateringen ikke kan startes (vises i dialogen).
/// </summary>
public sealed record UpdateTarget(Machine? Machine, string Container, bool Detected, bool OnHost, string? Problem, string? Note, List<UpdateHost> Candidates);

/// <summary>Opdateringsjobbets indhold – kun kontrollerede værdier, aldrig hemmeligheder.</summary>
public sealed record UpdatePlan(
    string Run, string Container, string ExpectHost, string DataDir, string FromVersion, string ToVersion,
    string? Image, string? AssetUrl, string? LocalDir, string LocalTag, int HealthMinutes, int KeepBackups = 5);

/// <summary>
/// Scriptet, som agenten på Docker-værten kører for at opdatere Fleetdeck-containeren:
/// hent/indlæs/byg billedet → stop → backup af database og nøgler → ny container med samme navn, netværk, IP, mapper og miljø
/// (Unraid: skabelonen + rebuild_container) → vent på /api/health med den nye version → ellers tilbage til det gamle.
/// Selve arbejdet kører løsrevet fra agenten (setsid), så det gøres færdigt, også hvis jobbet stoppes eller agenten genstarter.
/// </summary>
public static partial class UpdateScript
{
    public const string Source = "selfupdate";
    public const string DefaultRepo = "MRDonnii/fleetdeck";
    public const string DefaultImage = "ghcr.io/mrdonnii/fleetdeck:{version}";
    public const string DefaultLocalDir = "/winsetup/Fleetdeck";
    public const string Delimiter = "FLEETDECK_OPDATERING_SLUT";

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}\z")] private static partial Regex RepoRx();
    // registry[:port]/sti/navn[:tag][@sha256:…] – kun tegn, der aldrig har betydning i bash eller sed
    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*(?::[0-9]{1,5})?(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?(?:@sha256:[a-f0-9]{64})?\z")]
    private static partial Regex ImageRx();
    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9._~%+/-]{1,400}\z")] private static partial Regex AssetUrlRx();
    [GeneratedRegex(@"^/[A-Za-z0-9._ /+-]{0,250}\z")] private static partial Regex DirRx();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")] internal static partial Regex HostRx();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,80}\z")] private static partial Regex RunRx();

    public static string? ValidateRepo(string? r) =>
        r is not null && RepoRx().IsMatch(r) ? null : "GitHub-projektet skrives som ejer/navn, fx " + DefaultRepo;

    public static string? ValidateImage(string? i) =>
        i is { Length: > 0 and <= 300 } && ImageRx().IsMatch(i) ? null : "Docker-billedet skal skrives som fx ghcr.io/ejer/fleetdeck:3.16.0 (små bogstaver)";

    public static string? ValidateAssetUrl(string? u) => u is not null && AssetUrlRx().IsMatch(u) && !u.Contains("..") ? null : "Ugyldig adresse til billedfilen";

    public static string? ValidateDir(string? d) =>
        d is not null && DirRx().IsMatch(d) && !d.Split('/').Contains("..") ? null : "Build-mappen skal være en fuld sti uden specialtegn, fx " + DefaultLocalDir;

    /// <summary>Skabelonen for billedet ({version} og {tag} udfyldes). Tom = brug billedfilen i udgivelsen.</summary>
    public static string? ValidateImageTemplate(string? t)
    {
        if (string.IsNullOrEmpty(t)) return null;
        return ValidateImage(Expand(t, "1.2.3", "v1.2.3"));
    }

    public static string Expand(string template, string version, string tag)
    {
        var s = template.Trim().Replace("{version}", version).Replace("{tag}", tag);
        // stien i et billednavn skal være med små bogstaver (GHCR: ejeren skrives fx Mr-Donnii på GitHub)
        var slash = s.LastIndexOf('/');
        var colon = s.IndexOf(':', slash + 1);
        return colon < 0 ? s.ToLowerInvariant() : s[..colon].ToLowerInvariant() + s[colon..];
    }

    /// <summary>
    /// Docker-billedet for en udgivelse. Noternes "Docker-billede:" bruges kun, når det er samme registry og navn som skabelonen
    /// (eller standardbilledet, hvis skabelonen er tom) – release-teksten kan aldrig pege selvopdateringen på et andet billede.
    /// </summary>
    public static string? ImageFor(ReleaseInfo r, string template)
    {
        if (r.LocalDir is not null) return null;
        var expected = Repository(Expand(string.IsNullOrWhiteSpace(template) ? DefaultImage : template, r.Version, r.Tag));
        if (r.Image is { } i && ValidateImage(i) is null && Repository(i) == expected) return i;
        if (string.IsNullOrWhiteSpace(template)) return null;
        var e = Expand(template, r.Version, r.Tag);
        return ValidateImage(e) is null ? e : null;
    }

    /// <summary>"registry/sti/navn" uden tag og digest (små bogstaver).</summary>
    public static string Repository(string image)
    {
        var s = image.Trim().ToLowerInvariant();
        var at = s.IndexOf('@'); if (at >= 0) s = s[..at];
        var slash = s.LastIndexOf('/');
        var colon = s.IndexOf(':', slash + 1);
        return colon < 0 ? s : s[..colon];
    }

    /// <summary>Billedfilen fra udgivelsen: kun fra det GitHub-projekt, opdateringerne hentes fra (…/{repo}/releases/download/…).</summary>
    public static string? AssetFor(ReleaseInfo r, string repo) =>
        r.ImageAssetUrl is { } u && ValidateRepo(repo) is null && ValidateAssetUrl(u) is null
        && u.StartsWith($"https://github.com/{repo}/releases/download/", StringComparison.OrdinalIgnoreCase) ? u : null;

    public static string? Validate(UpdatePlan p)
    {
        if (p.ExpectHost.Length > 0 && !HostRx().IsMatch(p.ExpectHost)) return "Ugyldigt værtsnavn";
        // tom container: scriptet finder den selv ud fra værtsnavnet – det kræver, at værtsnavnet er kendt
        if (p.Container.Length == 0 && p.ExpectHost.Length == 0) return MissingContainer;
        if (p.Container.Length > 0 && PublicAccess.ValidateContainer(p.Container) is { } c) return c;
        if (!RunRx().IsMatch(p.Run)) return "Ugyldigt kørsels-id";
        if (ValidateDir(p.DataDir) is { } dd) return dd;
        if (SemVer.Parse(p.FromVersion) is null || SemVer.Parse(p.ToVersion) is null) return "Ugyldigt versionsnummer";
        if (ValidateImage(p.LocalTag) is { } lt) return lt;
        var sources = (p.Image is null ? 0 : 1) + (p.AssetUrl is null ? 0 : 1) + (p.LocalDir is null ? 0 : 1);
        if (sources != 1) return "Udgivelsen har intet Docker-billede (hverken billede i registret, billedfil eller lokal build)";
        if (p.Image is not null && ValidateImage(p.Image) is { } im) return im;
        if (p.AssetUrl is not null && ValidateAssetUrl(p.AssetUrl) is { } au) return au;
        if (p.LocalDir is not null && ValidateDir(p.LocalDir) is { } ld) return ld;
        if (p.HealthMinutes is < 1 or > 60) return "Ventetiden skal være 1-60 minutter";
        if (p.KeepBackups is < 1 or > 50) return "Antal backups skal være 1-50";
        return null;
    }

    public const string MissingContainer = "Fleetdeck kunne ikke finde sin egen container. Skriv navnet fra docker ps i feltet Container under Indstillinger → Generelt → Opdatering.";

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Hele jobbet: skriver arbejds-scriptet, starter det løsrevet og viser dets output, mens det kører.</summary>
    public static JobParams Job(UpdatePlan p) => new() { Body = Build(p), TimeoutMin = 90 };

    public static string Build(UpdatePlan p)
    {
        var worker = Worker(p);
        if (worker.Split('\n').Any(l => l.Trim() == Delimiter)) throw new InvalidOperationException("Scriptet indeholder slutmarkøren");
        return $"# Fleetdeck: opdatering af selve Fleetdeck-serveren ({p.FromVersion} → {p.ToVersion})\nRUN={Q(p.Run)}\n" + """
            BASE=/tmp/fleetdeck-opdatering
            W="$BASE/$RUN"
            mkdir -p "$W" && chmod 700 "$BASE" "$W" || { echo "✗ Kunne ikke lave $W"; exit 1; }
            # tidligere kørsler: de 5 nyeste mapper gemmes
            ls -1dt "$BASE"/*/ 2>/dev/null | tail -n +6 | while IFS= read -r d; do [ "$d" = "$W/" ] || rm -rf -- "$d"; done
            cat > "$W/run.sh" <<'FLEETDECK_OPDATERING_SLUT'

            """ + worker + "\n" + Delimiter + "\n" + """
            : > "$W/log"; rm -f "$W/exit" "$W/pid"
            # løsrevet fra agenten (dobbelt fork + setsid): opdateringen gøres færdig, også hvis jobbet stoppes
            if command -v setsid >/dev/null 2>&1; then DETACH="setsid nohup"; else DETACH="nohup"; fi
            ( $DETACH bash -c 'echo $$ > "$1/pid"; bash "$1/run.sh" "$1" > "$1/log" 2>&1; echo $? > "$1/exit"' fleetdeck-opdatering "$W" < /dev/null > /dev/null 2>&1 & )
            n=0; waited=0; gone=0
            show() { t=$(( $(wc -l < "$W/log") )); if [ "$t" -gt "$n" ]; then sed -n "$((n + 1)),${t}p" "$W/log"; n=$t; fi; }
            while :; do
              show
              if [ -f "$W/exit" ]; then sleep 1; show; code=$(cat "$W/exit"); exit "${code:-1}"; fi
              if [ -s "$W/pid" ] && ! kill -0 "$(cat "$W/pid")" 2>/dev/null; then
                gone=$((gone + 1)); [ "$gone" -ge 3 ] && [ ! -f "$W/exit" ] && { show; echo "✗ Opdateringen stoppede uventet – se $W/log på maskinen"; exit 1; }
              fi
              waited=$((waited + 2))
              if [ "$waited" -ge 5100 ]; then echo "Opdateringen kører stadig i baggrunden – se $W/log på maskinen"; exit 1; fi
              sleep 2
            done
            """;
    }

    /// <summary>Arbejds-scriptet (kører løsrevet; $1 = arbejdsmappen).</summary>
    public static string Worker(UpdatePlan p)
    {
        if (Validate(p) is { } err) throw new ArgumentException(err);
        var to = SemVer.Parse(p.ToVersion)!.Value;
        var head = $"""
            # Fleetdeck: opdatering {p.FromVersion} → {p.ToVersion}
            NAME={Q(p.Container)}
            EXPECT_HOST={Q(p.ExpectHost)}
            DATA_DIR={Q(p.DataDir)}
            FROM_VER={Q(p.FromVersion)}
            TO_VER={Q(to.ToString())}
            TO_BASE={Q(to.Base)}
            IMAGE={Q(p.Image ?? "")}
            ASSET_URL={Q(p.AssetUrl ?? "")}
            LOCAL_DIR={Q(p.LocalDir ?? "")}
            LOCAL_TAG={Q(p.LocalTag)}
            HEALTH_MIN={p.HealthMinutes}
            KEEP={p.KeepBackups}

            """;
        return head + """
            WORK=${1:-/tmp/fleetdeck-opdatering/manuel}
            mkdir -p "$WORK"
            PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
            OLD="${NAME}-fd-forrige"
            TPL="/boot/config/plugins/dockerMan/templates-user/my-${NAME}.xml"
            REBUILD=/usr/local/emhttp/plugins/dynamix.docker.manager/scripts/rebuild_container
            STAGE=start; BK=""; TPL_BAK=""; HOSTDATA=""; OLD_ID=""; OLD_IMAGE_REF=""; OLD_IMAGE_ID=""; NEW=""; NEW_ID=""

            step() { echo "::ws $1 $2"; echo "→ $2"; }
            ok() { echo "  ✓ $*"; }
            fail() { echo "✗ $*"; exit 1; }
            in_list() { local x="$1" y; shift; for y in "$@"; do [ "$x" = "$y" ] && return 0; done; return 1; }
            ins() { docker inspect -f "$1" "$OLD" 2>/dev/null; }

            # docker pull uden terminal skriver én linje pr. lag: tæl dem, så bjælken kører op under hentningen
            # (hvert lag tæller to gange: hentet og pakket ud; et lag, der findes i forvejen, tæller dobbelt, men vises først ved næste hentede lag,
            # fordi docker nævner alle lag, før det første er hentet)
            pull_progress() {
              local line id total=0 units=0 seen=" " dl=" " fin=" " pct last=-1
              while IFS= read -r line; do
                printf '%s\n' "$line" >> "$WORK/pull.log"
                case "$line" in *": "*) id=${line%%:*} ;; *) continue ;; esac
                case "$id" in *[!0-9a-f]*|"") continue ;; esac
                case "$seen" in *" $id "*) ;; *) seen="$seen$id "; total=$((total + 1)) ;; esac
                case "$line" in
                  *": Download complete") case "$dl" in *" $id "*) ;; *) dl="$dl$id "; units=$((units + 1)) ;; esac ;;
                  *": Already exists") case "$fin" in *" $id "*) ;; *) fin="$fin$id "; dl="$dl$id "; units=$((units + 2)) ;; esac; continue ;;
                  *": Pull complete")
                    case "$fin" in *" $id "*) ;; *)
                      fin="$fin$id "
                      case "$dl" in *" $id "*) units=$((units + 1)) ;; *) dl="$dl$id "; units=$((units + 2)) ;; esac ;;
                    esac ;;
                  *) continue ;;
                esac
                pct=$(( units * 100 / (2 * total) ))
                if [ "$pct" -ne "$last" ]; then echo "  Henter lag: $pct %"; last=$pct; fi
              done
            }

            # sti inde i containeren → sti på værten (den længste mappe, der passer)
            host_path() {
              local p="$1" best="" src="" d s
              while IFS='|' read -r d s; do
                [ -n "$d" ] || continue
                case "$p/" in "${d%/}/"*) if [ ${#d} -gt ${#best} ]; then best=$d; src=$s; fi ;; esac
              done < <(docker inspect -f '{{range .Mounts}}{{.Destination}}|{{.Source}}{{println}}{{end}}' "$NAME" 2>/dev/null)
              [ -n "$best" ] || return 1
              echo "${src%/}${p#"${best%/}"}"
            }

            # /api/health fra containeren: socket-filen i datamappen, containerens IP eller (til sidst) inde i containeren
            health_body() {
              local b ip port
              if command -v curl >/dev/null 2>&1; then
                if [ -S "$HOSTDATA/fleetdeck.sock" ]; then
                  b=$(curl -fsS --max-time 5 --unix-socket "$HOSTDATA/fleetdeck.sock" http://localhost/api/health 2>/dev/null) && [ -n "$b" ] && { echo "$b"; return 0; }
                fi
                ip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' "$NAME" 2>/dev/null | awk '{print $1}')
                port=$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$NAME" 2>/dev/null | sed -n 's/^PORT=//p' | head -n 1)
                if [ -n "$ip" ]; then
                  b=$(curl -fsS --max-time 5 "http://$ip:${port:-8585}/api/health" 2>/dev/null) && [ -n "$b" ] && { echo "$b"; return 0; }
                fi
              fi
              if b=$(docker exec "$NAME" dotnet /app/Fleetdeck.Server.dll --healthcheck 2>/dev/null); then echo "${b:-ok}"; return 0; fi
              return 1
            }

            # vent på, at containeren svarer (med versionen $1, hvis den er givet)
            wait_health() {
              local want="$1" end b st seen=""
              end=$(( $(date +%s) + HEALTH_MIN * 60 ))
              while [ "$(date +%s)" -lt "$end" ]; do
                sleep 5
                st=$(docker inspect -f '{{.State.Status}}' "$NAME" 2>/dev/null)
                if [ "$st" = exited ] || [ "$st" = dead ]; then
                  echo "  ! Containeren er stoppet ($st). Sidste linjer fra dens log:"
                  docker logs --tail 15 "$NAME" 2>&1 | sed 's/^/    /'
                  return 1
                fi
                b=$(health_body) || continue
                [ -z "$want" ] && return 0
                case "$b" in *"\"release\":\"$want\""*|*"\"version\":\"$TO_BASE\""*) return 0 ;; esac
                if [ "$b" != "$seen" ]; then echo "  … svarer, men med en anden version: $b"; seen=$b; fi
              done
              return 1
            }

            restore_backup() {
              if [ -z "$BK" ] || [ ! -f "$BK/fleetdeck.db" ]; then echo "  ! Der er ingen database-backup at lægge tilbage"; return 1; fi
              rm -f "$HOSTDATA/fleetdeck.db-wal" "$HOSTDATA/fleetdeck.db-shm"
              cp -p "$BK"/fleetdeck.db* "$HOSTDATA/" && ok "Databasen er lagt tilbage fra $BK"
              if [ -d "$BK/keys" ]; then rm -rf "$HOSTDATA/keys" && cp -rp "$BK/keys" "$HOSTDATA/keys" && ok "Nøglerne er lagt tilbage"; fi
            }

            rollback() {
              step 90-99 "Ruller tilbage til Fleetdeck $FROM_VER"
              if docker inspect "$NAME" >/dev/null 2>&1 && [ "$(docker inspect -f '{{.Id}}' "$NAME")" != "$OLD_ID" ]; then
                docker stop -t 30 "$NAME" >/dev/null 2>&1; docker rm -f "$NAME" >/dev/null 2>&1 && ok "Den nye container er fjernet"
              fi
              restore_backup
              if [ -n "$TPL_BAK" ]; then cp -p "$TPL_BAK" "$TPL" && ok "Unraid-skabelonen peger igen på $OLD_IMAGE_REF"; fi
              # peger det gamle navn nu på det nye billede (samme tag), sættes det tilbage
              if [ -n "$OLD_IMAGE_REF" ] && [ "$(docker image inspect -f '{{.Id}}' "$OLD_IMAGE_REF" 2>/dev/null)" != "$OLD_IMAGE_ID" ]; then
                docker tag "$OLD_IMAGE_ID" "$OLD_IMAGE_REF" >/dev/null 2>&1
              fi
              if docker inspect "$OLD" >/dev/null 2>&1; then docker rename "$OLD" "$NAME" || { echo "✗ Kunne ikke give $OLD sit gamle navn igen – gør det i Docker"; return 1; }; fi
              docker start "$NAME" >/dev/null 2>&1
              if wait_health ""; then echo "✓ Fleetdeck $FROM_VER kører igen med databasen fra før opdateringen."
              else echo "✗ Fleetdeck $FROM_VER svarer ikke endnu – se containeren $NAME i Docker. Backup: $BK"; fi
            }

            # noget gik galt: læg alt tilbage, som det var
            abort() {
              echo "✗ $*"
              case "$STAGE" in
                stopped)
                  echo "Starter den tidligere version igen …"
                  docker start "$NAME" >/dev/null 2>&1
                  if wait_health ""; then echo "✓ Fleetdeck $FROM_VER kører igen – intet er ændret."; else echo "✗ Fleetdeck svarer ikke – se containeren $NAME i Docker."; fi ;;
                renamed|created) rollback ;;
              esac
              exit 1
            }

            # ny container med de samme indstillinger som den gamle (navn, netværk, fast IP, mapper, porte, miljø, mærker …)
            recreate() {
              local args=() imgenv=() imglab=() line n d rw ip p hip hp r c nm dests=" " v
              args+=(--name "$NAME")
              nm=$(ins '{{.HostConfig.NetworkMode}}')
              case "$nm" in ""|default) ;; *) args+=(--network "$nm") ;; esac
              while IFS='|' read -r n ip; do [ "$n" = "$nm" ] && [ -n "$ip" ] && args+=(--ip "$ip"); done \
                < <(ins '{{range $n, $e := .NetworkSettings.Networks}}{{$n}}|{{if $e.IPAMConfig}}{{$e.IPAMConfig.IPv4Address}}{{end}}{{println}}{{end}}')
              mapfile -t imgenv < <(docker image inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$OLD_IMAGE_ID" 2>/dev/null)
              while IFS= read -r line; do [ -n "$line" ] && ! in_list "$line" "${imgenv[@]}" && args+=(-e "$line"); done \
                < <(ins '{{range .Config.Env}}{{println .}}{{end}}')
              mapfile -t imglab < <(docker image inspect -f '{{range $k, $v := .Config.Labels}}{{$k}}={{$v}}{{println}}{{end}}' "$OLD_IMAGE_ID" 2>/dev/null)
              while IFS= read -r line; do [ -n "$line" ] && ! in_list "$line" "${imglab[@]}" && args+=(--label "$line"); done \
                < <(ins '{{range $k, $v := .Config.Labels}}{{$k}}={{$v}}{{println}}{{end}}')
              while IFS= read -r line; do
                [ -n "$line" ] || continue
                args+=(-v "$line"); d=$(printf '%s' "$line" | cut -d: -f2); dests="$dests$d "
              done < <(ins '{{range .HostConfig.Binds}}{{println .}}{{end}}')
              while IFS='|' read -r n d rw; do
                [ -n "$n" ] || continue
                case "$dests" in *" $d "*) continue ;; esac
                if [ "$rw" = false ]; then args+=(-v "$n:$d:ro"); else args+=(-v "$n:$d"); fi
              done < <(ins '{{range .Mounts}}{{if eq .Type "volume"}}{{.Name}}|{{.Destination}}|{{.RW}}{{println}}{{end}}{{end}}')
              while IFS='|' read -r p hip hp; do
                [ -n "$p" ] || continue
                case "$hip" in *:*) hip="[$hip]" ;; esac
                if [ -z "$hp" ]; then args+=(-p "$p"); elif [ -n "$hip" ]; then args+=(-p "$hip:$hp:$p"); else args+=(-p "$hp:$p"); fi
              done < <(ins '{{range $p, $b := .HostConfig.PortBindings}}{{range $b}}{{$p}}|{{.HostIp}}|{{.HostPort}}{{println}}{{end}}{{end}}')
              r=$(ins '{{.HostConfig.RestartPolicy.Name}}')
              case "$r" in ""|no) ;; on-failure) args+=(--restart "on-failure:$(ins '{{.HostConfig.RestartPolicy.MaximumRetryCount}}')") ;; *) args+=(--restart "$r") ;; esac
              while IFS= read -r c; do [ -n "$c" ] && args+=(--cap-add "$c"); done < <(ins '{{range .HostConfig.CapAdd}}{{println .}}{{end}}')
              while IFS= read -r c; do [ -n "$c" ] && args+=(--cap-drop "$c"); done < <(ins '{{range .HostConfig.CapDrop}}{{println .}}{{end}}')
              [ "$(ins '{{.HostConfig.Privileged}}')" = true ] && args+=(--privileged)
              while IFS= read -r c; do [ -n "$c" ] && args+=(--device "$c"); done < <(ins '{{range .HostConfig.Devices}}{{.PathOnHost}}:{{.PathInContainer}}:{{.CgroupPermissions}}{{println}}{{end}}')
              while IFS= read -r c; do [ -n "$c" ] && args+=(--add-host "$c"); done < <(ins '{{range .HostConfig.ExtraHosts}}{{println .}}{{end}}')
              while IFS= read -r c; do [ -n "$c" ] && args+=(--security-opt "$c"); done < <(ins '{{range .HostConfig.SecurityOpt}}{{println .}}{{end}}')
              while IFS= read -r c; do [ -n "$c" ] && args+=(--sysctl "$c"); done < <(ins '{{range $k, $v := .HostConfig.Sysctls}}{{$k}}={{$v}}{{println}}{{end}}')
              while IFS= read -r c; do [ -n "$c" ] && args+=(--tmpfs "$c"); done < <(ins '{{range $k, $v := .HostConfig.Tmpfs}}{{$k}}{{if $v}}:{{$v}}{{end}}{{println}}{{end}}')
              v=$(ins '{{.Config.Hostname}}'); case "$OLD_ID" in "$v"*) ;; *) [ -n "$v" ] && args+=(--hostname "$v") ;; esac
              v=$(ins '{{.HostConfig.LogConfig.Type}}'); [ -n "$v" ] && args+=(--log-driver "$v")
              while IFS= read -r c; do [ -n "$c" ] && args+=(--log-opt "$c"); done < <(ins '{{range $k, $v := .HostConfig.LogConfig.Config}}{{$k}}={{$v}}{{println}}{{end}}')
              v=$(ins '{{.HostConfig.Memory}}'); [ "${v:-0}" -gt 0 ] 2>/dev/null && args+=(--memory "$v")
              v=$(ins '{{.HostConfig.NanoCpus}}'); [ "${v:-0}" -gt 0 ] 2>/dev/null && args+=(--cpus "$(awk -v n="$v" 'BEGIN { printf "%.3f", n / 1000000000 }')")
              v=$(ins '{{.HostConfig.CpusetCpus}}'); [ -n "$v" ] && args+=(--cpuset-cpus "$v")
              if ! docker create "${args[@]}" "$NEW" > "$WORK/create.log" 2>&1; then sed 's/^/    /' "$WORK/create.log"; return 1; fi
              # øvrige netværk (med fast IP, hvis den gamle havde en)
              while IFS='|' read -r n ip; do
                [ -n "$n" ] && [ "$n" != "$nm" ] || continue
                if [ -n "$ip" ]; then docker network connect --ip "$ip" "$n" "$NAME"; else docker network connect "$n" "$NAME"; fi
              done < <(ins '{{range $n, $e := .NetworkSettings.Networks}}{{$n}}|{{if $e.IPAMConfig}}{{$e.IPAMConfig.IPv4Address}}{{end}}{{println}}{{end}}')
              docker start "$NAME" >/dev/null 2>&1
            }

            # ------------------------------------------------------------------ 1. kontrol
            mkdir -p /tmp/fleetdeck-opdatering
            exec 9> /tmp/fleetdeck-opdatering/.lock
            if command -v flock >/dev/null 2>&1; then flock -n 9 || fail "En anden opdatering af Fleetdeck kører allerede på maskinen"; fi
            step 2-5 "Kontrollerer Docker og containeren ${NAME:-(findes ud fra værtsnavnet)}"
            command -v docker >/dev/null 2>&1 || fail "Docker findes ikke på maskinen"
            docker info >/dev/null 2>&1 || fail "Docker kører ikke på maskinen"
            if [ -z "$NAME" ]; then
              # intet navn: find containeren, hvis id eller værtsnavn er Fleetdecks eget (samme regel som kontrollen nedenfor)
              for id in $(docker ps -aq --no-trunc 2>/dev/null); do
                hit=""
                if [ "${#EXPECT_HOST}" -ge 12 ]; then case "$id" in "$EXPECT_HOST"*) hit=1 ;; esac; fi
                [ -z "$hit" ] && [ "$(docker inspect -f '{{.Config.Hostname}}' "$id" 2>/dev/null)" = "$EXPECT_HOST" ] && hit=1
                if [ -n "$hit" ]; then NAME=$(docker inspect -f '{{.Name}}' "$id" 2>/dev/null); NAME=${NAME#/}; break; fi
              done
              [ -n "$NAME" ] || fail "Fandt ikke Fleetdeck-containeren på $(hostname): ingen container har id eller værtsnavn $EXPECT_HOST. Skriv navnet fra docker ps i feltet Container under Indstillinger → Generelt → Opdatering."
              OLD="${NAME}-fd-forrige"; TPL="/boot/config/plugins/dockerMan/templates-user/my-${NAME}.xml"
              ok "Fandt Fleetdeck-containeren: $NAME"
            fi
            docker inspect "$NAME" >/dev/null 2>&1 || fail "Containeren $NAME findes ikke på $(hostname)"
            if docker inspect "$OLD" >/dev/null 2>&1; then
              fail "Containeren $OLD fra en tidligere, afbrudt opdatering findes stadig. Kontrollér, at $NAME kører rigtigt, og slet så $OLD (docker rm $OLD)."
            fi
            OLD_ID=$(docker inspect -f '{{.Id}}' "$NAME")
            if [ -n "$EXPECT_HOST" ]; then
              v=$(docker inspect -f '{{.Config.Hostname}}' "$NAME")
              case "$OLD_ID" in
                "$EXPECT_HOST"*) ;;
                *) [ "$v" = "$EXPECT_HOST" ] || fail "Containeren $NAME er ikke den Fleetdeck, der bad om opdateringen (værtsnavn $v ≠ $EXPECT_HOST). Vælg den rigtige container under Indstillinger → Generelt." ;;
              esac
            fi
            OLD_IMAGE_REF=$(docker inspect -f '{{.Config.Image}}' "$NAME")
            OLD_IMAGE_ID=$(docker inspect -f '{{.Image}}' "$NAME")
            HOSTDATA=$(host_path "$DATA_DIR") || fail "Fandt ikke datamappen ($DATA_DIR) blandt containerens mapper"
            [ -f "$HOSTDATA/fleetdeck.db" ] || fail "Databasen findes ikke i $HOSTDATA"
            ok "$NAME kører $OLD_IMAGE_REF – data i $HOSTDATA"

            # ------------------------------------------------------------------ 2. det nye billede (mens den gamle version stadig kører)
            step 5-95 "Henter Fleetdeck $TO_VER"
            if [ -n "$LOCAL_DIR" ]; then
              SRC=$(host_path "$LOCAL_DIR") || fail "Build-mappen $LOCAL_DIR er ikke en af containerens mapper"
              [ -f "$SRC/Dockerfile" ] || fail "Der er ingen Dockerfile i $SRC"
              echo "  Bygger $LOCAL_TAG fra $SRC (det tager nogle minutter) …"
              if ! docker build -t "$LOCAL_TAG" "$SRC" > "$WORK/build.log" 2>&1; then
                tail -n 30 "$WORK/build.log" | sed 's/^/    /'; fail "Docker-billedet kunne ikke bygges"
              fi
              NEW=$LOCAL_TAG
            elif [ -n "$IMAGE" ]; then
              echo "  docker pull $IMAGE"
              : > "$WORK/pull.log"
              docker pull "$IMAGE" 2>&1 | pull_progress
              if [ "${PIPESTATUS[0]}" -ne 0 ]; then
                tail -n 5 "$WORK/pull.log" | sed 's/^/    /'
                if grep -qi "manifest unknown" "$WORK/pull.log"; then
                  fail "Billedet $IMAGE findes ikke endnu – det bliver nok stadig bygget på GitHub. Prøv igen om 10 minutter. Den kørende version er ikke rørt."
                fi
                fail "Kunne ikke hente $IMAGE (er billedet offentligt, og har maskinen adgang til internettet?)"
              fi
              NEW=$IMAGE
            else
              command -v curl >/dev/null 2>&1 || fail "curl findes ikke på maskinen"
              TAR="$HOSTDATA/.opdatering-billede.tar"
              echo "  Henter billedfilen fra udgivelsen …"
              curl -fsSL --retry 3 -o "$TAR" "$ASSET_URL" || { rm -f "$TAR"; fail "Kunne ikke hente billedfilen fra GitHub"; }
              L=$(docker load -i "$TAR" 2>&1); rc=$?; rm -f "$TAR"
              [ "$rc" -eq 0 ] || fail "Billedfilen kunne ikke indlæses: $L"
              NEW=$(printf '%s\n' "$L" | sed -n 's/^Loaded image: //p' | tail -n 1)
              if [ -z "$NEW" ]; then
                v=$(printf '%s\n' "$L" | sed -n 's/^Loaded image ID: //p' | tail -n 1)
                [ -n "$v" ] && docker tag "$v" "$LOCAL_TAG" && NEW=$LOCAL_TAG
              fi
              [ -n "$NEW" ] || fail "Billedfilen indeholdt intet Docker-billede"
            fi
            NEW_ID=$(docker image inspect -f '{{.Id}}' "$NEW" 2>/dev/null) || fail "Billedet $NEW findes ikke efter hentningen"
            ok "Billedet $NEW er klar"
            step 100-100 "Klar – Fleetdeck genstarter nu"

            # ------------------------------------------------------------------ 3. stop og backup
            step 100-100 "Genstarter Fleetdeck: stopper og tager backup af databasen"
            docker stop -t 60 "$NAME" >/dev/null 2>&1 || fail "Kunne ikke stoppe $NAME"
            STAGE=stopped
            BKROOT="$HOSTDATA/opdatering-backup"
            BK="$BKROOT/$(date +%Y%m%d-%H%M%S)-v$FROM_VER"
            mkdir -p "$BK" && chmod 700 "$BKROOT" "$BK" || abort "Kunne ikke lave backup-mappen $BK"
            for f in fleetdeck.db fleetdeck.db-wal fleetdeck.db-shm; do
              if [ -f "$HOSTDATA/$f" ]; then cp -p "$HOSTDATA/$f" "$BK/" || abort "Backup af $f fejlede (er disken fuld?)"; fi
            done
            if [ -d "$HOSTDATA/keys" ]; then cp -rp "$HOSTDATA/keys" "$BK/keys" || abort "Backup af nøglerne fejlede"; fi
            if [ -f "$TPL" ]; then cp -p "$TPL" "$BK/" && TPL_BAK="$BK/$(basename "$TPL")"; fi
            ok "Backup: $BK"
            ls -1d "$BKROOT"/*/ 2>/dev/null | sort -r | tail -n +$((KEEP + 1)) | while IFS= read -r d; do rm -rf -- "$d"; done

            # ------------------------------------------------------------------ 4. ny container
            step 100-100 "Genstarter Fleetdeck: starter $TO_VER"
            docker rename "$NAME" "$OLD" || abort "Kunne ikke omdøbe $NAME"
            STAGE=renamed
            MADE=""
            if [ -n "$TPL_BAK" ] && [ -x "$REBUILD" ]; then
              sed -i "s|<Repository>[^<]*</Repository>|<Repository>$NEW</Repository>|" "$TPL"
              echo "  Unraid: skabelonen peger nu på $NEW – genskaber containeren"
              "$REBUILD" "$NAME" > "$WORK/rebuild.log" 2>&1
              if docker inspect "$NAME" >/dev/null 2>&1; then MADE=unraid
              else tail -n 10 "$WORK/rebuild.log" | sed 's/^/    /'; echo "  ! Unraid lavede ikke containeren – den laves ud fra den gamle"; fi
            fi
            if [ -z "$MADE" ]; then recreate || abort "Den nye container kunne ikke oprettes"; MADE=docker; fi
            STAGE=created
            [ "$(docker inspect -f '{{.Image}}' "$NAME" 2>/dev/null)" = "$NEW_ID" ] || abort "Den nye container bruger ikke billedet $NEW"
            if [ "$(docker inspect -f '{{.State.Running}}' "$NAME")" != true ]; then docker start "$NAME" >/dev/null 2>&1 || abort "Den nye container kunne ikke starte"; fi
            ok "Containeren $NAME kører $NEW"

            # ------------------------------------------------------------------ 5. svarer den nye version?
            step 100-100 "Genstarter Fleetdeck: venter på, at $TO_VER svarer (højst $HEALTH_MIN min)"
            wait_health "$TO_VER" || abort "Fleetdeck $TO_VER svarede ikke rigtigt inden for $HEALTH_MIN minutter"
            step 100-100 "Rydder op"
            docker rm "$OLD" >/dev/null 2>&1
            echo "✓ Fleetdeck er opdateret fra $FROM_VER til $TO_VER."
            echo "  Database-backup: $BK (de $KEEP nyeste gemmes)"
            echo "  Det tidligere billede ($OLD_IMAGE_REF) er beholdt, så der kan gås tilbage."
            exit 0
            """;
    }
}

/// <summary>Søg efter, vis og start opdateringer af selve Fleetdeck (Indstillinger → Generelt).</summary>
public sealed partial class UpdateService(AppDb db, SettingsService settings, AuditService audit, JobService jobs, IHttpClientFactory http, Paths paths, ILogger<UpdateService> log)
{
    public sealed record Cache(DateTime? Checked, string? Error, List<ReleaseInfo> Releases, string Source);
    public sealed record State(int JobId, string From, string To, string By, DateTime Started, bool Auto, string? Result = null);

    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["update_source"] = "github", ["update_repo"] = UpdateScript.DefaultRepo, ["update_channel"] = ReleaseFeed.Stable,
        ["update_image"] = UpdateScript.DefaultImage, ["update_local_dir"] = UpdateScript.DefaultLocalDir,
        ["update_auto"] = "0", ["update_auto_hour"] = "3", ["update_machine_id"] = "", ["update_container"] = "", ["update_health_minutes"] = "5",
    };

    /// <summary>Kører serveren i en container? (uden Docker kan den ikke opdatere sig selv)</summary>
    public static bool InDocker => File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

    private async Task<string> Get(string k) => await settings.Get(k) is { Length: > 0 } v ? v : Defaults.GetValueOrDefault(k, "");

    private static T? Read<T>(string s) where T : class
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return JsonSerializer.Deserialize<T>(s, Json.Web); } catch (JsonException) { return null; }
    }

    public async Task<UpdateConfig> Config() => new(
        await Get("update_source") == "local" ? "local" : "github",
        await Get("update_repo"),
        await Get("update_channel") == ReleaseFeed.Beta ? ReleaseFeed.Beta : ReleaseFeed.Stable,
        await HasSetting("update_image") ? await settings.Get("update_image") : UpdateScript.DefaultImage,
        await Get("update_local_dir"),
        await Get("update_auto") == "1",
        int.TryParse(await Get("update_auto_hour"), out var h) && h is >= 0 and <= 23 ? h : 3,
        int.TryParse(await settings.Get("update_machine_id"), out var mid) && mid > 0 ? mid : null,
        await settings.Get("update_container"),
        int.TryParse(await Get("update_health_minutes"), out var hm) && hm is >= 1 and <= 60 ? hm : 5);

    // billedet må gerne være sat til tomt (= brug billedfilen i udgivelsen)
    private Task<bool> HasSetting(string k) => db.Settings.AsNoTracking().AnyAsync(s => s.Key == k);

    public async Task<string?> Save(UpdateConfig c, string? token, string by)
    {
        if (c.Source is not ("github" or "local")) return "Ukendt kilde";
        if (c.Channel is not (ReleaseFeed.Stable or ReleaseFeed.Beta)) return "Ukendt kanal";
        if (c.Source == "github" && UpdateScript.ValidateRepo(c.Repo) is { } re) return re;
        if (UpdateScript.ValidateImageTemplate(c.ImageTemplate) is { } ie) return ie;
        if (c.Source == "local" && UpdateScript.ValidateDir(c.LocalDir) is { } de) return de;
        if (c.Container.Length > 0 && PublicAccess.ValidateContainer(c.Container) is { } ce) return ce;
        if (c.MachineId is { } mid && !await db.Machines.AnyAsync(m => m.Id == mid)) return "Maskinen findes ikke";
        if (c.AutoHour is < 0 or > 23) return "Tidspunktet skal være 0-23";
        if (c.HealthMinutes is < 1 or > 60) return "Ventetiden skal være 1-60 minutter";
        var old = await Config();
        await settings.Set("update_source", c.Source);
        await settings.Set("update_repo", c.Repo.Trim());
        await settings.Set("update_channel", c.Channel);
        await settings.Set("update_image", c.ImageTemplate.Trim());
        await settings.Set("update_local_dir", c.LocalDir.Trim());
        await settings.Set("update_auto", c.Auto ? "1" : "0");
        await settings.Set("update_auto_hour", c.AutoHour.ToString(CultureInfo.InvariantCulture));
        await settings.Set("update_machine_id", c.MachineId?.ToString(CultureInfo.InvariantCulture) ?? "");
        await settings.Set("update_container", c.Container.Trim());
        await settings.Set("update_health_minutes", c.HealthMinutes.ToString(CultureInfo.InvariantCulture));
        if (token is not null) await settings.Set("update_token", token.Trim());
        // ny kilde/kanal: søg igen næste gang
        if (old.Source != c.Source || old.Repo != c.Repo || old.LocalDir != c.LocalDir) await settings.Set("update_cache", "");
        await audit.Log(by, "update.settings",
            $"kilde {c.Source}{(c.Source == "github" ? $" ({c.Repo}, {c.Channel})" : $" ({c.LocalDir})")}, automatisk {(c.Auto ? $"kl. {c.AutoHour}" : "fra")}"
            + $", maskine {c.MachineId?.ToString(CultureInfo.InvariantCulture) ?? "auto"}, container {(c.Container.Length > 0 ? c.Container : "auto")}"
            + (token is not null ? (token.Length > 0 ? ", ny GitHub-nøgle" : ", GitHub-nøgle fjernet") : ""));
        return null;
    }

    public async Task<Cache> Cached()
    {
        var c = Read<Cache>(await settings.Get("update_cache"));
        return c?.Releases is null ? new Cache(null, null, [], "") : c;
    }

    /// <summary>Hent udgivelserne fra kilden (GitHub eller den lokale build-mappe) og gem dem.</summary>
    public async Task<Cache> Check(CancellationToken ct = default)
    {
        var cfg = await Config();
        Cache c;
        try
        {
            if (cfg.Source == "local")
            {
                if (UpdateScript.ValidateDir(cfg.LocalDir) is { } e) throw new InvalidDataException(e);
                c = new Cache(DateTime.UtcNow, null, [ReleaseFeed.ReadLocal(cfg.LocalDir)], "local");
            }
            else
            {
                if (UpdateScript.ValidateRepo(cfg.Repo) is { } e) throw new InvalidDataException(e);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{cfg.Repo}/releases?per_page=30");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (await settings.Get("update_token") is { Length: > 0 } tok) req.Headers.Authorization = new("Bearer", tok);
                using var res = await http.CreateClient("update").SendAsync(req, ct);
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException(res.StatusCode switch
                    {
                        System.Net.HttpStatusCode.NotFound => $"GitHub-projektet {cfg.Repo} findes ikke (eller er privat – angiv en GitHub-nøgle)",
                        System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests => "GitHub afviser flere forespørgsler lige nu – prøv igen om lidt",
                        _ => $"GitHub svarede {(int)res.StatusCode}",
                    });
                c = new Cache(DateTime.UtcNow, null, ReleaseFeed.ParseGitHub(await res.Content.ReadAsStringAsync(ct)), "github");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            var old = await Cached();
            c = old with { Checked = DateTime.UtcNow, Error = e is TaskCanceledException ? "GitHub svarede ikke i tide" : e.Message };
        }
        await settings.Set("update_cache", Json.Ser(c));
        await settings.Set("update_checked", DateTime.UtcNow.ToString("O"));
        return c;
    }

    public static string Current => ThisVersion.Full;

    /// <summary>Find maskinen og containeren, Fleetdeck kører i.</summary>
    public static List<UpdateHost> Detect(IEnumerable<(int Id, string Label, UnraidInfo? Info)> machines, string selfHost, IReadOnlyCollection<string> selfIps)
    {
        var list = new List<UpdateHost>();
        foreach (var (id, label, info) in machines)
        {
            if (info is null) continue;
            foreach (var c in info.Containers)
            {
                var score = 0; var why = new List<string>();
                if (selfHost.Length >= 12 && c.Id is { Length: >= 12 } cid && cid.StartsWith(selfHost, StringComparison.OrdinalIgnoreCase)) { score += 100; why.Add("container-id"); }
                if (c.Ip is { Length: > 0 } ip && selfIps.Contains(ip) && c.Network is not ("host" or "bridge")) { score += 60; why.Add("IP-adresse"); }
                if (c.Name.Equals(selfHost, StringComparison.OrdinalIgnoreCase)) { score += 100; why.Add("værtsnavn"); }
                if (FleetdeckImage(c.Image)) { score += 30; why.Add("billede"); }
                else if (c.Name.Equals("fleetdeck", StringComparison.OrdinalIgnoreCase)) { score += 30; why.Add("navn"); }
                if (score >= 30) list.Add(new UpdateHost(id, label, c.Name, c.Image, score, string.Join(" + ", why)));
            }
        }
        return list.OrderByDescending(x => x.Score).ThenBy(x => x.Machine, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>fleetdeck:latest, ghcr.io/ejer/fleetdeck:3.16.0 … – men ikke fleetdeck-mdm eller tunnelen.</summary>
    public static bool FleetdeckImage(string image) => FleetdeckImageRx().IsMatch(image);
    [GeneratedRegex(@"(^|/)fleetdeck(:[^/]*)?(@sha256:[a-f0-9]+)?\z", RegexOptions.IgnoreCase)] private static partial Regex FleetdeckImageRx();

    public static HashSet<string> SelfIps()
    {
        var set = new HashSet<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address)) set.Add(a.Address.ToString());
        }
        catch (NetworkInformationException) { }
        return set;
    }

    public static string SelfHost => Environment.MachineName.ToLowerInvariant();

    /// <summary>Maskinen og containeren, opdateringen skal køre på (valgt i Indstillinger, ellers fundet automatisk).</summary>
    public async Task<UpdateTarget> Target(UpdateConfig cfg)
    {
        var linux = await db.Machines.AsNoTracking().Where(m => m.Platform == "linux").ToListAsync();
        var infos = linux.ToDictionary(m => m.Id, m => Json.Des<HealthReport>(m.HealthJson).Unraid);
        var found = Detect(linux.Select(m => (m.Id, m.Label, infos[m.Id])), SelfHost, SelfIps());
        Machine? machine = null; var container = cfg.Container; var detected = false;
        if (cfg.MachineId is { } mid) machine = linux.FirstOrDefault(m => m.Id == mid);
        if (machine is null && found.FirstOrDefault() is { } best) { machine = linux.First(m => m.Id == best.MachineId); detected = true; }
        if (container.Length == 0 && machine is not null && found.FirstOrDefault(f => f.MachineId == machine.Id) is { } fc) { container = fc.Container; detected = true; }
        var (problem, note) = Explain(machine?.Label, cfg.MachineId is not null && machine is null, linux.Count,
            machine is null ? null : infos[machine.Id]?.Containers.Count, container, SelfHost);
        var onHost = machine is not null && container.Length == 0 && problem is null;
        return new UpdateTarget(machine, container, detected, onHost, problem, note, found);
    }

    /// <summary>
    /// Hvorfor opdateringen ikke kan starte (problem), eller hvordan containeren findes (note), når feltet Container er tomt.
    /// containers: antal containere, agenten på værten melder (null = ingen Docker-oplysninger fra agenten).
    /// </summary>
    public static (string? Problem, string? Note) Explain(string? machine, bool chosenGone, int linuxCount, int? containers, string container, string selfHost)
    {
        const string where = "under Indstillinger → Generelt → Opdatering";
        if (machine is null)
            return (chosenGone ? $"Den valgte Docker-vært findes ikke længere. Vælg maskinen, Fleetdeck-containeren kører på, {where}."
                : linuxCount == 0 ? $"Ingen Linux-maskine har agenten. Installér agenten på Docker-værten, Fleetdeck kører på, og vælg den {where}."
                : $"Docker-værten er ikke fundet: ingen agent melder en container, der ligner Fleetdeck. Vælg maskinen, Fleetdeck kører på, {where}.", null);
        if (container.Length > 0) return (null, null);
        var why = containers is null or 0 ? $"agenten på {machine} melder ingen Docker-containere" : $"ingen af de {containers} containere, agenten på {machine} melder, ligner Fleetdeck";
        if (!UpdateScript.HostRx().IsMatch(selfHost))
            return ($"Fleetdeck kunne ikke finde sin egen container ({why}). Skriv navnet fra docker ps i feltet Container {where}.", null);
        return (null, $"{why} – containeren findes på værten, når opdateringen starter");
    }

    public async Task<State?> CurrentState() => Read<State>(await settings.Get("update_state")) is { JobId: > 0 } st ? st : null;

    /// <summary>Når opdateringsjobbet er færdigt: skriv resultatet i loggen (én gang).</summary>
    public async Task Settle()
    {
        if (await CurrentState() is not { Result: null } st) return;
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == st.JobId);
        string? result = null;
        if (SemVer.Parse(Current) is { } cur && SemVer.Parse(st.To) is { } to && cur.CompareTo(to) == 0 && st.From != st.To) result = "ok";
        else if (job is null || !JobStatus.Active.Contains(job.Status)) result = job?.Status == JobStatus.Success ? "ok" : "failed";
        if (result is null) return;
        await settings.Set("update_state", Json.Ser(st with { Result = result }));
        await audit.Log(st.By, result == "ok" ? "update.done" : "update.failed",
            result == "ok" ? $"Fleetdeck er opdateret fra {st.From} til {st.To}" : $"Opdateringen til {st.To} lykkedes ikke – Fleetdeck kører {Current} (job {st.JobId})");
    }

    public async Task<(int? JobId, string? Error)> Start(string version, bool allowOlder, string by, bool auto = false)
    {
        if (!InDocker) return (null, "Fleetdeck kører ikke i Docker, så den kan ikke opdatere sig selv herfra.");
        var cfg = await Config();
        var cache = await Cached();
        if (cfg.Source == "local") cache = await Check();
        var rel = cache.Releases.FirstOrDefault(r => r.Version == version);
        if (rel is null) return (null, $"Version {version} findes ikke i listen – søg efter opdateringer igen");
        var cur = SemVer.Parse(Current) ?? default;
        if (rel.Sem <= cur && !allowOlder)
            return (null, rel.Sem < cur ? $"{rel.Version} er ældre end den version, der kører ({Current}). Vælg \"Tillad ældre version\" for at gå tilbage."
                : $"{rel.Version} kører allerede. Vælg \"Tillad ældre version\" for at installere den igen.");
        if (await db.Jobs.AnyAsync(j => j.Source == UpdateScript.Source && JobStatus.Active.Contains(j.Status)))
            return (null, "En opdatering af Fleetdeck er allerede i gang");
        var target = await Target(cfg);
        if (target.Problem is { } problem || target.Machine is null) return (null, target.Problem ?? UpdateScript.MissingContainer);
        var (m, container) = (target.Machine, target.Container);
        var poll = await settings.GetNum("poll_seconds", 30);
        if (!MachineViews.Online(m, poll)) return (null, $"{m.Label} er offline – agenten på Docker-værten skal være forbundet");
        var image = UpdateScript.ImageFor(rel, cfg.ImageTemplate);
        var asset = image is null && rel.LocalDir is null ? UpdateScript.AssetFor(rel, cfg.Repo) : null;
        var run = $"{rel.Version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var plan = new UpdatePlan(run, container, SelfHost, paths.Data, Current, rel.Version, image, asset, rel.LocalDir,
            $"fleetdeck:{rel.Version}", cfg.HealthMinutes);
        if (UpdateScript.Validate(plan) is { } err) return (null, err);
        var downgrade = rel.Sem < cur;
        var job = await jobs.Create(m.Id, JobTypes.Script, $"Opdatér Fleetdeck til {rel.Version}", UpdateScript.Job(plan), RunAs.System, null, UpdateScript.Source, by);
        await settings.Set("update_state", Json.Ser(new State(job.Id, Current, rel.Version, by, DateTime.UtcNow, auto)));
        if (auto) await settings.Set("update_auto_last", rel.Version);
        await audit.Log(by, auto ? "update.auto_start" : "update.start",
            $"{Current} → {rel.Version} på {m.Label}/{container} ({image ?? (asset is not null ? "billedfil fra udgivelsen" : $"lokal build fra {rel.LocalDir}")})"
            + (downgrade ? ", NEDGRADERING" : rel.Sem.CompareTo(cur) == 0 ? ", geninstallation" : ""));
        return (job.Id, null);
    }

    /// <summary>Baggrund: søg efter opdateringer (hver 12. time; hver 6. med automatisk opdatering) og opdatér om natten, hvis det er slået til.</summary>
    public async Task Tick()
    {
        await Settle();
        var cfg = await Config();
        var last = DateTime.TryParse(await settings.Get("update_checked"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lc) ? lc : DateTime.MinValue;
        if (cfg.Source == "github" && DateTime.UtcNow - last > TimeSpan.FromHours(cfg.Auto ? 6 : 12)) await Check();
        if (!cfg.Auto || !InDocker || DateTime.Now.Hour != cfg.AutoHour) return;
        var cache = cfg.Source == "local" ? await Check() : await Cached();
        if (ReleaseFeed.Latest(cache.Releases, cfg.Channel) is not { } rel || SemVer.Parse(Current) is not { } cur || rel.Sem <= cur) return;
        if (await settings.Get("update_auto_last") == rel.Version) return;
        if (rel.Published is { } pub && DateTime.UtcNow - pub < TimeSpan.FromHours(2)) return;   // giv billedet tid til at blive bygget efter udgivelsen   // aldrig samme version to gange automatisk (fx efter tilbagerulning)
        var (id, err) = await Start(rel.Version, false, "automatisk", auto: true);
        if (err is not null)
        {
            await settings.Set("update_auto_last", rel.Version);
            await audit.Log("automatisk", "update.auto_skipped", $"{rel.Version}: {err}");
            log.LogWarning("Automatisk opdatering til {Version} blev ikke startet: {Error}", rel.Version, err);
        }
        else log.LogInformation("Automatisk opdatering til {Version} startet (job {Job})", rel.Version, id);
    }
}
