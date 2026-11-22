namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Genskab-kit: alt hvad der skal til for at genskabe maskinen på en frisk Ubuntu – pakker, pakkekilder, systemopsætning,
/// egne tjenester, /opt og brugerens opsætning. Store datamapper (projekter m.m.) er ikke med.
/// </summary>
public static class Kit
{
    public const string Work = "/var/lib/fleetdeck";

    // Kører som root. $1 = bruger, $2 = uddatafil (.tar.gz)
    private const string BackupScript = """
        set -u
        U="$1"; OUT="$2"
        H=$(getent passwd "$U" | cut -d: -f6); UIDN=$(id -u "$U")
        W=$(mktemp -d /var/tmp/ws-kit.XXXXXX); trap 'rm -rf "$W"' EXIT
        M="$W/meta"; mkdir -p "$M"
        # --- lister
        apt-mark showmanual | sort -u > "$M/apt-manual.txt"
        command -v snap >/dev/null && snap list 2>/dev/null | awk 'NR>1 && $1!~/^(core|bare|snapd|gnome-|gtk-|mesa-)/ {print $1, $4, $6}' > "$M/snaps.txt"
        systemctl list-unit-files --type=service --type=timer --state=enabled --no-legend 2>/dev/null | awk '{print $1}' | sort > "$M/system-enabled.txt"
        : > "$M/custom-units.txt"
        for f in /etc/systemd/system/*.service /etc/systemd/system/*.timer; do
          [ -f "$f" ] && [ ! -L "$f" ] || continue
          case "$(basename "$f")" in cloud-*|snap.*|fleetdeck-agent.*) continue ;; esac   # laves af Ubuntu/snap/installeren
          dpkg -S "$f" >/dev/null 2>&1 || echo "$f" >> "$M/custom-units.txt"
        done
        crontab -l -u "$U" > "$M/user-crontab.txt" 2>/dev/null || true
        [ -f "/var/lib/systemd/linger/$U" ] && echo yes > "$M/linger"
        crontab -l > "$M/root-crontab.txt" 2>/dev/null || true
        . /etc/os-release
        printf '{"user":"%s","uid":%s,"home":"%s","hostname":"%s","os":"%s","created":"%s","timezone":"%s"}\n' \
          "$U" "$UIDN" "$H" "$(hostname)" "$PRETTY_NAME" "$(date -u +%FT%TZ)" "$(cat /etc/timezone 2>/dev/null)" > "$M/info.json"
        echo "::ws 0-10 Finder filer til kittet"
        # --- filer
        L="$W/paths.txt"; : > "$L"
        add() { for p in "$@"; do [ -e "$p" ] && echo "${p#/}" >> "$L"; done; return 0; }
        add /etc/apt/sources.list.d /etc/apt/keyrings /etc/apt/preferences.d /etc/apt/apt.conf.d/50unattended-upgrades
        for k in $(grep -rhoE '(signed-by=|Signed-By: )[^] ]+' /etc/apt/sources.list.d 2>/dev/null | sed -E 's/^(signed-by=|Signed-By: )//' | sort -u); do add "$k"; done
        add /etc/gdm3/custom.conf /etc/sudoers.d /etc/ssh/sshd_config.d /etc/netplan /etc/fstab /etc/hosts /etc/hostname /etc/default/grub \
            /etc/X11/xorg.conf.d /etc/NetworkManager/system-connections /etc/unbound /etc/environment /etc/timezone
        for c in $(grep -oE 'credentials=[^, ]+' /etc/fstab 2>/dev/null | cut -d= -f2 | sort -u); do add "$c"; done
        while read -r f; do add "$f" "$f.d"; done < "$M/custom-units.txt"
        for d in /opt/*; do case "$d" in /opt/google|/opt/fleetdeck|/opt/containerd) ;; *) add "$d" ;; esac; done
        add "$H/kiosk" "$H/.config/systemd" "$H/.config/autostart" "$H/.config/chrome-kiosk" "$H/.config/dconf" "$H/.config/monitors.xml" \
            "$H/.local/bin" "$H/.ssh" "$H/.bashrc" "$H/.profile" "$H/.gitconfig" "$H/.config/rustdesk"
        echo "::ws 10-100 Pakker genskab-kittet"
        tar -czf "$OUT" --warning=no-file-changed --ignore-failed-read \
            --exclude='*/Cache/*' --exclude='*/Code Cache/*' --exclude='*/GPUCache/*' --exclude='*/Service Worker/CacheStorage/*' --exclude='*/.cache/*'             --exclude='*/optimization_guide_model_store/*' --exclude='*/component_crx_cache/*' --exclude='*/WasmTtsEngine/*' --exclude='*/Safe Browsing/*'             --exclude='*/GrShaderCache/*' --exclude='*/ShaderCache/*' --exclude='*/DawnGraphiteCache/*' --exclude='*/DawnWebGPUCache/*' --exclude='*/__pycache__/*'             --exclude='opt/AdGuardHome/data/querylog.json*' --exclude='*.log' \
            -C / -T "$L" -C "$W" meta
        rc=$?; [ $rc -le 1 ] || exit $rc
        echo "Pakker: $(wc -l < "$M/apt-manual.txt") apt, $(wc -l < "$M/snaps.txt" 2>/dev/null || echo 0) snap. Egne tjenester: $(wc -l < "$M/custom-units.txt"). Stier: $(wc -l < "$L")."
        """;

