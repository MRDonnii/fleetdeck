#!/usr/bin/env bash
# Henter én Mac-DMG (app/agent/<navn>) fra et offentligt Fleetdeck-billede uden Docker og skriver stien til filen.
# Brug: dmg-from-image.sh ghcr.io/ejer/fleetdeck:3.45.0 Fleetdeck-mac-arm64.dmg
set -euo pipefail
ref=$1 name=$2
repo=${ref#ghcr.io/}; tag=${repo##*:}; repo=${repo%:*}
token=$(curl -fsS "https://ghcr.io/token?scope=repository:$repo:pull" | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
get() { curl -fsSL -H "Authorization: Bearer $token" -H "Accept: application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json" "https://ghcr.io/v2/$repo/$1"; }
manifest=$(get "manifests/$tag")
digest=$(python3 -c 'import sys,json;d=json.loads(sys.argv[1]);m=[x["digest"] for x in d.get("manifests",[]) if x.get("platform",{}).get("architecture")=="amd64"];print(m[0] if m else "")' "$manifest")
[ -n "$digest" ] && manifest=$(get "manifests/$digest")
dir=$(mktemp -d)
for layer in $(python3 -c 'import sys,json;[print(l["digest"]) for l in reversed(json.loads(sys.argv[1])["layers"])]' "$manifest"); do
  # bsdtar (macOS) kan stoppe, så snart filen er pakket ud – så afgør filen, ikke afslutningskoden
  get "blobs/$layer" | tar -xzf - -C "$dir" "app/agent/$name" 2>/dev/null || true
  if [ -s "$dir/app/agent/$name" ]; then echo "$dir/app/agent/$name"; exit 0; fi
done
echo "::error::$name findes ikke i $ref" >&2; exit 1
