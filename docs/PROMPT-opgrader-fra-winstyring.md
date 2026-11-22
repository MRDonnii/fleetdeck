# Prompt: opgradér fra WinStyring til Fleetdeck

Giv denne fil til Claude (eller en anden AI-assistent) på den maskine, der kører din WinStyring-server, fx med:
"Læs PROMPT-opgrader-fra-winstyring.md og gør det, der står".

---

## Til assistenten

Du skal skifte en kørende **WinStyring**-server (2.x) ud med **Fleetdeck** (3.x) fra
`https://github.com/MRDonnii/fleetdeck`. Data skal med, og alle agenter skal flytte sig selv bagefter.
Spørg ejeren, før du gør noget, der ikke kan fortrydes, og tag backup først.

### Sådan virker skiftet
- Fleetdeck bruger samme database-format som WinStyring 2.x. Databasen hedder nu `fleetdeck.db` i stedet for `winstyring.db`.
- Agenterne (Windows, Linux, Raspberry Pi, Mac) spørger serveren om en ny version. Får de Fleetdeck-agenten, flytter de sig selv:
  - Windows: tjenesten `WinStyring` bliver til `Fleetdeck`, og `C:\Program Files\WinStyring` bliver til `C:\Program Files\Fleetdeck`.
  - Linux/Mac: `/opt/winstyring` bliver til `/opt/fleetdeck`.
  - Opsætning og nøgle følger med, og det gamle ryddes op af sig selv.
- **Serverens adresse og port skal være de samme som før.** Ellers kan de gamle agenter ikke finde den nye server.

### 1. Find den nuværende opsætning
1. Find WinStyring-containeren og noter følgende:
   - IP og port. Standard er 8585.
   - Netværk (fx `br0`/macvlan eller bridge).
   - Mappen til `/data`, fx `/mnt/user/appdata/winstyring`.
   - Mappen til `/winsetup`.
   - Miljøvariabler: `SERVER_URL`, `DISCOVERY`, `PXE`, `FRAME_ANCESTORS` og `TZ`.

   Kommandoer:
   - `docker ps`
   - `docker inspect <navn> --format '{{json .Config.Env}} {{json .Mounts}} {{json .NetworkSettings.Networks}}'`
2. Kører den på Unraid, så find skabelonen i `/boot/config/plugins/dockerMan/templates-user/my-WinStyring.xml`.

### 2. Byg Fleetdeck-billedet
```bash
git clone https://github.com/MRDonnii/fleetdeck.git /tmp/fleetdeck-src   # eller git pull, hvis den findes
cd /tmp/fleetdeck-src
git checkout "$(git describe --tags --abbrev=0)"                           # seneste udgivelse
docker build -t fleetdeck:latest .
```
Byggeriet tager 5–15 minutter. Det kræver kun Docker; .NET og Node hentes i byggeriet.

### 3. Stop WinStyring og flyt data (med backup)
```bash
docker stop <winstyring-container>
OLD=/mnt/user/appdata/winstyring        # ret til den rigtige sti fra trin 1
NEW=/mnt/user/appdata/fleetdeck
mkdir -p "$NEW"
cp -a "$OLD/winstyring.db" "$OLD/winstyring.db.backup-$(date +%Y%m%d)"
sqlite3 "$OLD/winstyring.db" ".backup '$NEW/fleetdeck.db'"   # findes sqlite3 ikke: cp -a "$OLD/winstyring.db" "$NEW/fleetdeck.db" (kun mens containeren er stoppet)
cp -a "$OLD/keys" "$NEW/" 2>/dev/null; cp -a "$OLD/winget-index.db" "$NEW/" 2>/dev/null
```
Lad den gamle mappe ligge som backup, til alt virker.

### 4. Start Fleetdeck med samme adresse
- **Unraid:**
  1. Kopiér `deploy/unraid/fleetdeck.xml` fra repoet til `/boot/config/plugins/dockerMan/templates-user/my-Fleetdeck.xml`.
  2. Udfyld skabelonen med værdierne fra trin 1: samme netværk, samme IP, samme port, `/data` = den nye mappe, samme `/winsetup` og samme miljøvariabler.
  3. Opret containeren via Docker → Add Container → vælg skabelonen "Fleetdeck".
  4. Sæt den til at starte automatisk. Fjern WinStyring fra autostart, så de to ikke kæmper om samme IP.
- **Anden Docker-vært:** `docker run -d --name Fleetdeck --restart unless-stopped` med samme `--network`, `--ip`, `-e …` og `-v <ny mappe>:/data -v <winsetup>:/winsetup` som WinStyring havde. Brug billedet `fleetdeck:latest`.

Tjek bagefter:
- `curl http://<ip>:<port>/api/health` skal svare `{"ok":true,"version":"3.x.y"}`.
- Log ind på websiden med det samme brugernavn som før.

### 5. Lad agenterne flytte sig
- Inden for ca. 5 minutter henter de gamle agenter den nye version og flytter sig selv.
- På websiden under **Maskiner** skal alle maskiner stå som online med nyeste agent.
- Står en maskine stille i mere end 15 minutter:
  - Windows: `sc query Fleetdeck` og `sc query WinStyring`. Loggen ligger i `C:\ProgramData\Fleetdeck\agent.log` (eller `C:\ProgramData\WinStyring\agent.log`, hvis den ikke er flyttet endnu).
  - Linux: `systemctl status fleetdeck-agent winstyring-agent` og `journalctl -u fleetdeck-agent -n 50`.
  - Kan den ikke nå serveren, så tjek ruten. Med **Tailscale** og "acceptér ruter" slået til kan trafik til serverens LAN-adresse gå en omvej og fejle. Løsningen på en pc, der står på samme LAN: `tailscale set --accept-routes=false`.
- Unraid-serveren selv (Unraid-agenten): kør installationskommandoen fra **Maskiner → Tilføj maskine → Linux / Raspberry Pi → Selve Unraid-serveren** igen. Den bruger socket-filen i appdata, fordi Unraid ikke kan nå sine egne macvlan-containere.

### 6. Logoet i Unraids Docker-liste
Unraid kan ikke hente ikonet fra containerens egen adresse, når den ligger på macvlan. Læg det direkte:
```bash
cp /tmp/fleetdeck-src/web/public/fleetdeck.png /boot/config/plugins/dockerMan/images/Fleetdeck-icon.png
cp /tmp/fleetdeck-src/web/public/fleetdeck.png /var/lib/docker/unraid/images/Fleetdeck-icon.png
```

### 7. Ryd op (når alt har kørt et døgn)
1. Slet WinStyring-containeren og den gamle skabelon `my-WinStyring.xml`.
2. Lad `appdata/winstyring` ligge et par uger som backup, og slet den så.

### Rapportér tilbage
Skriv kort til ejeren:
- hvilken version der kører
- hvor mange maskiner der er flyttet
- hvad der evt. stadig mangler

Fejl i Fleetdeck oprettes som issue på GitHub-repoet.
