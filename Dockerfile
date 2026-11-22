# syntax=docker/dockerfile:1
# Fleetdeck – multi-stage build: web (Node) → server + agenter + Mac-app (.NET 10) → lille runtime-billede

# ---- 1. Webgrænseflade (React + TypeScript + Vite)
# Web og .NET bygges på byggemaskinens egen arkitektur (hurtigt, også ved arm64-billeder): resultatet er arkitekturuafhængigt,
# og agenterne krydskompileres alligevel. Kun runtime-trinnet er pr. platform (linux/amd64, linux/arm64).
FROM --platform=$BUILDPLATFORM node:24-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build -- --outDir /out/wwwroot

# ---- 2. Server og agent (.NET 10 SDK)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props Fleetdeck.slnx ./
COPY src/Fleetdeck.Shared/Fleetdeck.Shared.csproj src/Fleetdeck.Shared/
COPY src/Fleetdeck.Server/Fleetdeck.Server.csproj src/Fleetdeck.Server/
COPY src/Fleetdeck.Agent.Windows/Fleetdeck.Agent.Windows.csproj src/Fleetdeck.Agent.Windows/
COPY src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj src/Fleetdeck.Agent.Unix/
RUN dotnet restore src/Fleetdeck.Server/Fleetdeck.Server.csproj \
 && dotnet restore src/Fleetdeck.Agent.Windows/Fleetdeck.Agent.Windows.csproj -p:EnableWindowsTargeting=true \
 && dotnet restore src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj
COPY src/ src/
RUN dotnet publish src/Fleetdeck.Server/Fleetdeck.Server.csproj -c Release -o /out/server --no-restore
# Windows-agenten krydskompileres på Linux til én selvstændig .exe
RUN dotnet publish src/Fleetdeck.Agent.Windows/Fleetdeck.Agent.Windows.csproj -c Release -o /out/agent -p:EnableWindowsTargeting=true --no-restore
# Linux-agenten (x64, ARM64, 32-bit ARM, musl til Alpine) og Mac-agenten (Apple Silicon og Intel) – hver som én fil
RUN dotnet publish src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj -c Release -o /out/linuxagent --no-restore \
 && dotnet publish src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj -c Release -r linux-arm64 -o /out/linuxagent-arm64 --no-restore \
 && dotnet publish src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj -c Release -r linux-arm -o /out/linuxagent-arm --no-restore \
 && dotnet publish src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj -c Release -r linux-musl-x64 -o /out/linuxagent-musl-x64 --no-restore \
 && dotnet publish src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj -c Release -r linux-musl-arm64 -o /out/linuxagent-musl-arm64 --no-restore \
 && dotnet publish src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj -c Release -r osx-arm64 -o /out/macagent-arm64 --no-restore \
 && dotnet publish src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj -c Release -r osx-x64 -o /out/macagent-x64 --no-restore \
 && dotnet publish src/Fleetdeck.Agent.Unix/Fleetdeck.Agent.Unix.csproj -c Release -r osx-x64 -p:FleetdeckLegacy=true -o /out/macagent-x64-legacy
# ↑ legacy: Intel-Mac'er med macOS 10.15–11 (.NET 8, egne obj-legacy-mapper; se Directory.Build.props og Shared/MacCompat.cs)
# Mac-appen (Fleetdeck.app i menulinjen + installationsvindue, Avalonia) som disk-image til Apple Silicon og Intel.
# Pakken, ad hoc-signaturen og DMG'en (HFS+ + UDZO) laves af src/Fleetdeck.MacPack i ren .NET – ingen Xcode, hdiutil, libdmg eller root.
# Den native menu (FleetdeckMenu) bygges med swiftc på en macOS-runner (job "macmenu" i release-image.yml) og lægges i
# src/Fleetdeck.MacMenu/build/<arch>/. Mangler den (lokalt byg), kommer pakken uden – appen viser så selv menuen.
# REQUIRE_MAC_MENU=1 (CI): stop, hvis den mangler, så en udgivelse aldrig kommer uden.
ARG REQUIRE_MAC_MENU=0
RUN V=$(dotnet msbuild src/Fleetdeck.MacApp/Fleetdeck.MacApp.csproj -getProperty:Version) \
 && M=src/Fleetdeck.MacMenu/build \
 && if [ "$REQUIRE_MAC_MENU" = 1 ] && { [ ! -f $M/arm64/FleetdeckMenu ] || [ ! -f $M/x64/FleetdeckMenu ]; }; then echo "FleetdeckMenu mangler i $M" >&2; exit 1; fi \
 && MA=$([ -f $M/arm64/FleetdeckMenu ] && echo "--menu $M/arm64" || true) \
 && MX=$([ -f $M/x64/FleetdeckMenu ] && echo "--menu $M/x64" || true) \
 && dotnet publish src/Fleetdeck.MacApp/Fleetdeck.MacApp.csproj -c Release -r osx-arm64 -o /out/macapp-arm64 \
 && dotnet publish src/Fleetdeck.MacApp/Fleetdeck.MacApp.csproj -c Release -r osx-x64 -o /out/macapp-x64 \
 && dotnet publish src/Fleetdeck.MacApp/Fleetdeck.MacApp.csproj -c Release -r osx-x64 -p:FleetdeckLegacy=true -o /out/macapp-x64-legacy \
 && dotnet build src/Fleetdeck.MacPack/Fleetdeck.MacPack.csproj -c Release -o /out/macpack \
 && mkdir -p /out/dmg \
 && dotnet /out/macpack/fleetdeck-macpack.dll all --publish /out/macapp-arm64 --version "$V" --icon src/Fleetdeck.Agent.Windows/fleetdeck.ico $MA --out /out/dmg/Fleetdeck-mac-arm64.dmg \
 && dotnet /out/macpack/fleetdeck-macpack.dll all --publish /out/macapp-x64 --version "$V" --icon src/Fleetdeck.Agent.Windows/fleetdeck.ico $MX --out /out/dmg/Fleetdeck-mac-x64.dmg \
 && dotnet /out/macpack/fleetdeck-macpack.dll all --publish /out/macapp-x64-legacy --version "$V" --icon src/Fleetdeck.Agent.Windows/fleetdeck.ico --out /out/dmg/Fleetdeck-mac-x64-legacy.dmg