    // Kører som root på en frisk Ubuntu. $1 = kit (.tar.gz)
    private const string RestoreScript = """
        set -u
        KIT="$1"; R=/var/lib/fleetdeck/restore
        rm -rf "$R"; mkdir -p "$R"; tar -xzf "$KIT" -C "$R" || exit 1
        M="$R/meta"
        U=$(sed -nE 's/.*"user":"([^"]+)".*/\1/p' "$M/info.json")
        id "$U" >/dev/null 2>&1 || { echo "Brugeren $U findes ikke. Installér Ubuntu med samme brugernavn."; exit 2; }
        H=$(getent passwd "$U" | cut -d: -f6)
        echo "::ws 2-10 Lægger pakkekilder på"
        echo "== Pakkekilder"
        # Ubuntus egne kilder kommer fra den nye installation (passer til dens version) – kun tredjeparts lægges på
        find "$R/etc/apt/sources.list.d" -maxdepth 1 \( -name 'ubuntu.sources*' -o -name '*.curtin.orig' -o -name '*.distUpgrade' \) -delete 2>/dev/null
        cp -a "$R/etc/apt/sources.list.d/." /etc/apt/sources.list.d/ 2>/dev/null
        [ -d "$R/etc/apt/keyrings" ] && mkdir -p /etc/apt/keyrings && cp -a "$R/etc/apt/keyrings/." /etc/apt/keyrings/
        [ -d "$R/usr/share/keyrings" ] && cp -an "$R/usr/share/keyrings/." /usr/share/keyrings/
        [ -d "$R/etc/apt/preferences.d" ] && cp -a "$R/etc/apt/preferences.d/." /etc/apt/preferences.d/
        apt-get update -qq
        echo "::ws 10-70 Installerer pakker"
        echo "== apt-pakker"
        apt-cache pkgnames | sort -u > "$R/available.txt"
        comm -12 <(sort -u "$M/apt-manual.txt") "$R/available.txt" > "$R/install.txt"
        comm -23 <(sort -u "$M/apt-manual.txt") "$R/available.txt" > "$R/missing.txt"
        # Pakker der har skiftet navn i nyere Ubuntu
        sed -nE 's/^python3\.[0-9]+-venv$/python3-venv/p; s/^dnsutils$/bind9-dnsutils/p' "$R/missing.txt" | sort -u | comm -12 - "$R/available.txt" >> "$R/install.txt"
        echo "Mangler i pakkekilderne (springes over): $(tr '\n' ' ' < "$R/missing.txt")"
        xargs -a "$R/install.txt" apt-get install -y -o Dpkg::Options::=--force-confdef -o Dpkg::Options::=--force-confold || echo "Nogle apt-pakker fejlede"
        # RustDesk ligger ikke i nogen pakkekilde – hentes fra RustDesks officielle udgivelser på GitHub
        if grep -qx rustdesk "$R/missing.txt" && ! dpkg -s rustdesk >/dev/null 2>&1; then
          u=$(curl -fsSL https://api.github.com/repos/rustdesk/rustdesk/releases/latest | grep -oE 'https://github\.com/rustdesk/rustdesk/releases/download/[^"]+-x86_64\.deb' | head -1)
          if [ -n "$u" ] && curl -fsSL -o "$R/rustdesk.deb" "$u" && apt-get install -y "$R/rustdesk.deb"; then sed -i '/^rustdesk$/d' "$R/missing.txt"
          else echo "RustDesk kunne ikke hentes – installér den fra rustdesk.com"; fi
        fi
        echo "::ws 70-82 Installerer snaps"
        echo "== snaps"
        [ -s "$M/snaps.txt" ] && while read -r name track notes; do
          snap list "$name" >/dev/null 2>&1 && continue
          case "$notes" in *classic*) c=--classic ;; *) c= ;; esac
          track="${track%%/…*}"; track="${track%%…*}"; case "$track" in ""|-) track=latest/stable ;; esac
          snap install "$name" --channel="${track:-latest/stable}" $c || echo "snap $name fejlede"
        done < "$M/snaps.txt"
        echo "::ws 82-94 Lægger filer og opsætning på"
        echo "== Filer"
        # Maskinspecifikt fra den nye installation bevares: diskene i fstab, netplan og værtsnavn.
        # Fra den gamle fstab flettes kun netværksdrev (cifs/nfs) ind.
        if [ -f "$R/etc/fstab" ]; then
          grep -E '^\s*[^#].*\s(cifs|nfs|nfs4)\s' "$R/etc/fstab" | while read -r line; do
            tgt=$(echo "$line" | awk '{print $2}')
            grep -qE "\s$tgt\s" /etc/fstab || echo "$line" >> /etc/fstab
          done
          rm -f "$R/etc/fstab"
        fi
        rm -rf "$R/etc/netplan" "$R/etc/hostname"
        for top in etc opt usr home; do [ -d "$R/$top" ] && cp -a "$R/$top/." "/$top/"; done
        chown -R "$U:$U" "$H" 2>/dev/null
        chmod 700 "$H/.ssh" 2>/dev/null; chmod 600 "$H/.ssh/"* 2>/dev/null
        for c in $(grep -oE 'credentials=[^, ]+' /etc/fstab 2>/dev/null | cut -d= -f2); do chmod 600 "$c" 2>/dev/null; done
        chmod 440 /etc/sudoers.d/* 2>/dev/null; visudo -c >/dev/null || echo "ADVARSEL: sudoers-fejl"
        for d in $(grep -oE '^\S+\s+(/mnt/\S+)' /etc/fstab | awk '{print $2}'); do mkdir -p "$d"; done
        echo "::ws 94-100 Slår tjenester til"
        echo "== Tjenester"
        systemctl daemon-reload
        while read -r f; do n=$(basename "$f"); grep -qx "$n" "$M/system-enabled.txt" && systemctl enable "$n"; done < "$M/custom-units.txt"
        # Crontab lægges IKKE på automatisk: fx en spejl-backup med --delete ville ellers kunne tømme backuppen,
        # før dine data er kopieret tilbage. Den gemmes, så du selv lægger den på bagefter.
        [ -s "$M/user-crontab.txt" ] && cp "$M/user-crontab.txt" "$H/crontab-fra-backup.txt" && chown "$U:$U" "$H/crontab-fra-backup.txt"
        [ -s "$M/root-crontab.txt" ] && cp "$M/root-crontab.txt" /root/crontab-fra-backup.txt
        # "linger" (brugertjenester uden login) markeres, så det slår til ved næste opstart – intet startes nu
        [ -f "$M/linger" ] && mkdir -p /var/lib/systemd/linger && touch "/var/lib/systemd/linger/$U"
        mount -a 2>/dev/null
        update-grub >/dev/null 2>&1
        [ -s "$R/missing.txt" ] && echo "Ikke lagt på (findes ikke til denne Ubuntu-version): $(tr '\n' ' ' < "$R/missing.txt")"
        echo "Genskab-kittet er lagt på. Næste skridt:"
        echo "  1. Kopiér dine data tilbage fra spejl-backuppen."
        [ -f "$H/crontab-fra-backup.txt" ] && echo "  2. Læg crontab på igen:  crontab ~/crontab-fra-backup.txt   (og som root: crontab /root/crontab-fra-backup.txt)"
        echo "  3. Genstart maskinen, så autologin, tjenester og kiosk starter."
        """;

