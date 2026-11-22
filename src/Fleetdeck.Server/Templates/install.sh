#!/bin/bash
# Fleetdeck: installér Linux-agenten som systemd-tjeneste og tilmeld maskinen.
#   curl -fsSL "{{SERVER}}/install.sh?key=..." | sudo bash
set -euo pipefail
SERVER="{{SERVER}}"
KEY="{{KEY}}"
# Ingen nøgle: "Anmod om adgang" – agenten beder administratoren om lov og viser en kode (WS_MESSAGE: en besked til ham)
REQUEST="{{REQUEST}}"
PROFILE="${WS_PROFILE:-{{PROFILE}}}"   # valgt i Fleetdeck (0 = ingen profil); tom = spørg her
# https med selvsigneret certifikat: curl og agenten accepterer kun serverens fastlåste nøgle (tom = almindelig certifikatkontrol)
TLSPIN="${WS_TLS_PIN:-{{TLSPIN}}}"
TLS=(); PINARG=()
if [ -n "$TLSPIN" ]; then TLS=(-k --pinnedpubkey "sha256//$TLSPIN"); PINARG=(--tls-pin "$TLSPIN"); fi

# Profilen skal altid vælges, så maskinen ikke får noget, den ikke skal have. Uden terminal (automatisk installation): ingen profil.
pick_profile_srv() { curl -fsSL ${TLS[@]+"${TLS[@]}"} "$SERVER$1"; }
pick_profile() {
  [ -n "$REQUEST" ] && return 0   # profilen vælger administratoren, når han godkender
  [ -n "$PROFILE" ] && return 0
  if ! { [ -r /dev/tty ] && [ -w /dev/tty ] && : </dev/tty; } 2>/dev/null; then PROFILE=0; return 0; fi
  local json ids=() names=() i=1 a
  json=$("$@" "/api/agent/profiles?key=$KEY" 2>/dev/null || echo '[]')
  while IFS='|' read -r id name pk mc; do [ -n "$id" ] && ids+=("$id") && names+=("$(printf '%b' "$name")  ($pk programmer, $mc maskiner)"); done < <(
    printf '%s\n' "$json" | tr '{' '\n' | sed -n 's/.*"id":\([0-9]*\).*"name":"\([^"]*\)".*"machines":\([0-9]*\).*"packages":\([0-9]*\).*/\1|\2|\4|\3/p')
  { echo; echo "Vælg profil til denne maskine (styrer hvilke programmer og indstillinger, den får):"
    echo "  0) Ingen profil – kun overvågning og opdateringer"
    for n in "${names[@]}"; do echo "  $i) $n"; i=$((i+1)); done; } >/dev/tty
  while :; do
    printf 'Skriv nummeret: ' >/dev/tty; read -r a </dev/tty || a=0
    if [[ "$a" =~ ^[0-9]+$ ]] && [ "$a" -le "${#ids[@]}" ]; then break; fi
    echo "Skriv et af numrene ovenfor." >/dev/tty
  done
  if [ "$a" = 0 ]; then PROFILE=0; else PROFILE="${ids[$((a-1))]}"; fi
}
# WS_FORCE=1: bed om adgang, selvom maskinen allerede er tilmeldt (den mister sit token, til den er godkendt igen)
auth() { if [ -n "$REQUEST" ]; then AUTH=(--request ${WS_MESSAGE:+--message "$WS_MESSAGE"} ${WS_FORCE:+--force}); else AUTH=(--key "$KEY" --profile "$PROFILE"); fi; }
DIR=/opt/fleetdeck
UNIT=/etc/systemd/system/fleetdeck-agent.service

