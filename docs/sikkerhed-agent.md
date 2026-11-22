# Tillid mellem agent og server

Fleetdecks agenter kører som SYSTEM/root. Den, der kan få en agent til at tro, at et job kommer fra serveren, kan derfor køre kode på maskinen. Dette dokument beskriver, hvordan Fleetdeck beskytter mod det, og hvordan en eksisterende installation udrulles trinvis uden at nogen maskine mister forbindelsen.

## Kort fortalt

| Værn | Hvad det beskytter mod |
|---|---|
| **Signerede jobs** | En falsk server eller en mand-i-midten på netværket kan ikke sende jobs, scripts, afinstallation, selvopdatering eller en ny serveradresse. |
| **Nonce og udløb** | Et gammelt, opsnappet svar kan ikke afspilles igen. |
| **Sikker tilmelding** | Tilmeldingsnøglen kan ikke bruges til at overtage en eksisterende maskines identitet (jobs, gendannelsessæt, backup). |
| **Engangskoder** | Tilmelding uden at dele den globale nøgle. Den globale nøgle kan slås fra. |
| **https med fastlåst nøgle** | Trafikken (tokens, scripts, adgangsoplysninger) krypteres – også med et selvsigneret certifikat. |

## Signerede jobs

- Serveren har en signeringsnøgle (ECDSA P-256) i `data/keys/job-signing.pem`. Den laves ved første start.
- Nøglen er bevidst adskilt fra nøglen til agentfiler (`agent-signing.pem`), så en signatur over et svar aldrig kan bruges som signatur over en agentfil.
- Ved hvert check-in sender agenten en tilfældig engangs-nonce. Serveren signerer et svar, der indeholder:
  - maskinens id, agentens nonce, udstedelsestidspunkt og udløb (5 minutter);
  - jobs (med scripts og parametre), selvopdatering (version og den fulde SHA-256 af agentfilen), serveradresse, TLS-fingeraftryk, netværksdrev, "Hold kørende"-tjenester og share-login.
- Signaturen dækker de nøjagtige JSON-bytes. Agenten tjekker signaturen, før den læser indholdet, og bruger derefter kun det signerede indhold. Så afhænger signaturen ikke af, hvordan en ældre eller nyere agent serialiserer felterne.
- Et svar, der bærer agentens egen friske nonce, er beviseligt nyt. Derfor afvises det ikke, hvis maskinens ur går forkert (fx en Raspberry Pi uden batteriur). Udløbet gælder for signerede data uden nonce.

### Pinning af nøglen

- **Nye agenter** får nøglen i tilmeldingssvaret og kræver signerede jobs fra første check-in.
- **Eksisterende agenter** pinner nøglen første gang, de modtager et gyldigt signeret svar (trust on first use). Det sker automatisk, når de har opdateret sig selv.
- Når nøglen er pinnet, gælder:
  - Usignerede eller ugyldige svar afvises. Agenten kører ingen jobs, opdaterer sig ikke, flytter ikke server og rører ikke drev eller tjenester.
  - Afvisningen skrives i agentens log og vises i ikonet i proceslinjen/menulinjen ("Sikkerhed: …").
  - Agenten melder afvisningen til serveren ved næste check-in. Den vises på maskinen i Fleetdeck og sendes som notifikation.
  - Selvopdatering kræver, at den hentede fil passer med den signerede SHA-256.
- Agenten bliver ved med at tjekke ind, sende helbred og inventar, selv når den afviser jobs. Den bliver derfor aldrig usynlig.
- Fleetdeck viser pr. maskine: **Pinnet**, **Ikke pinnet**, **Ældre agent** eller **Anden nøgle** (agenten har pinnet en anden nøgle end serverens).

### Indstillingen "Kræv signerede jobs"

| Tilstand | Serveren | Ældre agenter | Agenter med pinnet nøgle |
|---|---|---|---|
| **Fra** | Signerer ikke. | Virker som før. | Afviser alle jobs. Bruges kun som nødbremse. |
| **Kun signér** (standard) | Signerer alle svar til agenter, der understøtter det. | Virker som før. | Kører kun signerede jobs. |
| **Kræv** | Som "Kun signér". Jobs leveres kun til agenter, der har pinnet nøglen. | Får ingen jobs, kun selvopdateringen. Efter opdateringen pinner de nøglen. | Kører kun signerede jobs. |

## Sikker tilmelding

