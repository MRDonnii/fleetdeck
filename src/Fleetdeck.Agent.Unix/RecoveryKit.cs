using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Gendannelsespunkt: pakker maskinens opsætning (profilens stier + egne systemd-tjenester, scripts de bruger, udev-regler,
/// boot-opsætning og pakkeliste) og sender den til Fleetdeck. Pakningen læser kun og kører med laveste prioritet.
/// Gendannelse overskriver ALDRIG eksisterende filer og starter kun tjenester, der ikke kører – en kørende maskine forstyrres ikke.
/// </summary>
internal static class RecoveryKit
{
    private const string Work = UnixRoot.KitWork;   // root-ejet, 0700 (aldrig /var/tmp)

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
    /// <summary>Kun almindelige stier og *-mønstre (de udfoldes af bash) – intet, der kan blive til en kommando.</summary>
    public static bool SafePath(string p) => System.Text.RegularExpressions.Regex.IsMatch(p, @"^/[\w./*\-]+$") && !p.Contains("..");

    public static string BuildScript(List<string> paths, string outFile) => $$"""
        set -u
        umask 077
        W={{Work}}; rm -rf "$W/manifest"; mkdir -p "$W/manifest"; M="$W/manifest"
        . /etc/os-release 2>/dev/null; echo "$PRETTY_NAME $(uname -m) $(hostname) $(date -Is) dpkg=$(dpkg --print-architecture 2>/dev/null || echo ukendt)" > "$M/system.txt"
        command -v apt-mark >/dev/null && apt-mark showmanual > "$M/apt-manual.txt" 2>/dev/null
        # egne tjenester (dem i /etc/systemd/system) og om de er slået til
        : > "$M/units.txt"
        for f in /etc/systemd/system/*.service /etc/systemd/system/*.timer /etc/systemd/system/*.mount /etc/systemd/system/*.path; do
          [ -f "$f" ] || continue; u=$(basename "$f"); echo "$u $(systemctl is-enabled "$u" 2>/dev/null) $(systemctl is-active "$u" 2>/dev/null)" >> "$M/units.txt"
        done
        # filer, som tjenesterne starter (ExecStart m.fl.) uden for de faste stier
        : > "$M/extra.txt"
        for f in /etc/systemd/system/*.service; do
          [ -f "$f" ] || continue
          systemctl cat "$(basename "$f")" 2>/dev/null | grep -E '^(Exec[A-Za-z]*|EnvironmentFile|WorkingDirectory)=' | grep -oE '/[^ "'\'';]+' | while read -r p; do
            case "$p" in /usr/bin/*|/bin/*|/usr/sbin/*|/sbin/*|/usr/lib/*|/lib/*|/dev/*|/proc/*|/sys/*|/run/*|/tmp/*) continue;; esac
            [ -e "$p" ] && echo "$p"
          done >> "$M/extra.txt"
        done
        # boot- og netværksopsætning gemmes som reference (lægges ikke automatisk tilbage – kan afhænge af SD/netboot)
        for f in /boot/firmware/config.txt /boot/config.txt /boot/firmware/cmdline.txt /boot/cmdline.txt /etc/fstab /etc/modules /etc/hosts /etc/hostname; do
          [ -f "$f" ] && mkdir -p "$M/reference$(dirname "$f")" && cp -p "$f" "$M/reference$f"
        done
        L="$W/list.txt"; : > "$L"
        for p in {{string.Join(" ", paths.Where(SafePath))}} /etc/systemd/system /etc/udev/rules.d /etc/modules-load.d /etc/modprobe.d; do
          for e in $p; do [ -e "$e" ] && echo "${e#/}" >> "$L"; done
        done
        sort -u "$M/extra.txt" | sed 's#^/##' >> "$L"
        echo "${M#/}" >> "$L"
        sort -u -o "$L" "$L"
        nice -n 19 ionice -c3 tar --ignore-failed-read --warning=no-file-changed --exclude='*/__pycache__' --exclude='*.pyc' \
          -czf {{Q(outFile)}} -C / -T "$L"
        rc=$?; [ $rc -le 1 ] || exit $rc
        echo "Pakket: $(wc -l < "$L") stier, $(du -h {{Q(outFile)}} | cut -f1)"
        """;

    /// <summary>Gendan på den maskine, der kører. Med <paramref name="newCard"/> (et nyt SD-kort fra "Lav SD-kort"): det udeladte tages ikke
    /// med, pakker, der ikke findes i den nye udgave, bliver en advarsel i stedet for at stoppe det hele, og fremdriften meldes i trin.
    /// Boot-opsætningen lægges aldrig tilbage – den blev flettet ind, da kortet blev bygget.</summary>
    public static string RestoreScript(string tarFile, bool newCard = false, IReadOnlyList<string>? exclude = null)
    {
        var excl = (exclude ?? []).Where(SafeExclude).ToList();
        if (!newCard) return RestoreScriptPlain(tarFile);
        return $$"""
        set -u
        T={{Q(tarFile)}}
        echo "::ws 5-15 Læser gendannelsespunktet"
        echo "Nyt SD-kort: gendanner fra $(basename "$T") – eksisterende filer overskrives ikke."
        rm -rf {{Work}}/manifest
        EX=(--exclude='*/__pycache__' --exclude='*.pyc'{{string.Concat(excl.Select(e => " --exclude=" + Q(e)))}})
        for e in {{string.Join(" ", excl.Select(Q))}}; do echo "Udeladt: $e"; done
        echo "::ws 15-45 Filer"
        tar --skip-old-files --keep-directory-symlink "${EX[@]}" -xzf "$T" -C / 2>&1 | grep -v 'Already exists' | tail -20
        M={{Work}}/manifest
        echo "::ws 45-75 Pakker"
        if [ -s "$M/apt-manual.txt" ] && command -v apt-get >/dev/null; then
          need=$(comm -23 <(sort -u "$M/apt-manual.txt") <(apt-mark showmanual | sort -u) | tr '\n' ' ')
          if [ -n "$need" ]; then
            DEBIAN_FRONTEND=noninteractive apt-get update -qq
            echo "Installerer manglende pakker: $need"
            if ! DEBIAN_FRONTEND=noninteractive apt-get install -y -qq $need >/tmp/fd-apt.log 2>&1; then
              # én pakke, der ikke findes i den nye udgave, må ikke stoppe resten: prøv dem enkeltvis
              for p in $need; do
                DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "$p" >/dev/null 2>&1 || echo "ADVARSEL: pakken $p kunne ikke installeres (findes måske ikke i denne udgave)"
              done
            fi
          fi
        fi
        echo "::ws 75-95 Tjenester"
        systemctl daemon-reload
        udevadm control --reload-rules 2>/dev/null; udevadm trigger 2>/dev/null
        while read -r u state act; do
          [ "$state" = enabled ] || continue
          [ -e "/etc/systemd/system/$u" ] || [ -e "/lib/systemd/system/$u" ] || [ -e "/usr/lib/systemd/system/$u" ] || { echo "Udeladt: $u (enheden kom ikke med)"; continue; }
          [ "$(systemctl is-enabled "$u" 2>/dev/null)" = masked ] && continue
          systemctl is-enabled "$u" >/dev/null 2>&1 || { systemctl enable "$u" >/dev/null 2>&1 && echo "Slået til: $u"; }
          case "$u" in
            *.service|*.timer|*.path)
              if [ "$act" = active ]; then
                if systemctl is-active --quiet "$u"; then echo "Kører allerede: $u"; else systemctl start "$u" && echo "Startet: $u" || echo "KUNNE IKKE STARTE: $u"; fi
              elif ! systemctl is-active --quiet "$u"; then echo "Ikke startet (kørte ikke ved sikkerhedskopien): $u"; fi;;
          esac
        done < "$M/units.txt"
        echo "::ws 95-100 Færdig"
        echo "Boot-opsætningen blev flettet ind, da kortet blev bygget; den gamle ligger til sammenligning i $M/reference."
        echo "Færdig."
        """;
    }

    /// <summary>Ekskluder-mønstre fra profilen ("udelad ved nyt kort"): stier uden indledende /, med * som jokertegn.</summary>
    public static bool SafeExclude(string p) => p.Length is > 0 and < 160 && System.Text.RegularExpressions.Regex.IsMatch(p, @"^[\w./*\-+@]+$") && !p.Contains("..") && !p.StartsWith('/');

    private static string RestoreScriptPlain(string tarFile) => $$"""
        set -u
        T={{Q(tarFile)}}
        echo "Gendanner fra $(basename "$T") – eksisterende filer overskrives ikke, kørende tjenester røres ikke."
        rm -rf {{Work}}/manifest   # vores egen arbejdsmappe: manifestet skal komme fra netop denne pakke
        before=$(mktemp); tar -tzf "$T" | grep -v '/$' > "$before"
        missing=0; while read -r f; do [ -e "/$f" ] || missing=$((missing+1)); done < "$before"
        echo "Mangler på maskinen: $missing af $(wc -l < "$before") filer"
        tar --skip-old-files --keep-directory-symlink -xzf "$T" -C / 2>&1 | grep -v 'Already exists' | tail -20
        M={{Work}}/manifest
        if [ -s "$M/apt-manual.txt" ] && command -v apt-get >/dev/null; then
          need=$(comm -23 <(sort -u "$M/apt-manual.txt") <(apt-mark showmanual | sort -u) | tr '\n' ' ')
          if [ -n "$need" ]; then echo "Installerer manglende pakker: $need"; DEBIAN_FRONTEND=noninteractive apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq $need 2>&1 | tail -5; fi
        fi
        systemctl daemon-reload
        udevadm control --reload-rules 2>/dev/null; udevadm trigger 2>/dev/null
        # kun tjenester, der KØRTE, da punktet blev taget, startes (engangstjenester og bevidst stoppede tjenester røres ikke)
        while read -r u state act; do
          [ "$state" = enabled ] || continue
          systemctl is-enabled "$u" >/dev/null 2>&1 || { systemctl enable "$u" >/dev/null 2>&1 && echo "Slået til: $u"; }
          case "$u" in
            *.service|*.timer|*.path)
              if [ "$act" = active ]; then systemctl is-active --quiet "$u" || { systemctl start "$u" && echo "Startet: $u" || echo "KUNNE IKKE STARTE: $u"; }
              elif ! systemctl is-active --quiet "$u"; then echo "Ikke startet (kørte ikke ved sikkerhedskopien): $u"; fi;;
          esac
        done < "$M/units.txt"
        echo "Boot-opsætningen fra pakken ligger i $M/reference (lægges ikke automatisk tilbage)."
        echo "Færdig."
        """;
}

/// <summary>"Hold kørende": tjenester fra profilen startes igen med det samme, hvis de stopper. Kørende tjenester røres aldrig.</summary>
internal static class Keeper
{
    private static readonly Dictionary<string, KeptService> State = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<DateTime>> Restarts = [];
    private static readonly Dictionary<string, DateTime> LastTry = [];

    private static string ConditionResult(string unit) =>
        Shell.Run($"systemctl show -p ConditionResult --value '{unit.Replace("'", "")}'", TimeSpan.FromSeconds(10)).Output.Trim();

    public static void Tick(List<string> units)
    {
        if (units.Count == 0 || !Init.IsSystemd) return;
        foreach (var u in units)
        {
            var unit = u.Contains('.') ? u : u + ".service";
            var st = Shell.Run($"systemctl is-active '{unit.Replace("'", "")}'", TimeSpan.FromSeconds(10)).Output.Trim();
            var k = State.TryGetValue(unit, out var e) ? e : State[unit] = new KeptService { Name = unit };
            var wasSkipped = k.Skipped;
            k.State = st.Length > 0 ? st : "ukendt";
            k.Active = st is "active" or "activating" or "reloading";
            if (k.Active) { k.Skipped = false; continue; }
            // Sprunget over pga. en betingelse (ConditionResult=no): ingen fejl. Prøv kun igen hver halve time, hvis betingelsen ændrer sig.
            k.Skipped = KeptService.SkippedByCondition(st, ConditionResult(unit));
            if (k.Skipped) { k.State = "sprunget over"; k.LastError = null; }
            // højst ét forsøg pr. minut pr. tjeneste
            var gap = k.Skipped ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(1);
            if (LastTry.TryGetValue(unit, out var lt) && DateTime.UtcNow - lt < gap) continue;
            LastTry[unit] = DateTime.UtcNow;
            var r = Shell.Run($"systemctl reset-failed '{unit.Replace("'", "")}' 2>/dev/null; systemctl start '{unit.Replace("'", "")}'", TimeSpan.FromSeconds(60));
            var list = Restarts.TryGetValue(unit, out var l) ? l : Restarts[unit] = [];
            list.Add(DateTime.UtcNow); list.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromHours(24));
            k.LastRestart = DateTime.UtcNow; k.Restarts24h = list.Count;
            k.LastError = r.ExitCode == 0 ? null : (r.Output.Length > 200 ? r.Output[..200] : r.Output);
            var after = Shell.Run($"systemctl is-active '{unit.Replace("'", "")}'", TimeSpan.FromSeconds(10)).Output.Trim();
            k.Active = after is "active" or "activating";
            k.Skipped = !k.Active && KeptService.SkippedByCondition(after, ConditionResult(unit));
            if (k.Skipped)
            {
                // Ikke en genstart: betingelsen er stadig ikke opfyldt.
                list.RemoveAt(list.Count - 1); k.Restarts24h = list.Count; k.LastRestart = list.Count > 0 ? list[^1] : null;
                k.State = "sprunget over"; k.LastError = null;
                if (!wasSkipped) Log.Info($"Hold kørende: {unit} springes over – betingelsen er ikke opfyldt på denne maskine");
                continue;
            }
            Log.Info($"Hold kørende: {unit} var {st} – startet igen ({(k.Active ? "kører" : "fejlede")})");
        }
        foreach (var gone in State.Keys.Where(x => !units.Any(u => (u.Contains('.') ? u : u + ".service") == x)).ToList()) State.Remove(gone);
    }

    public static List<KeptService>? Report() => State.Count == 0 ? null : State.Values.Select(v => new KeptService
    {
        Name = v.Name, Active = v.Active, State = v.State, Restarts24h = v.Restarts24h, LastRestart = v.LastRestart, LastError = v.LastError, Skipped = v.Skipped,
    }).ToList();
}