if [ "$(id -u)" != "0" ]; then echo "Fleetdeck: kør med sudo:  curl -fsSL \"$SERVER/install.sh?key=$KEY\" | sudo bash" >&2; exit 1; fi
if [ -f /etc/unraid-version ]; then
  # Unraid (ingen systemd, kører fra RAM): agenten gemmes på USB-stikket og startes fra /boot/config/go.
  # Kører Fleetdeck på samme Unraid (egen IP på br0), kan værten ikke nå den IP – så bruges socket-filen i appdata.
  SOCK=${WS_SOCKET:-}
  if [ -z "$SOCK" ] && ! curl -fsS -m 3 ${TLS[@]+"${TLS[@]}"} "$SERVER/api/health" >/dev/null 2>&1; then
    for s in /mnt/user/appdata/fleetdeck/fleetdeck.sock /mnt/cache/appdata/fleetdeck/fleetdeck.sock; do [ -S "$s" ] && SOCK=$s && break; done
  fi
  if [ -n "$SOCK" ]; then GET=(curl -fsSL --unix-socket "$SOCK"); SRC=http://localhost; AGENT_SERVER="unix:$SOCK"
  else GET=(curl -fsSL ${TLS[@]+"${TLS[@]}"}); SRC=$SERVER; AGENT_SERVER=$SERVER; fi
  echo "Fleetdeck: henter agenten til Unraid ($AGENT_SERVER)"
  mkdir -p /usr/local/fleetdeck
  "${GET[@]}" "$SRC/api/agent/download?platform=linux&arch=x64" -o /usr/local/fleetdeck/fleetdeck-agent.new
  chmod 755 /usr/local/fleetdeck/fleetdeck-agent.new
  mv -f /usr/local/fleetdeck/fleetdeck-agent.new /usr/local/fleetdeck/fleetdeck-agent
  pick_profile_get() { "${GET[@]}" "$SRC$1"; }; pick_profile pick_profile_get; auth
  exec /usr/local/fleetdeck/fleetdeck-agent install --server "$AGENT_SERVER" "${AUTH[@]}" ${PINARG[@]+"${PINARG[@]}"} --service ${WS_NAME:+--name "$WS_NAME"}