- Den globale tilmeldingsnøgle (eller en engangskode) giver kun adgang til at tilmelde en **ny** maskine.
- En eksisterende maskine overtages kun, når tilmeldingen beviser, at det er den samme maskine:
  - agenten sender sit gamle token (geninstallation, hvor konfigurationen stadig findes – installationsprogrammerne gør det selv);
  - der bruges en engangskode, som er lavet til geninstallation af netop den maskine;
  - en administrator har startet "Geninstallér fra netværket" for maskinen, og udrulningen er aktiv.
- Ellers bliver det en **ny, ventende maskine** (`NAVN~2`). Den kan tjekke ind og vises i Fleetdeck. Den får ingen jobs, ingen share-adgang, ingen drev og ingen genskabelse fra gendannelsessæt, før en administrator vælger:
  - **Godkend som erstatning for NAVN**: den gamle maskinpost (profil, historik, gendannelsessæt, backup) overtager den nye agent, og den ventende post forsvinder. Bad installationen om genskabelse, startes den nu.
  - **Behold som ny maskine**: begge maskiner fortsætter hver for sig.
  - **Afvis**: den ventende maskine slettes, og dens token holder op med at virke.
- Alle agent-endpoints kræver maskinens eget token (256 bit, gemt som SHA-256 og sammenlignet i konstant tid), undtagen tilmelding, installationsscripts, agentfiler og den offentlige nøgle. Hvert endpoint er afgrænset til egen maskine.

### Engangskoder og den globale nøgle

- Under **Indstillinger → Agent-sikkerhed** laves engangskoder med udløb (som standard 24 timer, højst 30 dage). Koden vises én gang sammen med færdige installationskommandoer. Kun en hash gemmes.
- En kode kan bindes til **geninstallation af en bestemt maskine**. Så overtager tilmeldingen maskinen uden ekstra godkendelse.
- **Skift tilmeldingsnøgle** laver en ny global nøgle. Tilmeldte maskiner påvirkes ikke, fordi de bruger deres egne tokens. Gamle installationslinks, USB-pinde og netværksinstallationer med den gamle nøgle holder op med at virke.
- Den globale nøgle kan slås helt fra. Så virker kun engangskoder.

### SD-kort til Raspberry Pi

- Et kort fra **Lav SD-kort** bærer kun en engangskode (24 timer, én brug). Ved "erstat" er koden bundet til netop den maskine.
- På kortet ligger desuden serverens adresse, TLS-fingeraftrykket, navn, bruger, IP og den offentlige SSH-nøgle. Der er ingen adgangskode, og brugeren har kun login med nøgle.
- Førstestarten flytter koden fra boot-partitionen til `/var/lib/fleetdeck-firstboot` (kun root) og sletter den, når Pi'en er tilmeldt.
- Fleetdeck læser kortets filer tilbage, før billedet gives ud, og nægter at udlevere det, hvis noget ligner en privat nøgle eller et token.
- Billedet bygges uden ekstra rettigheder i containeren: kun boot-partitionen (FAT) ændres med mtools. Rodfilsystemet røres ikke.

## https

- Start serveren med miljøvariablen `HTTPS_PORT` (fx `8586`) for at få https **ved siden af** http.
  - Uden `HTTPS_CERT`/`HTTPS_KEY` (PEM) laver Fleetdeck selv et selvsigneret certifikat i `data/keys/tls-cert.pem` og `tls-key.pem`.
  - Fingeraftrykket (SHA-256 af certifikatets offentlige nøgle) vises under Indstillinger.
- Agenterne accepterer et certifikat, som systemet stoler på, **eller** et certifikat, hvis nøgle passer med det fastlåste fingeraftryk. Et selvsigneret certifikat er derfor nok på et hjemmenet.
- Fingeraftrykket når agenten på tre måder:
  - `--tls-pin sha256//…` ved installation (installationsscriptet sætter det selv, når serveradressen er https);
  - tilmeldingssvaret;
  - det signerede check-in-svar.
- Klarer en proxy https med sit eget selvsignerede certifikat, kan fingeraftrykket skrives i Indstillinger.
- Linux/Mac kan installere direkte over https med et selvsigneret certifikat:
  `curl -fsSLk --pinnedpubkey "sha256//<fingeraftryk>" "https://server:8586/install.sh?key=…" | sudo bash`
  Windows PowerShell 5.1 kan ikke fastlåse en nøgle i en one-liner. Installér derfor over http og lad agenten flytte til https bagefter (se trin 5).
