#!/bin/bash
# Fleetdeck: tilmeld et nyt SD-kort (tjenesten fleetdeck-firstboot, efter netværket er oppe).
# Venter på Fleetdeck i op til 30 minutter, kører install.sh med engangskoden og maskinens navn – ved "erstat" også med
# genskabelse fra gendannelsespunktet – og rydder så engangskoden væk og slår sig selv fra. Mislykkes det, prøver tjenesten
# igen ved næste start; Pi'en kører videre med et rent system, der kan rettes over terminalen.
set -u
D=/var/lib/fleetdeck-firstboot
LOG=/var/log/fleetdeck-firstboot.log
exec >>"$LOG" 2>&1
echo "== $(date -Is) Fleetdeck tilmelding"
[ -f "$D/settings.env" ] || { echo "Ingen indstillinger – slår mig selv fra"; systemctl disable fleetdeck-firstboot.service; exit 0; }
# shellcheck disable=SC1091
. "$D/settings.env"
CODE=$(cat "$D/code")
TLS=()
[ -n "${FD_TLSPIN:-}" ] && TLS=(-k --pinnedpubkey "sha256//$FD_TLSPIN")
say() { curl -fsS -m 10 ${TLS[@]+"${TLS[@]}"} -X POST "$FD_SERVER/api/picard/status?step=$1" -H "X-Fleetdeck-Code: $CODE" >/dev/null 2>&1 || true; }

up=0
for _ in $(seq 1 180); do
  if curl -fsS -m 5 ${TLS[@]+"${TLS[@]}"} "$FD_SERVER/api/health" >/dev/null 2>&1; then up=1; break; fi
  sleep 10
done
[ "$up" = 1 ] || { echo "Fleetdeck svarede ikke på $FD_SERVER i 30 minutter – prøver igen ved næste start"; exit 1; }
say startet

if ! curl -fsSL ${TLS[@]+"${TLS[@]}"} "$FD_SERVER/install.sh?key=$CODE&profile=$FD_PROFILE" -o /run/fleetdeck-install.sh; then
  echo "install.sh kunne ikke hentes (er engangskoden udløbet?)"; say fejl; exit 1
fi
export WS_NAME="$FD_NAME" WS_PROFILE="$FD_PROFILE" WS_TLS_PIN="${FD_TLSPIN:-}"
[ "${FD_MODE:-ny}" = erstat ] && export WS_PROVISION=1
if bash /run/fleetdeck-install.sh; then
  rm -f /run/fleetdeck-install.sh
  rm -rf "$D"
  systemctl disable fleetdeck-firstboot.service
  echo "== $(date -Is) tilmeldt – Fleetdeck tager over herfra"
else
  rc=$?; echo "install.sh sluttede med $rc"; say fejl; exit "$rc"
fi
