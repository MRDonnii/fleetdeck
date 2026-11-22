# Installér Fleetdeck

Fleetdeck er **én Docker-container**. Den kan køre på enhver maskine med Docker, der er tændt hele tiden:
en Linux-server, en NAS (Unraid, Synology, QNAP) eller en Raspberry Pi 4/5 med 64-bit styresystem.
Billedet findes til **x86-64 (amd64)** og **ARM64**.

Når containeren kører, åbner du den i browseren. En **opsætningsguide** opretter administratoren og spørger om resten.

- [1. Docker compose (anbefalet)](#1-docker-compose-anbefalet)
- [2. Docker run](#2-docker-run)
- [3. Unraid](#3-unraid)
- [4. Synology og QNAP](#4-synology-og-qnap)
- [5. Raspberry Pi og andre ARM64-maskiner](#5-raspberry-pi-og-andre-arm64-maskiner)
- [6. Første start: opsætningsguiden](#6-første-start-opsætningsguiden)
- [7. Variabler og mapper](#7-variabler-og-mapper)
- [8. Netværksboot (PXE)](#8-netværksboot-pxe)
- [9. Opdatering](#9-opdatering)

## Før du starter

- Docker 20.10 eller nyere (med `docker compose`, eller Container Manager/Container Station på en NAS).
- En fast IP-adresse til værten (reservér den i routeren). Maskinerne finder serveren på den adresse.
- Port **8585** skal være fri på værten. Vælg en anden port med `FLEETDECK_PORT`.
- Åbn **ikke** porten i routeren. Fleetdeck er beregnet til hjemmenettet.

## 1. Docker compose (anbefalet)

```sh
mkdir -p /opt/fleetdeck && cd /opt/fleetdeck
curl -fsSLO https://raw.githubusercontent.com/mrdonnii/fleetdeck/main/deploy/compose/docker-compose.yml
# valgfrit: tidszone og adresse
printf 'TZ=Europe/Stockholm\nSERVER_URL=http://192.168.1.10:8585\n' > .env
docker compose up -d
```

Åbn `http://<værtens IP>:8585`. Filen er [`deploy/compose/docker-compose.yml`](../deploy/compose/docker-compose.yml).
Data ligger i `./data` ved siden af filen. Containeren genstarter selv (`restart: unless-stopped`) og har en sundhedstjek, så
`docker ps` viser `healthy`, når den er klar.

## 2. Docker run

Det samme uden compose:

```sh
docker run -d --name fleetdeck --restart unless-stopped \
  -p 8585:8585 \
  -e TZ=Europe/Stockholm \
  -e SERVER_URL=http://192.168.1.10:8585 \
  -e PXE=0 -e DISCOVERY=0 \
  -v /opt/fleetdeck/data:/data \
  -v /opt/fleetdeck/winsetup:/winsetup \
  ghcr.io/mrdonnii/fleetdeck:stable
```

Til https ved siden af http: tilføj `-p 8443:8443 -e HTTPS_PORT=8443`.

## 3. Unraid

1. Kopiér [`deploy/unraid/fleetdeck.xml`](../deploy/unraid/fleetdeck.xml) til
   `/boot/config/plugins/dockerMan/templates-user/my-Fleetdeck.xml` (fx via flash-sharen).
2. **Docker → Add Container → Template: Fleetdeck**.
3. Giv containeren en fast IP på `br0`, og skriv samme adresse i **Serveradresse** (fx `http://192.168.1.30:8585`).
4. Tryk **Apply**, og åbn adressen.

Skabelonen bruger `br0`, så containeren har sin egen IP. Det er nødvendigt for netværksboot, som ikke må dele port 67/69
med selve Unraid. Linux-agenten på Unraid-værten taler med containeren gennem en socket-fil i datamappen.

## 4. Synology og QNAP

**Synology (DSM 7.2+, Container Manager):**

1. Lav mappen `docker/fleetdeck` i File Station.
2. **Container Manager → Projekt → Opret**. Vælg mappen, og vælg "Opret docker-compose.yml". Indsæt indholdet af
   `docker-compose.yml`.
3. Ret evt. `TZ` direkte i filen (fx `TZ: Europe/Stockholm`), og tryk **Næste → Udført**.
4. Er port 8585 optaget, så ret venstre side af `ports:` (fx `"18585:8585"`) og brug den port i adressen.

**QNAP (Container Station 3):**

1. **Applikationer → Opret**. Giv den navnet `fleetdeck`, og indsæt indholdet af `docker-compose.yml`.
2. Skift `./data` og `./winsetup` til mapper på en delt mappe, fx `/share/Container/fleetdeck/data`.
3. Tryk **Opret**.

På begge: brug NAS'ens IP-adresse som serveradresse. Netværksboot kræver `network_mode: host`, og det kolliderer ofte med
NAS'ens egne tjenester. Lad PXE være slået fra, medmindre du ved, at port 67 og 69 er ledige.

## 5. Raspberry Pi og andre ARM64-maskiner

Billedet findes til `linux/arm64`, og Docker vælger den rigtige udgave selv. Det kræver et **64-bit** styresystem
(Raspberry Pi OS 64-bit eller Ubuntu Server). Brug compose-filen som ovenfor. En Pi 4 med 4 GB eller mere er nok til en
husstand. Læg `./data` på en SSD frem for SD-kortet, hvis du kan.

32-bit ARM (armv7) understøttes ikke af serveren. Agenten findes derimod også til 32-bit Raspberry Pi.

## 6. Første start: opsætningsguiden

Første gang du åbner Fleetdeck, er der ingen brugere, og guiden starter:

1. **Start**: ny opsætning, eller **Gendan fra backup** (en `.fdbackup`-fil fra en tidligere server; maskinerne fortsætter som før).
2. **Administrator**: brugernavn og adgangskode (mindst 8 tegn).
3. **Husstand og adresse**: husstandens navn og den adresse, maskinerne skal bruge. Guiden udfylder den ud fra adressen i
   browseren (eller `SERVER_URL`) og advarer, hvis den ligner `localhost` eller en intern Docker-adresse.
   Med **Test adressen** kan du se, om den svarer.
4. **Sprog, tastatur og tidszone** til netværksinstallationer af Windows og Ubuntu og til overvågede iPads. Forslagene
   kommer fra browseren.
5. **Notifikationer**: ingen, eller Home Assistant via MQTT.
6. **Overblik**: intet gemmes før her. Derefter er du logget ind.
7. **Totrinsbekræftelse** (anbefales, kan springes over) og **Tilføj din første maskine**.

Guiden virker kun, så længe der ingen brugere er. Bagefter svarer den ikke (404), og alt ændres under Indstillinger.
Sprog, tastatur og tidszone til installationer ligger under **Udrulning**.

> Windows-installationen bruger sproget fra Windows-mediet. Vælger du et sprog, som ISO'en ikke indeholder, kan
> Windows Setup spørge om sproget. Brug en ISO på samme sprog, eller en flersproget ISO.

## 7. Variabler og mapper

| Variabel | Standard | Betydning |
|---|---|---|
| `PORT` | `8585` | Porten i containeren (http). |
| `SERVER_URL` | (tom) | Adressen, maskinerne bruger. Vinder over Indstillinger, når den er ny eller ændret. |
| `TZ` | `Europe/Copenhagen` i billedet, `UTC` i compose-filen | Serverens tidszone til planlagte opdateringer og backup. |
| `HTTPS_PORT` | (tom) | Slår https til på denne port. Uden `HTTPS_CERT` laves et selvsigneret certifikat, som agenterne fastlåser. |
| `HTTPS_CERT`, `HTTPS_KEY` | (tom) | Egne PEM-filer til https. |
| `PXE` | `1` | `0` slår netværksboot (proxy-DHCP og TFTP) fra. |
| `PXE_INTERFACE` | netkortet med standardruten | Netkort til netværksboot. |
| `DISCOVERY` | `1` | `0` slår "Find automatisk" fra (UDP 8586, broadcast). |
| `FRAME_ANCESTORS` | `'self'` | Sider, der må vise Fleetdeck i en iframe. |
| `TRUSTED_PROXIES` | lokale netværk | Proxyer, hvis `X-Forwarded-For` stoles på. |
| `AGENT_SOCKET` | `/data/fleetdeck.sock` | Socket til agenten på selve værten. `0` slår den fra. |
| `DATA_DIR`, `WINSETUP_DIR` | `/data`, `/winsetup` | Mapperne i containeren (skal normalt ikke ændres). |

| Mappe i containeren | Indhold | Backup? |
|---|---|---|
| `/data` | Database, nøgler, certifikater og Fleetdecks egne backups | **Ja** (eller brug Indstillinger → Backup) |
| `/winsetup` | Windows-installationsfiler, netværksboot, drivere og USB-nøglen | Valgfri, kan hentes igen |

## 8. Netværksboot (PXE)

Netværksinstallation af Windows og Ubuntu kræver, at containeren kan modtage broadcast (DHCP på port 67 og TFTP på 69).
Det kan den ikke bag Dockers NAT. Brug derfor en af disse:

- **Egen IP** (macvlan/ipvlan, som `br0` på Unraid), eller
- **`network_mode: host`**: i compose-filen sletter du `ports:`, sætter `PXE: "1"` og `DISCOVERY: "1"` og fjerner `#`
  foran `network_mode: host` og `cap_add`.

Der må ikke køre en anden PXE- eller TFTP-server på samme net. Fleetdeck er kun proxy-DHCP, så din router deler stadig
IP-adresser ud. Sprog, tastatur og tidszone til installationerne vælges i guiden og under **Udrulning**.

## 9. Opdatering

- **Compose**: `docker compose pull && docker compose up -d`.
- **Docker run**: `docker pull ghcr.io/mrdonnii/fleetdeck:stable`, og lav containeren igen med samme kommando.
- **Unraid**: Docker → Fleetdeck → **Check for updates**, eller opdatér fra Fleetdeck (Indstillinger → Opdatering).

Opdatering fra Fleetdeck selv kræver Linux-agenten på Docker-værten. Den genskaber containeren med `docker run`. Bruger du
compose, så opdatér hellere med compose, så filen og containeren passer sammen.

Tag en backup først (Indstillinger → Backup). Databasen migreres automatisk ved start, og der gemmes en kopi før hver migrering.