fi
# macOS: hent disk-imaget med Fleetdeck.app for arkitekturen ($1), og læg appen i /Programmer (den gamle byttes først ud, når den nye
# ligger klar). 0 = appen er på plads. Fejler noget, fortsætter installationen med kun agenten.
mac_app() {
  local dmg mnt ok=1 app=/Applications/Fleetdeck.app new=/Applications/.Fleetdeck.new.app
  dmg=$(mktemp /tmp/fleetdeck-app.XXXXXX) || return 1
  echo "Fleetdeck: henter Fleetdeck.app (menulinjen og opsætningen)"
  if curl -fsSL ${TLS[@]+"${TLS[@]}"} "$SERVER/api/agent/download?platform=mac&kind=dmg&arch=$1" -o "$dmg" \
     && mnt=$(mktemp -d /tmp/fleetdeck-dmg.XXXXXX); then
    if hdiutil attach "$dmg" -nobrowse -readonly -noautoopen -mountpoint "$mnt" >/dev/null; then
      if [ -x "$mnt/Fleetdeck.app/Contents/MacOS/Fleetdeck" ] && rm -rf "$new" && ditto "$mnt/Fleetdeck.app" "$new"; then
        xattr -dr com.apple.quarantine "$new" 2>/dev/null || true
        rm -rf "$app" && mv "$new" "$app" && ok=0
      fi
      hdiutil detach "$mnt" -force >/dev/null 2>&1 || true
    fi
    rmdir "$mnt" 2>/dev/null || true
  fi
  rm -f "$dmg"
  if [ "$ok" != 0 ]; then
    rm -rf "$new"
    echo "Fleetdeck: Fleetdeck.app kunne ikke hentes – kun agenten installeres nu; tjenesten lægger appen på, når den kan." >&2
    [ -d "$app" ] && return 0
  fi
  return "$ok"
}
if [ "$(uname -s)" = "Darwin" ]; then
  # macOS: Apple Silicon (arm64) eller Intel (x64); kører som launchd-tjenesten com.fleetdeck.agent
  case "$(uname -m)" in arm64) ARCH=arm64 ;; *) ARCH=x64 ;; esac
  # Terminal under Rosetta melder x86_64 på en Apple Silicon-Mac: spørg hardwaren, så agent og app passer til maskinen
  if [ "$(sysctl -in hw.optional.arm64 2>/dev/null)" = 1 ]; then ARCH=arm64; fi
  # macOS-udgaven: det almindelige byg kræver macOS 12; Intel-Mac'er med 10.15/11 får legacy-udgaven (.NET 8); ældre kan ikke
  MACOS=$(sw_vers -productVersion 2>/dev/null); MAJ=${MACOS%%.*}; MIN=$(echo "$MACOS" | cut -d. -f2)
  case "$MAJ" in ''|*[!0-9]*) MAJ=99 ;; esac; case "$MIN" in ''|*[!0-9]*) MIN=0 ;; esac
  if [ "$ARCH" = arm64 ] && [ "$MAJ" -lt 12 ]; then
    echo "Fleetdeck kræver macOS 12 (Monterey) eller nyere på en Mac med Apple silicon – denne Mac har macOS $MACOS. Opdatér macOS (gratis) under Apple-menuen → Systemindstillinger → Softwareopdatering, og prøv igen." >&2; exit 1
  fi
  if [ "$ARCH" = x64 ] && { [ "$MAJ" -lt 10 ] || { [ "$MAJ" = 10 ] && [ "$MIN" -lt 15 ]; }; }; then
    echo "Fleetdeck kræver macOS 10.15 (Catalina) eller nyere – denne Mac har macOS $MACOS. Se under Apple-menuen → Om denne Mac, om macOS kan opdateres." >&2; exit 1
  fi
  if [ "$ARCH" = x64 ] && [ "$MAJ" -lt 12 ]; then ARCH=x64-legacy; echo "Fleetdeck: macOS $MACOS – bruger udgaven til ældre Intel-Mac'er (macOS 10.15–11)"; fi
  MDIR=/usr/local/fleetdeck
  echo "Fleetdeck: henter Mac-agenten fra $SERVER"
  mkdir -p "$MDIR"
  curl -fsSL ${TLS[@]+"${TLS[@]}"} "$SERVER/api/agent/download?platform=mac&arch=$ARCH" -o "$MDIR/fleetdeck-agent.new"
  chmod 755 "$MDIR/fleetdeck-agent.new"
  xattr -d com.apple.quarantine "$MDIR/fleetdeck-agent.new" 2>/dev/null || true
  # Apple Silicon kræver en signatur. Filen fra serveren er normalt allerede (ad hoc) signeret – så røres den ikke.
  # "fleetdeck-agent install" nedenfor giver den bagefter en fast lokal signatur ("Fleetdeck Agent (local)"),
  # så Fuld diskadgang ikke forsvinder ved hver selvopdatering.
  codesign -v "$MDIR/fleetdeck-agent.new" 2>/dev/null || codesign -s - -f --identifier com.fleetdeck.agent "$MDIR/fleetdeck-agent.new" 2>/dev/null || true
  mv -f "$MDIR/fleetdeck-agent.new" "$MDIR/fleetdeck-agent"
  # Fleetdeck.app (ikonet i menulinjen, statusvinduet og opsætningen) fra samme server – uden den får brugeren ingen GUI.
  # WS_NO_APP=1: kun agenten (fx en Mac uden skærm) – huskes (--no-app), så tjenesten heller ikke lægger appen på senere.
  # Kan appen ikke hentes, lægger tjenesten den selv på senere.
  APPARG=()
  if [ -n "${WS_NO_APP:-}" ]; then APPARG=(--no-app); elif mac_app "$ARCH"; then APPARG=(--menubar /Applications/Fleetdeck.app); fi
  pick_profile pick_profile_srv; auth
  exec "$MDIR/fleetdeck-agent" install --server "$SERVER" "${AUTH[@]}" ${PINARG[@]+"${PINARG[@]}"} ${APPARG[@]+"${APPARG[@]}"} --service ${WS_NAME:+--name "$WS_NAME"}
fi
case "$(uname -m)" in
  x86_64|amd64) ARCH=x64 ;;
  aarch64|arm64) ARCH=arm64 ;;            # Raspberry Pi 3/4/5 med 64-bit OS m.fl.
  armv7l|armv7*|armhf) ARCH=arm ;;        # 32-bit Raspberry Pi OS (Pi 2/3/4)
  *) echo "Fleetdeck: processoren $(uname -m) understøttes ikke (kun x64, ARM64 og 32-bit ARMv7 – ikke Pi Zero/1)" >&2; exit 1 ;;
esac
# musl (Alpine, Void-musl): egen agent; glibc-udgaven kan ikke starte der
if ldd --version 2>&1 | grep -qi musl || [ -f /etc/alpine-release ]; then
  case "$ARCH" in x64|arm64) ARCH="musl-$ARCH" ;; *) echo "Fleetdeck: musl understøttes kun på x64 og ARM64" >&2; exit 1 ;; esac
  command -v apk >/dev/null && apk add -q --no-cache libstdc++ libgcc >/dev/null 2>&1 || true
fi