# ↑ legacy-DMG'en uden den native menu (bygget til macOS 12): appen viser selv ikonet i menulinjen

# ---- cloudflared (Offentlig adgang kørt i selve Fleetdeck): fast version, tjekket mod Cloudflares offentliggjorte SHA-256
FROM alpine:3.22 AS cloudflared
ARG TARGETARCH
ARG CLOUDFLARED_VERSION=2026.11.1
RUN apk add --no-cache curl \
 && A="${TARGETARCH:-amd64}" \
 && case "$A" in amd64) S=97bb3b02d23a5980fc9c15a1a270e692da1923f103f920d5b16d4931d5ab55e7 ;; arm64) S=b155cec3e48e489cfb8e5c602769fb8c771083018d8881d68b4d18a791567c5a ;; *) echo "ukendt arkitektur $A" >&2; exit 1 ;; esac \
 && curl -fsSL -o /cloudflared "https://github.com/cloudflare/cloudflared/releases/download/$CLOUDFLARED_VERSION/cloudflared-linux-$A" \
 && echo "$S  /cloudflared" | sha256sum -c - \
 && chmod 755 /cloudflared

# ---- 3. Runtime (+ dnsmasq til netværksboot: proxy-DHCP og TFTP)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update  && apt-get install -y --no-install-recommends dnsmasq-base iproute2 fdisk mtools xz-utils  && rm -rf /var/lib/apt/lists/*
ENV DATA_DIR=/data WINSETUP_DIR=/winsetup AGENT_DIR=/app/agent PORT=8585 TZ=Europe/Copenhagen \
    ASPNETCORE_ENVIRONMENT=Production DOTNET_gcServer=0
WORKDIR /app
COPY --from=build /out/server ./
COPY --from=web /out/wwwroot ./wwwroot
COPY --from=build /out/agent/Fleetdeck.exe ./agent/Fleetdeck.exe
COPY --from=build /out/linuxagent/fleetdeck-agent ./agent/fleetdeck-agent
COPY --from=build /out/linuxagent-arm64/fleetdeck-agent ./agent/fleetdeck-agent-arm64
COPY --from=build /out/linuxagent-arm/fleetdeck-agent ./agent/fleetdeck-agent-arm
COPY --from=build /out/linuxagent-musl-x64/fleetdeck-agent ./agent/fleetdeck-agent-musl-x64
COPY --from=build /out/linuxagent-musl-arm64/fleetdeck-agent ./agent/fleetdeck-agent-musl-arm64
COPY --from=build /out/macagent-arm64/fleetdeck-agent ./agent/fleetdeck-agent-mac-arm64
COPY --from=build /out/macagent-x64/fleetdeck-agent ./agent/fleetdeck-agent-mac-x64
COPY --from=build /out/macagent-x64-legacy/fleetdeck-agent ./agent/fleetdeck-agent-mac-x64-legacy
COPY --from=build /out/dmg/ ./agent/
COPY deploy/docker/dnsmasq.conf /app/pxe/dnsmasq.conf
COPY --from=cloudflared /cloudflared /usr/local/bin/cloudflared
COPY --chmod=755 deploy/docker/start.sh /app/start.sh
VOLUME ["/data", "/winsetup"]
EXPOSE 8585 67/udp 69/udp 4011/udp
HEALTHCHECK --interval=60s --timeout=5s --start-period=30s CMD ["dotnet", "Fleetdeck.Server.dll", "--healthcheck"]
ENTRYPOINT ["/app/start.sh"]