- **Ingen agent tvinges over.** Indstillingen **https-adresse til agenter** flytter kun agenter, der har pinnet jobnøglen:
  - adressen og fingeraftrykket kommer i det signerede svar;
  - agenten flytter først, når den nye adresse svarer.
  - Installationer og ældre agenter bruger fortsat serveradressen.
- Holder https op med at virke for en flyttet agent (fx et nyt certifikat), prøver den efter ca. 20 mislykkede forsøg sin forrige adresse. Den skifter kun tilbage, hvis svaret derfra er signeret med den pinnede nøgle.

## Filer og mapper på maskinen

Agenten kører som SYSTEM/root. Alt, hvad den kører eller indlæser, må derfor kun kunne ændres af SYSTEM/root og administratorer.

**Windows**
- `Program Files\Fleetdeck` og `ProgramData\Fleetdeck` (med `work`) ejes af Administratorer. Kun SYSTEM og administratorer kan skrive.
- `ProgramData\Fleetdeck\userwork` (status og beskeder til ikonet) kan brugere kun læse. I `userwork\cmd` (ordrer fra ikonet) og
  `userwork\out` (output fra jobs i brugerens session) kan de kun oprette filer. De kan ikke oprette mapper (og dermed ingen
  junctions) og kun ændre deres egne filer. Tjenesten kører aldrig noget herfra og følger aldrig en genvej.
- Scripts og installationsprogrammer til jobs i brugerens session lægges i en ny mappe under `work`, som kun den bruger må læse.
- Før agenten kører noget med SYSTEM- eller administratorrettigheder, tjekker den filen, mappen den ligger i, og mapperne over.
  Ingen andre end SYSTEM, administratorer og Windows-tjenester må kunne ændre, erstatte eller omdøbe dem, eje dem eller lægge filer
  ved siden af (fx en DLL). Reparse points (genveje, junctions) afvises. Kan rettighederne ikke læses, afvises filen også.
  Et afvist job fejler med en forklaring i jobbets output.
- Installationen og hver start af tjenesten sætter rettighederne igen. Dermed bliver også ældre installationer rettet, og rester i
  `userwork` fjernes uden at følge genveje.

**Linux og macOS**
- Arbejdsmapper til gendannelsessæt, iPad-filer og reservekort ligger under `/var/lib/fleetdeck` (root, 0700), aldrig i `/var/tmp`.
  En genvej, hvor mappen skulle være, fjernes og følges ikke.
- På macOS søger root-scripts i systemmapperne før `/usr/local/bin`.
- Selvopdatering kræver altid en husket nøgle. Er den ikke husket, hentes den først. Lykkes det ikke, installeres opdateringen ikke.

**Netværksinstallation (PXE)**
- Svarfilen, som installeren henter uden login, indeholder en engangskode for netop den udrulning. Den globale tilmeldingsnøgle
  står aldrig i den. Koden virker én gang, udløber efter to dage uden brug, og ved en geninstallation gælder den kun den maskine.

## Backup

`data/keys/` skal med i enhver backup af serveren. Mappen indeholder:

- `job-signing.pem` – signerede jobs;
- `agent-signing.pem` – agentfiler;
- `tls-*.pem` – https;
- DataProtection-nøglerne.

Mistes `job-signing.pem`, afviser alle agenter med pinnet nøgle alle jobs og opdateringer. Den eneste vej tilbage er at lægge nøglen tilbage eller geninstallere agenten lokalt på hver maskine. Fleetdecks egen selvopdatering tager `keys/` med i sin backup.

## Trinvis udrulning på en eksisterende installation

Hvert trin kan stoppes og rulles tilbage. Det gælder dog ikke, når agenter først har pinnet nøglen (se "Risici").

1. **Tag backup** af datamappen, især `keys/`.
2. **Opdatér serveren.** Tilstanden er automatisk "Kun signér", og intet ændrer sig for agenterne endnu.
   - Ældre agenter får de samme svar som før.
   - De opdaterer sig selv inden for få check-ins (agentfilen er stadig signeret med den gamle filnøgle) og pinner derefter jobnøglen.
   - Godkend ventende maskiner under Maskiner, hvis nogen dukker op.