# Agenten er en selvstændig .NET-fil og skal bruge libgcc og libstdc++ (mangler på helt minimale installationer)
if ! { ldconfig -p 2>/dev/null || ls /lib /usr/lib /lib64 /usr/lib64 2>/dev/null; } | grep -q 'libgcc_s\.so'; then
  echo "Fleetdeck: installerer libgcc/libstdc++"
  { command -v apk && apk add -q --no-cache libstdc++ libgcc; } >/dev/null 2>&1 ||
  { command -v xbps-install && xbps-install -Sy libgcc libstdc++; } >/dev/null 2>&1 ||
  { command -v apt-get && apt-get install -y -qq libgcc-s1 libstdc++6; } >/dev/null 2>&1 ||
  { command -v dnf && dnf install -y -q libgcc libstdc++; } >/dev/null 2>&1 ||
  { command -v zypper && zypper -n -q install libgcc_s1 libstdc++6; } >/dev/null 2>&1 ||
  { command -v pacman && pacman -S --noconfirm --needed gcc-libs; } >/dev/null 2>&1 ||
  { command -v eopkg && eopkg it -y libgcc libstdc++; } >/dev/null 2>&1 || true
fi

# Uden systemd (Alpine, Gentoo, Void, Artix, Devuan, MX …): agenten opretter selv tjenesten for OpenRC, runit eller SysV-init
if ! command -v systemctl >/dev/null; then
  echo "Fleetdeck: henter agenten fra $SERVER"
  mkdir -p "$DIR"
  curl -fsSL ${TLS[@]+"${TLS[@]}"} "$SERVER/api/agent/download?platform=linux&arch=$ARCH" -o "$DIR/fleetdeck-agent.new"
  chmod 755 "$DIR/fleetdeck-agent.new"
  mv -f "$DIR/fleetdeck-agent.new" "$DIR/fleetdeck-agent"
  pick_profile pick_profile_srv; auth
  exec "$DIR/fleetdeck-agent" install --server "$SERVER" "${AUTH[@]}" ${PINARG[@]+"${PINARG[@]}"} --service ${WS_NAME:+--name "$WS_NAME"} ${WS_PROVISION:+--provision}
fi

echo "Fleetdeck: henter agenten fra $SERVER"
mkdir -p "$DIR"
curl -fsSL ${TLS[@]+"${TLS[@]}"} "$SERVER/api/agent/download?platform=linux&arch=$ARCH" -o "$DIR/fleetdeck-agent.new"
chmod 755 "$DIR/fleetdeck-agent.new"
systemctl stop fleetdeck-agent 2>/dev/null || true
mv -f "$DIR/fleetdeck-agent.new" "$DIR/fleetdeck-agent"

# WS_NAME: maskinens navn (Ubuntu-installeren kører med et midlertidigt værtsnavn). WS_PROVISION=1: genskab fra seneste kit.
pick_profile pick_profile_srv; auth
# fejler det (fx afvist anmodning eller allerede tilmeldt), er den gamle konfiguration urørt: start den tidligere tjeneste igen
"$DIR/fleetdeck-agent" install --server "$SERVER" "${AUTH[@]}" ${PINARG[@]+"${PINARG[@]}"} ${WS_NAME:+--name "$WS_NAME"} ${WS_PROVISION:+--provision} \
  || { rc=$?; [ -f "$UNIT" ] && systemctl start fleetdeck-agent 2>/dev/null; exit $rc; }

cat > "$UNIT" <<'EOF'
[Unit]
Description=Fleetdeck Agent (styring af programmer, opdateringer og helbred)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/opt/fleetdeck/fleetdeck-agent
Restart=always
RestartSec=5
# Lav prioritet: skrivebord/kiosk går altid forrest
Nice=5
CPUWeight=20
IOWeight=20

[Install]
WantedBy=multi-user.target
EOF
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload
  systemctl enable --now fleetdeck-agent
else
  # i Ubuntu-installerens chroot: start ved næste opstart
  systemctl enable fleetdeck-agent
fi
echo "Fleetdeck: færdig. Agenten kører som tjenesten fleetdeck-agent (log: journalctl -u fleetdeck-agent)."
if [ -n "$REQUEST" ]; then echo "Fleetdeck: venter på, at administratoren godkender maskinen (koden står ovenfor). Status: sudo $DIR/fleetdeck-agent status"; fi
