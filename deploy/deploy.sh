#!/usr/bin/env bash
# Udrul Fleetdeck-serveren til Unraid fra GitHub-versionen af koden.
#   deploy/deploy.sh [--provision] [--source-only]
# 1. Kræver ren arbejdskopi på main, skubber main og tagger v<version> (fra Directory.Build.props) på GitHub.
# 2. Lægger koden i $SHARE/Fleetdeck og bygger billedet fleetdeck:latest + fleetdeck:<version> (de 3 nyeste versioner beholdes).
# 3. Med --provision: lægger også Windows Opsætning (provision/windows), USB- og PXE-filerne på sharen. Kun filer fra git
#    overskrives; personlige filer (apps.json, config.json, USB/autounattend.xml, Backup, Drivers …) røres ikke.
# 4. Med --source-only: koden lægges kun på sharen (med RELEASE-NOTES.md) – intet billede bygges. Opdatér så fra
#    Fleetdeck: Indstillinger → Generelt → Opdatering (kilde "Lokal build-mappe"), som bygger og skifter containeren selv.
# Bagefter (uden --source-only): Unraid → Docker → Fleetdeck → Force update – eller brug opdateringen i Fleetdeck (se docs/OPDATERING.md).
# Serveren angives i site.env (eller som miljøvariabler):
#   HOST=root@192.168.1.10                     SSH-adresse på Unraid-serveren
#   SHARE=/mnt/user/PC-Reinstall/WinSetup      WinSetup-mappen på serveren
#   KEY=~/.ssh/id_ed25519                      (valgfri) SSH-nøgle
# site.env findes ved $SITE_ENV, ellers deploy/site.env (ignoreres af git).
set -euo pipefail
cd "$(dirname "$0")/.."
SITE_ENV=${SITE_ENV:-deploy/site.env}
# shellcheck disable=SC1090
[ -f "$SITE_ENV" ] && . "$SITE_ENV"
: "${HOST:?Sæt HOST (fx root@192.168.1.10) i site.env – se toppen af deploy/deploy.sh}"
: "${SHARE:=/mnt/user/PC-Reinstall/WinSetup}"
SSH=(ssh); [ -n "${KEY:-}" ] && SSH=(ssh -i "$KEY")
PROVISION=0; SOURCE_ONLY=0
for a in "$@"; do
  case "$a" in
    --provision) PROVISION=1 ;;
    --source-only) SOURCE_ONLY=1 ;;
    *) echo "Ukendt valg: $a"; exit 1 ;;
  esac
done
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
[ -n "$VERSION" ] || { echo "Fandt ingen <Version> i Directory.Build.props"; exit 1; }

git diff --quiet && git diff --cached --quiet || { echo "Commit dine ændringer først."; exit 1; }
[ "$(git rev-parse --abbrev-ref HEAD)" = main ] || { echo "Udrul kun fra main."; exit 1; }
git push -q origin main || { echo "Kunne ikke skubbe main til GitHub – udruller ikke."; exit 1; }
TAG="v$VERSION"
if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null || git ls-remote --exit-code --tags origin "$TAG" >/dev/null 2>&1; then
  git fetch -q origin "refs/tags/$TAG:refs/tags/$TAG" 2>/dev/null || true
  [ "$(git rev-list -n1 "$TAG")" = "$(git rev-parse HEAD)" ] || echo "Bemærk: $TAG findes allerede på en anden commit – hæv <Version> for at tagge denne."
else
  git tag -a "$TAG" -m "Fleetdeck $VERSION" && git push -q origin "$TAG"
fi
# "Opdatér Fleetdeck" i Indstillinger læser GitHub-udgivelserne. Udgivelsen laves af GitHub Actions (release-image.yml), FØRST når
# billedet er lagt på ghcr.io – ellers tilbyder Fleetdeck en version, der ikke kan hentes endnu ("manifest unknown").

SHA=$(git rev-parse --short HEAD)
git archive HEAD | "${SSH[@]}" "$HOST" "set -e
  rm -rf $SHARE/Fleetdeck.new && mkdir -p $SHARE/Fleetdeck.new && tar -x -C $SHARE/Fleetdeck.new
  rm -rf $SHARE/Fleetdeck && mv $SHARE/Fleetdeck.new $SHARE/Fleetdeck
  cd $SHARE/Fleetdeck
  if [ $SOURCE_ONLY = 0 ]; then
  docker build -q -t fleetdeck:latest -t fleetdeck:$VERSION . >/dev/null
  docker images --format '{{.Tag}}' fleetdeck | grep -E '^[0-9]+\.[0-9]+\.[0-9]+\$' | sort -V -r | tail -n +4 \
    | sed 's|^|fleetdeck:|' | xargs -r docker rmi >/dev/null 2>&1 || true
  fi
  if [ $PROVISION = 1 ]; then
    cp -f provision/windows/*.ps1 provision/windows/*.cmd provision/windows/*.example.json $SHARE/
    mkdir -p $SHARE/USB $SHARE/PXE $SHARE/Installers && cp -f provision/usb/* $SHARE/USB/ && cp -rf provision/pxe/* $SHARE/PXE/
    cp -f provision/windows/Installers/LAES-MIG.txt $SHARE/Installers/ 2>/dev/null || true
    echo 'Windows Opsætning, USB og PXE er lagt på sharen.'
  fi"
if [ $SOURCE_ONLY = 1 ]; then
  # udgivelsesnoter til opdateringsvinduet i Fleetdeck: commits siden forrige tag
  PREV=$(git describe --tags --abbrev=0 "$TAG^" 2>/dev/null || true)
  { echo "Fleetdeck $VERSION ($SHA) – lokal build"; echo; git log --no-merges --pretty='- %s' ${PREV:+"$PREV..HEAD"} | head -n 60; } \
    | "${SSH[@]}" "$HOST" "cat > $SHARE/Fleetdeck/RELEASE-NOTES.md"
  echo "Fleetdeck $VERSION ($SHA) ligger på $HOST. Opdatér fra Fleetdeck: Indstillinger → Generelt → Opdatering (kilde: Lokal build-mappe)."
  exit 0
fi
echo "Fleetdeck $VERSION ($SHA) bygget på $HOST. Klik nu Docker → Fleetdeck → Force update i Unraid."
echo "Gamle billeder uden container ryddes bagefter med: ssh $HOST docker image prune -f"