    private static string WriteScript(string name, string body)
    {
        Directory.CreateDirectory(Work);
        var p = Path.Combine(Work, name);
        File.WriteAllText(p, body.Replace("\r\n", "\n"));
        File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return p;
    }

    /// <summary>Lav kittet og send det til serveren.</summary>
    public static async Task<ShellResult> Backup(Api api, CancellationToken ct)
    {
        var user = UserSession.Find()?.User ?? Shell.Out("getent passwd 1000 | cut -d: -f1");
        if (user.Length == 0) return new ShellResult(1, "Fandt ingen bruger (uid 1000) at tage backup af.", false);
        var out_ = Path.Combine(Work, "kit.tar.gz");
        Directory.CreateDirectory(Work);
        var r = Shell.Run($"bash {WriteScript("backup.sh", BackupScript)} '{user}' '{out_}'", TimeSpan.FromMinutes(30));
        if (r.ExitCode != 0 || !File.Exists(out_)) return r;
        var size = new FileInfo(out_).Length;
        try { await api.Upload("/api/agent/backup", out_, ct); }
        catch (Exception e) { return new ShellResult(1, r.Output + $"\nKunne ikke sende kittet til serveren: {e.Message}", false); }
        finally { try { File.Delete(out_); } catch (IOException) { } }
        return new ShellResult(0, r.Output + $"Genskab-kit sendt til Fleetdeck ({size / 1048576.0:0.#} MB).", false);
    }

    /// <summary>Hent det seneste kit for denne maskine og læg det på.</summary>
    public static async Task<ShellResult> Restore(Api api, CancellationToken ct)
    {
        var kit = Path.Combine(Work, "restore-kit.tar.gz");
        Directory.CreateDirectory(Work);
        try { await api.Download("/api/agent/backup/latest", kit, ct); }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        { return new ShellResult(0, "Der findes intet genskab-kit for denne maskine endnu – intet at gendanne.", false); }
        var r = Shell.Run($"bash {WriteScript("restore.sh", RestoreScript)} '{kit}'", TimeSpan.FromMinutes(120));
        try { File.Delete(kit); } catch (IOException) { }
        return r;
    }
}
