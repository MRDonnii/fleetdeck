# Backup og gendannelse af Fleetdeck

Fleetdeck kan tage backup af sig selv og gendanne fra en backup – også på en helt ny server. Alt foregår under
**Indstillinger → Backup** (kun for administratorer).

## Hvad er med i en backup?

En backup er én fil, `*.fdbackup`. Den indeholder:

| Del | Altid med? | Hvorfor |
|---|---|---|
| Databasen (`fleetdeck.db`) | Ja | Maskiner, brugere, profiler, pakker, indstillinger, historik. Tages med SQLite's `VACUUM INTO`, så kopien er konsistent, selv om serveren kører. |
| Nøglerne (`keys/`) | Ja | Agenternes signeringsnøgle og de nøgler, der krypterer gemte adgangskoder (AMT, AdGuard, tunnel-token, iPad-nøgler m.m.). Uden dem skal alle agenter tilmeldes igen, og gemte adgangskoder skal tastes igen. |
| Egne udvidelser (`extensions.json`) | Ja, hvis filen findes | |
| Gendannelsessæt (`kits/`) | Kan vælges | Kan være store og kan laves igen. |
| iPad-backups (`ipad-backups/`) | Kan vælges | Kan være meget store og kan laves igen. |

**Ikke med:** WinSetup-mappen (installationsfiler, PXE, Linux-backups af maskinerne) – den ligger typisk på et share og skal
have sin egen backup. Heller ikke winget-indekset (hentes igen af sig selv).

Desuden ligger der et manifest med Fleetdeck-versionen, tidspunktet og databasens seneste migration.

## Kryptering

Backuppen indeholder hemmeligheder og er derfor altid krypteret:

- AES-256-GCM i blokke på 1 MiB. Hver blok har sit eget mærke, og blokkenes rækkefølge og den sidste blok er bundet ind,
  så en ændret, afkortet eller sammenklippet fil afvises.
- Nøglen afledes af adgangskoden med PBKDF2-HMAC-SHA512 (600.000 runder, tilfældigt salt).
- Filens hoved (version, tidspunkt, indhold) kan læses uden adgangskode, men er autentificeret sammen med indholdet.
- Adgangskoden skal være på mindst 10 tegn. **Den kan ikke nulstilles** – glemmer du den, kan backuppen ikke bruges.

## Tag backup nu

Tryk **Tag backup nu**, vælg en adgangskode (eller brug den gemte, se nedenfor) og evt. de store dele. Backuppen gemmes
på serveren og hentes samtidig ned i browseren. Gem filen et andet sted end på Fleetdeck-serveren.

## Automatisk backup

Som standard tages der en backup hver nat (kl. 3, serverens tid):

- Den gemmes i datamappen, i undermappen `backups` (kan ændres), og de 7 nyeste beholdes (kan ændres).
- **Ekstra kopi til:** en fuld sti, fx et share eller en USB-disk, der er monteret i containeren
  (`-v /mnt/backup/fleetdeck:/backup` → skriv `/backup`). Samme antal beholdes dér. Det anbefales kraftigt – en backup på
  samme disk som serveren hjælper ikke, hvis disken dør.
- Fejler backuppen, eller er den seneste vellykkede over 2 dage gammel, vises en rød alarm øverst for administratorer,
  og der sendes besked via Home Assistant og/eller webhook (de samme som maskinernes alarmer). Når det virker igen,
  kommer der en "kører igen"-besked. En fejlet backup prøves igen efter en time.

### Backup-adgangskoden

For at den automatiske backup kan køre af sig selv, gemmes adgangskoden på serveren – krypteret med serverens egen nøgle.
**Skal du gendanne på en ny server, skal du bruge den adgangskode, du selv har valgt** – skriv den ned et sikkert sted
(fx i en adgangskode-manager), ikke kun på Fleetdeck-serveren.

Er der ikke valgt en adgangskode, låses de automatiske backups med serverens egen nøgle (`backup-local.key` i datamappen).
De kan så kun gendannes på den samme server – ikke efter et diskbrud. Vælg derfor en adgangskode.

