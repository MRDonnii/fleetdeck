# Opdatering af Fleetdeck fra brugerfladen

Fleetdeck kan opdatere sig selv: **Indstillinger → Generelt → Opdatering af Fleetdeck** (kun admin).
Ingen skal ind i Docker eller Unraid for at opdatere.

## Sådan virker det

1. Fleetdeck søger efter nye versioner i **GitHub-udgivelserne** for projektet (standard `MRDonnii/fleetdeck`)
   hver 12. time (hver 6. med automatisk opdatering), eller når der klikkes **Søg efter opdatering**.
   - **Stabil**: kun udgivelser, der *ikke* er markeret som "pre-release" på GitHub.
   - **Beta**: også pre-releases.
2. **Opdatér nu** viser udgivelsesnoterne og beder om bekræftelse. En ældre (eller samme) version kræver,
   at "Tillad ældre version" sættes – ellers afvises det.
3. Serveren kan ikke udskifte sin egen container indefra. Den laver derfor et job til **agenten på Docker-værten**
   (maskinen, containeren kører på). Værten findes automatisk ud fra agentens container-liste (Unraid: container-id,
   IP-adresse og billednavn) – eller vælges under "Indstillinger og versioner" (Docker-vært + container).
4. Jobbet (et bash-script, der kører løsrevet fra agenten, så det gøres færdigt, selv om jobbet stoppes):
   1. kontrollerer, at containeren er den Fleetdeck, der bad om opdateringen (værtsnavn/container-id),
   2. henter det nye billede (`docker pull`), indlæser billedfilen fra udgivelsen (`docker load`) eller bygger
      den lokale build-mappe (`docker build`) – mens den gamle version stadig kører,
   3. stopper containeren og kopierer `fleetdeck.db` (+ `-wal`/`-shm`) og `keys/` (og Unraid-skabelonen)
      til `<datamappe>/opdatering-backup/<tidspunkt>-v<gammel version>/` – de 5 nyeste gemmes,
   4. omdøber den gamle container til `<navn>-fd-forrige` og laver den nye med samme navn:
      - **Unraid**: skabelonens `<Repository>` sættes til det nye billede, og
        `/usr/local/emhttp/plugins/dynamix.docker.manager/scripts/rebuild_container <navn>` laver containeren;
      - **almindelig Docker** (eller hvis Unraid ikke laver den): `docker inspect` af den gamle → `docker create` med
        samme netværk, faste IP, mapper/volumener, porte, miljøvariabler, mærker, genstartspolitik, capabilities,
        enheder, log-opsætning og CPU/RAM-grænser (billedets egne standardværdier tages fra det nye billede),
   5. venter (standard 5 min) på, at `/api/health` svarer med den nye version (via `fleetdeck.sock` i datamappen,
      containerens IP eller `docker exec … --healthcheck`),
   6. lykkes det, slettes den gamle container (billedet beholdes). Ellers **rulles tilbage**: den nye container
      fjernes, databasen og nøglerne lægges tilbage fra backuppen, skabelonen sættes tilbage, og den gamle container
      startes igen.
5. Fremdriften vises live. Mens serveren genstarter, spørger siden `/api/health`, til den svarer, og henter så
   den nye brugerflade. Resultatet skrives i loggen (`update.start`, `update.done`/`update.failed`).

Output og log: jobbet under **Jobs** og på værten i `/tmp/fleetdeck-opdatering/<kørsel>/log`.

### Automatisk opdatering om natten

Slås til i panelet (med klokkeslæt, standard kl. 3). Kun nyere versioner på den valgte kanal, tidligst 2 timer
efter udgivelsen, og samme version forsøges aldrig automatisk to gange (fx efter en tilbagerulning).

## Krav

- Fleetdeck kører i Docker, og **Fleetdeck-agenten er installeret på Docker-værten** (Unraid eller Linux) som root.
- Docker-værten har `curl` (til billedfil og helbredstjek) – Unraid har det.
- Billedet skal være offentligt i registret – eller værten skal være logget ind (`docker login ghcr.io`) i forvejen.
  Fleetdeck sender aldrig adgangskoder eller nøgler til værten. En GitHub-nøgle (valgfri, til private projekter)
  bruges kun af serveren til at læse udgivelserne og vises aldrig igen.

## Sådan udgives en version (så andre husstande kan opdatere)

1. Hæv `<Version>` i `Directory.Build.props` (fx `3.16.0`, eller `3.17.0-beta.1` til en beta) og commit.
2. Tag commit'en med **præcis** `v<Version>` og skub tagget (`deploy/deploy.sh` gør det):
   `git tag -a v3.16.0 -m "Fleetdeck 3.16.0" && git push origin v3.16.0`
3. GitHub Actions (`.github/workflows/release-image.yml`) bygger og skubber
   `ghcr.io/<ejer med små bogstaver>/fleetdeck:<version>` (+ `:stable`/`:beta`). Jobbet fejler, hvis tag og
   `<Version>` ikke passer sammen – serveren venter nemlig på, at `/api/health` melder netop den version.
   Er menuen (src/Fleetdeck.MacMenu, MacApp.cs) uændret, så kør workflowet med `menu_from` = forrige version (fx `3.45.0`):
   så genbruges FleetdeckMenu byte for byte fra det billede, og Mac'erne bygger ikke menuen om.
   **Første gang**: gør pakken offentlig på GitHub (Packages → fleetdeck → Package settings → Change visibility → Public).
4. Når billedet er bygget: opret **GitHub-udgivelsen** for tagget med udgivelsesnoter (vises i bekræftelsen).
   - Beta: sæt "Set as a pre-release". Stabil: lad den være almindelig ("latest").
   - Ligger billedet et andet sted, så skriv en linje i noterne: `Docker-billede: registry.example/ejer/fleetdeck:3.16.0`
     (går forud for indstillingen "Docker-billede").
   - Uden registry: vedhæft `docker save fleetdeck:3.16.0 | gzip > fleetdeck-image-3.16.0.tar.gz` til udgivelsen
     og lad feltet "Docker-billede" være tomt – så hentes og indlæses filen på værten.

Billedet findes ud fra indstillingen **Docker-billede** (standard `ghcr.io/mrdonnii/fleetdeck:{version}`;
`{version}` = versionen uden "v", `{tag}` = tagget). En fork sætter sit eget projekt og billede.

## Udvikling: lokal build-mappe

Kilde **Lokal build-mappe** (standard `/winsetup/Fleetdeck` = `WinSetup/Fleetdeck` på sharen):

```bash
deploy/deploy.sh --source-only    # tagger, lægger koden + RELEASE-NOTES.md på sharen – bygger intet
```

Klik så **Søg efter opdatering** → **Opdatér nu**. Jobbet bygger `fleetdeck:<version>` på værten fra mappen og
skifter containeren som ovenfor. Samme version igen kræver "Tillad ældre version" (geninstallation).
`deploy/deploy.sh` uden `--source-only` virker som før (bygger billedet; derefter Force update i Unraid).

## Gå tilbage manuelt

Backupperne ligger i `<datamappe>/opdatering-backup/`. Stop containeren, kopiér `fleetdeck.db*` og `keys/`
tilbage til datamappen, og start den tidligere version (det gamle billede beholdes). Eller vælg en ældre version
i listen med "Tillad ældre version" – en database, der er opgraderet af en nyere version, virker dog måske ikke
med en ældre.