3. **Følg med under Indstillinger → Agent-sikkerhed**, indtil alle maskiner står som **Pinnet**.
   - Maskiner, der er slukket, pinner nøglen, næste gang de tænder.
   - Står en maskine som **Anden nøgle** eller med **Afviste jobs**, så undersøg det, før du går videre.
   - Kør gerne et harmløst job (fx et script med `echo ok`) på hver maskine, også på maskiner, der styrer noget kritisk, for at se, at signerede jobs går igennem.
4. **Skift til "Kræv"**, når alle maskiner er pinnet. Kun agenter med pinnet nøgle får herefter jobs.
5. **(Valgfrit) https:**
   1. Sæt `HTTPS_PORT`, og genstart serveren.
   2. Notér fingeraftrykket.
   3. Udfyld **https-adresse til agenter**.
   4. Agenterne flytter selv, én for én, når adressen svarer. På maskinen står så "https" og "TLS fastlåst".
   5. Behold http-porten åben, indtil alle står på https, og gerne også bagefter som nødadgang.
6. **(Valgfrit) Skift tilmeldingsnøglen**, så kopier af den gamle (installationslinks, netværksinstallation, USB-pinde) holder op med at virke. Brug derefter engangskoder til nye maskiner, og slå eventuelt den globale nøgle fra.

## Risici og grænser

- **Første kontakt** (installation over http, eller en eksisterende agents første signerede svar) er trust on first use. Kan en angriber allerede manipulere trafikken på det tidspunkt, kan han pinne sin egen nøgle. Brug https med fastlåst nøgle ved installationen, hvor det er muligt.
- **Ingen fjern-"afpinning".** Er nøglen pinnet, kan den ikke nulstilles fra serveren, for det ville en falsk server også kunne. Det betyder:
  - Mistes `job-signing.pem`, skal nøglen gendannes fra backup, eller agenten geninstalleres lokalt.
  - Rulles serveren tilbage til en version før signerede jobs, afviser pinnede agenter alle jobs, til serveren er opdateret igen. Check-in, helbred og inventar virker fortsat.
- **Tilstanden "Fra"** er en nødbremse. Pinnede agenter afviser jobs, så længe den er slået til.
- **Filer, som jobs henter undervejs** (installationsfiler, gendannelsessæt, iPad-profiler), er ikke signeret hver for sig. De beskyttes af tokenet og af https. Brug https, hvis netværket ikke er til at stole på.
- **Under en aktiv "Geninstallér fra netværket"** kan den globale nøgle overtage netop den maskine. Brug en engangskode bundet til maskinen for at lukke det vindue.
- **Den globale nøgle** ligger stadig i installationslinks, på WinSetup-sharet og i netværksinstallationen, så længe den er slået til. Skift den, når den har været delt bredt.

## ESP Flasher på pc'en (http://localhost)

USB fra browseren (Web Serial) kræver en sikker side. Chrome og Edge regner `http://localhost` som sikker, så agenten lytter
på `http://localhost:8587` og sender forespørgslerne videre til sin Fleetdeck-server (`LocalWeb` i Fleetdeck.Shared). Så virker
ESP Flasher uden at installere et certifikat. Menuen ved ikonet har punktet **ESP Flasher**.

- Der lyttes kun på 127.0.0.1 og ::1; forbindelser fra andre adresser lukkes. På Windows med eksklusiv port.
- Host skal være `localhost`, `127.0.0.1` eller `[::1]` med porten, ellers 421 (værn mod DNS-rebinding).
- En `Origin` fra en anden side, også en anden port på localhost, afvises med 403 – også for websockets.
- Én forespørgsel pr. forbindelse (`Connection: close`), så hver forespørgsel tjekkes. Absolut form og CONNECT afvises:
  målet er altid agentens egen server. En unix-socket (agenten på selve værten) bruges ikke.
- Agentens token sendes aldrig med. Browseren logger selv ind (egen cookie for localhost), og serverens CSRF-header og
  Origin-tjek virker uændret: proxyen sætter `X-Forwarded-Host: localhost:8587` og fjerner klientens egne `X-Forwarded-*`.
- Mod https bruges samme tillid som agenten (systemets certifikater eller det fastlåste fingeraftryk), uden klientnøgle.
- Porten ændres pr. maskine med `LocalWebPort` i agentens konfiguration (0 = slået fra) og for linket på ESP-siden med
  serverens miljøvariabel `LOCAL_WEB_PORT`.