## Automatiske sikkerhedskopier

Uden at du gør noget, laver Fleetdeck også:

- **Før opdatering:** når en ny version starter og skal opdatere databasen (EF-migrationer), tages der først en backup.
- **Før gendannelse:** før en gendannelse lægges ind, gemmes de hidtidige data.

De 3 nyeste af hver slags gemmes i `<datamappe>/backups`. De er låst med serverens egen nøgle, så de kan gendannes uden
adgangskode – men kun på denne server.

## Gendan

1. **Indstillinger → Backup → Gendan fra fil** (eller **Gendan** ud for en gemt backup).
2. Skriv backuppens adgangskode og `GENDAN` for at bekræfte.
3. Fleetdeck kontrollerer backuppen, *før* noget røres: adgangskode, at filen ikke er ændret, at databasen er hel, og at
   backuppen ikke er fra en nyere Fleetdeck-version (så skal Fleetdeck opdateres først).
4. Backuppen pakkes ud i `<datamappe>/restore-pending`, og serveren genstarter.
5. Ved start – før databasen åbnes – gemmes de nuværende data som en "Før gendannelse"-backup, og backuppen lægges ind.
   Går noget galt undervejs, lægges de oprindelige filer tilbage. Resultatet vises øverst i Backup-fanen.
6. Er backuppen fra en ældre version, opdateres databasen bagefter som ved en almindelig opdatering.

Efter gendannelsen logger du ind med brugerne fra backuppen. Agenterne fortsætter som før, fordi nøglerne er de samme.

**Genstart:** serveren stopper med slutkode 75 og forventer at blive startet igen af Docker eller systemd. Containeren skal
derfor have en genstartspolitik, fx `--restart unless-stopped` (eller `restart: unless-stopped` i compose). Uden den
lægges backuppen ind, næste gang du selv starter containeren.

### Ny server (første start)

Starter Fleetdeck med en tom datamappe, viser login-siden **Gendan fra backup** under "Opret den første administrator".
Vælg `.fdbackup`-filen og skriv adgangskoden. Det kan kun lade sig gøre, så længe der ikke er oprettet nogen bruger.

Husk at give den nye server samme adresse (eller ændre "Serverens adresse" bagefter) og at montere WinSetup-mappen igen.

## Glemt administrator-adgangskode

Der er to måder, når du ikke kan logge ind:

**Kommando** (mens serveren kører):

```sh
docker exec -it fleetdeck dotnet /app/Fleetdeck.Server.dll reset-admin            # den eneste administrator
docker exec -it fleetdeck dotnet /app/Fleetdeck.Server.dll reset-admin <bruger>   # en bestemt bruger
```

Uden Docker: `dotnet Fleetdeck.Server.dll reset-admin <bruger>` med samme `DATA_DIR` som serveren.

**Fil i datamappen** (hvis du kun kan nå filerne, fx via et share):

1. Læg en fil med navnet `reset-admin` i datamappen. Den kan være tom eller indeholde brugernavnet.
2. Genstart Fleetdeck.
3. Den midlertidige adgangskode står i containerens log (`docker logs fleetdeck`). Filen slettes af sig selv.

I begge tilfælde får brugeren en ny, tilfældig adgangskode, bliver administrator, og alle dens sessioner logges ud.
Findes brugernavnet ikke, oprettes det som administrator. Skift adgangskoden under **Indstillinger → Brugere**, når du er
logget ind. Handlingen skrives i loggen (Log-siden).

## Filer i datamappen

| Fil/mappe | Betydning |
|---|---|
| `backups/` | Gemte backups (standardmappen) og de automatiske sikkerhedskopier |
| `backups/.uploads/` | En uploadet backup, der venter på at blive gendannet |
| `backup-local.key` | Serverens egen backup-nøgle (til sikkerhedskopierne). Er ikke med i backups og overskrives aldrig. |
| `restore-pending/` | En kontrolleret backup, der lægges ind ved næste start |
| `restore-result.json` | Resultatet af seneste gendannelse |
| `reset-admin` | Engangsfil til nulstilling af administrator-adgangskoden |
