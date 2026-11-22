# Windows Opsætning + Fleetdeck: samlet løsning

## Overblik

| Del | Hvor | Hvad |
|---|---|---|
| **Alt samlet ét sted** | `\\SERVER\PC-Reinstall\WinSetup` (Unraid: `/mnt/user/PC-Reinstall/WinSetup`) | Opsætning, programmer, drivere, backup og Fleetdeck |
| **Fleetdeck – ÉN container** | fx **http://192.168.1.29:8585** | Container `Fleetdeck` (netværk br0, egen IP). Indeholder webside, API, agent-udlevering **og** netværksboot (dnsmasq proxy-DHCP + TFTP). **Første gang du åbner siden, opretter du selv din administrator** |
| **Pc'erne** | fx STUE-PC | Agenten kører som tjenesten "Fleetdeck Agent" og flytter selv med, hvis serverens adresse ændres |
| **Netværksboot (PXE)** | Inde i `Fleetdeck` | Svarer kun UEFI x64-pc'er. Microsofts signerede boot (Windows) og Canonicals signerede shim/GRUB (Ubuntu), så **Secure Boot kan blive slået til** |
| **USB-nøgle** | Windows Opsætning → *Gør USB-nøgle klar…* | Samme installation som netværksboot |
| **Kildekode** | GitHub (se *G. Udvikling*) | Alt i `WinSetup`-mappen undtagen backup, drivere, installationsfiler, nøgler og dine personlige filer |

> Fleetdeck har sin egen IP, så den ikke støder sammen med DHCP/TFTP-tjenester, som Unraid selv måtte køre. Unraid selv kan ikke
> åbne containerens IP (Docker-begrænsning for egne IP'er), men alle andre enheder på netværket kan.

### Udrul en pc over netværket (Windows eller Ubuntu)

Åbn **Udrulning** i Fleetdeck. Du kan enten lægge en udrulning klar på forhånd (pc'ens MAC-adresse), eller bare boote pc'en og vælge i menuen.

1. Boot pc'en fra netværket: boot-menu-tasten (fx **F11** eller **F12**) → **UEFI: PXE / Network**. I en VM: boot-menu → *UEFI PXEv4*.
2. **Windows:** det grafiske installationsprogram starter af sig selv. Log ind med din Unraid-bruger (den du bruger til `\\SERVER\PC-Reinstall`, ikke root). Er der en udrulning klar, vises den – tryk Enter. Ellers vælger du:
   - **1 Helt ren** – kun Windows. Ingen programmer, ingen Fleetdeck. Du vælger bare en adgangskode ved første login.
   - **2 Med mine ting** – pc'ens profil (eller standardprofilen *Fra WinSetup*): drivere, programmer og spil, dine filer, baggrund, Windows Update.
   - **3 Vælg profil** – fx en børne-pc-profil.
   - Derefter **pc-navn** og **Windows-bruger**. Samme navn som før = Fleetdeck genkender pc'en (profil og historik bevares).
3. **Ubuntu (24.04 eller 26.04, Desktop eller Server):** tryk først **Hent** under *Styresystemer* (ca. 3 GB fra releases.ubuntu.com, kontrolleres med SHA-256).
   Lav så en udrulning med pc'ens MAC, værtsnavn, bruger og adgangskode. Pc'en får automatisk Ubuntus bootfil og installerer selv. Kræver mindst **8 GB RAM**.
4. Både Windows og Ubuntu **stopper ved valg af disk** – intet slettes, før du vælger det.

"Geninstallér fra netværket" i Hjælperen lægger selv en udrulning klar, så pc'en kører igennem med samme navn, bruger og profil.

### Test i en VM på Unraid (anbefales før en rigtig pc)

VMs → Add VM → **Windows 11**:
- BIOS **OVMF TPM**, maskine Q35, **8 GB RAM** eller mere, 2–4 kerner.
- Disk: 64 GB, bus **SATA** (Windows-installationen har ikke VirtIO-diskdrivere).
- Netværk: bridge **br0**, model **e1000** (WinPE har ikke VirtIO-netkort). Notér MAC-adressen.
- Ingen ISO. Start VM'en, tryk **Esc** i opstarten → *Boot Manager* → **UEFI PXEv4**.
- Brug et **testnavn** som `TEST-VM` – ikke navnet på en rigtig pc, ellers overtager VM'en dens plads i Fleetdeck.
- Slet testmaskinen under *Maskiner* bagefter.

### Linux-maskiner – styring, backup og genskabelse

Linux-agenten kører som systemtjenesten `fleetdeck-agent`. Den starter selv ved opstart uden login, har lav CPU- og diskprioritet
og gør kun noget, når du trykker i Fleetdeck (eller profilen siger det). En profil kan indeholde de pakker, maskinen skal have;
forsvinder én, lægger Fleetdeck den på igen.

**Genskab-kit hver nat** → `\\SERVER\PC-Reinstall\WinSetup\Backup\<NAVN>\linux\` (de 7 seneste, kun læsbare for root): pakkeliste,
pakkekilder og nøgler, opsætning, egne tjenester, brugerindstillinger og crontab. Kittet kan indeholde adgangsoplysninger og ligger
derfor kun læsbart for root på Unraid. Store datamapper skal du selv tage backup af.

**Hvis maskinen skal genskabes:** Fleetdeck → **Udrulning** → Ny udrulning → Ubuntu (samme version som før), maskinens MAC, værtsnavn
og bruger. Boot maskinen fra netværket. Agenten installerer sig selv i den nye Ubuntu, tilmelder sig med samme navn (profil og historik
bevares) og lægger det seneste genskab-kit på. Crontab lægges ikke på automatisk: kopiér først dine data tilbage, og kør så
`crontab ~/crontab-fra-backup.txt`.

### Programkatalog (alt fra ninite.com)

Under **Pakker og spil** ligger ca. 150 populære gratis programmer (browsere, medier, runtimes, design, kontor, sikkerhed, værktøjer,
udvikling) med en kort dansk beskrivelse – filtrér på kategori eller søg. De ligger **ikke** på nogen profil, så intet installeres,
før du vælger det (Installér på… eller tilføj til en profil). Seks programmer findes ikke i winget (fx FileZilla, RustDesk, MusicBee):
de står som *fil*-pakker – læg installationsfilen i `Installers` og vælg den. Knappen **Tilføj programkatalog** lægger nye til, hvis kataloget udvides.
Bemærk: Avast, AVG og Avira slår Windows Defender fra, når de er installeret.

### Natlig vedligeholdelse, sluk og manuel styring

- **Natlig vedligeholdelse** (pr. profil, **slået fra som standard**): på det valgte tidspunkt tændes slukkede pc'er med Wake-on-LAN,
  opgaverne køres (Windows Update, alle programmer, oprydning, virusdefinitioner), og bagefter genstartes pc'en hvis nødvendigt og/eller
  slukkes igen. Om morgenen kommer én besked med, hvordan det gik pr. maskine. Opdateringerne kører i runder med genstart imellem, til intet
  er tilbage (standard højst 4). Var en maskine slukket, indhentes kørslen, når den kommer online (standard op til 3 døgn) – stille: den
  genstarter kun, når pc'en er ledig, og aldrig i profilens stilletid.
- **Vedligehold nu** på maskinsiden kører det samme med det samme. Du vælger, hvad der skal ske bagefter.
- **Sluk når færdig** (maskinsiden → Strøm, og under Opdateringer): pc'en slukker 2 minutter efter, at alt i køen er færdigt, fx en stor opdatering.
- **Strøm-menuen**: Tænd (Wake-on-LAN), Genstart, Sluk, Lås skærmen og Log af. Alt skal bekræftes.
- Genstart og sluk fra vedligeholdelse og "Sluk når færdig" sker **kun, hvis ingen har rørt pc'en i 15 minutter**.

### BIOS-opdatering

- Fanen **BIOS** under Opdateringer og sektionen **BIOS og firmware** på maskinsiden viser nuværende og nyeste BIOS (Lenovo), vigtige
  ændringer (fx nye Secure Boot-nøgler) og en backup af BIOS-indstillingerne, som kan sammenlignes og gendannes.
- En BIOS opdateres **kun, når du trykker** – aldrig af "Opdatér alt", vedligeholdelse eller planer. Windows: Lenovos eget program.
  Linux: kun hvis Lenovos pakke indeholder en rigtig UEFI-kapsel; ellers (fx ThinkCentre M720q, hvor pakken er en rå ROM) bruges
  **USB-stikket**: Secure Boot slås fra, mens der flashes, og til igen bagefter.
- Når en BIOS ligger klar til næste genstart, springer alle automatiske genstarter maskinen over. Du vælger selv genstarten.

### Alarmer du vil ignorere

Tryk **Ignorér** på en alarm, og vælg *kun på denne maskine* eller *på alle maskiner* (fx Defender, når den er slået fra med vilje).
Den giver så ingen alarm, besked, fradrag i helbredet eller rettelse, og den kommer ikke med i dashboard-status. Ignorerede alarmer står under
Helbred → *Ignorerede alarmer* med knappen **Vis igen**.

### Fejllog, Hændelser og live-log

Maskinens fane **Logs** viser fejl og advarsler fra systemloggen (Windows' hændelseslog, journald på Linux, macOS' unified log).
Ens hændelser samles i én række med antal og første/seneste gang; tryk på rækken for en dansk forklaring og *Hvad gør jeg*
(fx uventet nedlukning, blå skærm, hardwarefejl, diskfejl, mislykket Windows-opdatering eller OOM-killer på Linux).
Filtrér på niveau, kilde og tid, eller søg. Harmløs støj (fx DCOM 10016) skjules med **Skjul denne type** – *kun på denne maskine*
eller *på alle maskiner*; skjulte hændelser gemmes stadig, men giver ingen advarsler (**Vis skjulte** viser dem igen).
Agenten sender loggen højst hvert 10. minut (Mac hvert 30.), og serveren gemmer den i 30 dage. Det kræver agent 3.40.0 eller nyere.

Siden **Hændelser** (under Oversigt) samler de alvorlige hændelser fra alle maskiner. Helbredet advarer selv ved blå skærm/kernepanik,
hardwarefejl, diskfejl, gentagne uventede nedlukninger og gentagne OOM-lukninger.

**Live** (kun administrator) viser loggen, mens den skrives – praktisk under fejlsøgning eller en opdatering. Den går samme sikre vej
som terminalen (https, hjemmenet eller VPN), kan kun læse, skjuler adgangskoder og tokens, viser højst 20 linjer i sekundet,
og stopper af sig selv efter 30 minutter, eller når siden lukkes. Hver start og stop står i revisionsloggen.

### Diskhelbred, batteri, tidslinje og ændringer

Fanen **Helbred** viser nu også:

- **Tidslinje** – om maskinen var online eller offline de sidste 24 timer, 7 eller 30 dage, med markører for opstarter,
  opdateringer, alvorlige hændelser fra Logs og ændringer i programmer og drivere. Hold musen over (eller tryk på) en markør
  for detaljer. Gråt betyder, at Fleetdeck ikke førte tidslinje for maskinen endnu (den begynder ved første check-in efter 3.40.0).
- **Diske (SMART)** – pr. disk: NVMe/SSD/HDD, vurdering, resterende levetid, temperatur, timer i drift og dårlige sektorer.
  På Linux kræver det smartmontools (udvidelsen *Diskhelbred*); sovende diske på Unraid vækkes ikke.
  Helbredet advarer ved SMART-fejl, sektorer der begynder at stige, slid over 80 % og høj temperatur.
- **Batteri** (bærbare) – hvor meget batteriet kan rumme i forhold til som nyt, og antal opladninger.
  Under 60 % giver en advarsel; grænsen ændres under Indstillinger → *Batterialarm*.
- **Ændringer** – hvilke programmer og (på Windows) drivere der er installeret, opdateret eller fjernet og hvornår.
  Nyttigt, når noget pludselig holder op med at virke: se, hvad der ændrede sig lige før.

### Temperaturmåling (Windows)

Under Helbred → **Slå temperaturmåling til** lægges LibreHardwareMonitor fast i `C:\Program Files\Fleetdeck\LibreHardwareMonitor`
og køres skjult fra opstart (planlagt opgave *Fleetdeck Temperaturmaaling*). Agenten læser temperaturerne fra dens lokale webserver
(kun `127.0.0.1:8085`). Kør knappen igen, hvis målingerne udebliver, og genstart pc'en.

### På mobilen

Hele Fleetdeck kan bruges på telefonen: tabeller vises som kort, knapperne er store nok til en finger, inputfelter zoomer ikke,
og maskinsidens knapper samles i **Handling…** og **Mere**. Gem siden på hjemmeskærmen for at åbne den som en app.

### Status mens noget kører

Opdateringer, installationer, Windows Update, backup/genskab og scripts melder løbende tilbage, mens de kører: en bjælke med procent
(stribet, når procenten ikke kendes) og hvad maskinen laver lige nu, fx *Opdaterer pakker · Sætter op: firefox* eller
*Opdaterer Google Chrome (2/5)*. Det ses i panelet **I gang nu** øverst på Oversigt, i job-listerne og i jobbets detaljer, og det
opdateres med det samme (agenten sender højst hvert 4. sekund). Egne scripts kan selv sætte et trin med linjen
`::ws 30-60 Teksten` (fremdriften går så fra 30 til 60 %); linjen vises ikke i outputtet.

Et job, der kører, kan stoppes med knappen **Stop** under **Jobs**. Installationer kører altid lydløst: viser et installationsprogram
alligevel et vindue og venter på et klik, stopper agenten det og skriver det i jobbets output.

### Status til et dashboard

- **Status:** `GET <server>/api/kontrolrum/status` med headeren `Authorization: Bearer <nøgle>` (eller `X-Api-Key: <nøgle>`).
  Nøglen laves under **Indstillinger → Adgang for programmer → Ny API-nøgle** (kun administratorer) med rettigheden
  *Kun læse*. Der kan være én nøgle pr. program; hver kan slettes alene, og nøglen vises kun, når den laves (Fleetdeck gemmer
  kun dens SHA-256). Som standard virker den kun fra hjemmenettet/VPN, ikke gennem Cloudflare-tunnelen. Højst 60 kald i
  minuttet pr. nøgle; forkerte nøgler giver `401`, og efter 10 forkerte forsøg på 5 minutter giver IP'en `429` med `Retry-After`.
  Den gamle Kontrolrum-nøgle fra før 3.38.0 er flyttet over som nøglen "Kontrolrum" og virker uændret. Svaret: maskiner (i alt/online/offline/travle), jobs (kørende/i kø/fejlet 24 t),
  udrulninger, advarsler og `activity`: de jobs der kører eller står i kø. Alle tider er UTC (ISO 8601).
- **Sundhed:** `GET /api/health` uden login → `{"ok":true,"version":"…"}`.
- **Under en understi:** proxyen sender `X-Forwarded-Prefix: /din/sti`. Siden bruger kun relative adresser.
- **I en iframe:** kræver miljøvariablen `FRAME_ANCESTORS` med dit dashboards adresse (standard `'self'`).
  Udløbet session giver `401` (ingen omdirigering), og for mange login-forsøg giver `429`.
- **`?embed=1`** skjuler topbjælke og menu og viser kun en smal fanerække. Det huskes i fanen, og `?embed=0` slår det fra.

**Gode råd:**
- Reservér containerens IP i routerens DHCP (eller sørg for, at den ligger uden for DHCP-området), så ingen anden enhed får den.
- Brug en stærk root-adgangskode på Unraid, og fjern SSH-nøgler, du ikke længere bruger.
- Fleetdecks gendannelse bruger sin egen backup i `WinSetup\Backup`. Tag en ny med *Tag backup nu*.

---
# Windows Opsætning: frisk Windows, præcis som din pc er nu

**Sådan virker det:** Du starter fra en almindelig Windows-USB-nøgle. Alt andet ligger på Unraid
(`\\SERVER\PC-Reinstall\WinSetup`) og hentes derfra: programmer, drivere, baggrund, Windows-opsætning, dine filer og
programindstillinger. Programmerne hentes i nyeste version under installationen, så alt er frisk installeret.

```
USB-nøgle (Windows 11 + svarfil)  ──►  ny Windows (dansk, din konto, ingen spørgsmål undtagen disk)
                                          │  første login
                                          ▼
\\SERVER\PC-Reinstall\WinSetup  ──►  drivere ► programmer ► dine filer + indstillinger ► baggrund + opsætning ► genstart
```

---

## 1. Én gang: læg opsætningen på Unraid

1. I Unraid: **Shares → Add Share**. Navn: `PC-Reinstall`. Primary storage: cache (hurtigst). SMB: *Export: Yes*,
   *Security: Private*, og giv din Unraid-bruger *Read/Write*.
2. Kopiér **hele mappen `WinSetup`** over i `\\SERVER\PC-Reinstall\WinSetup` (så `Start-Opsaetning.cmd` ligger direkte i mappen).
3. Start **Fleetdeck** (webstyringen) som beskrevet i [afsnittet Fleetdeck](#fleetdeck-styring-af-alle-dine-windows-pcer) nedenfor.
   Det er valgfrit, men anbefales. Uden den bruger nyinstallationen programlisten fra `apps.json` som før.

Dine personlige filer ligger ikke i repoet: `apps.json` og `config.json` laves første gang ud fra `apps.example.json` og
`config.example.json`, og *Gem din pc* overskriver `apps.json`.

## 2. Gem din nuværende pc (gør det her nu, og igen før hver geninstallation)

Dobbeltklik på `\\SERVER\PC-Reinstall\WinSetup\Start-Opsaetning.cmd` og sig ja til administrator.

| Fane | Hvad du gør |
|---|---|
| **Baggrund og opsætning** | Klik **Hent baggrund og opsætning fra denne pc**. Lad fluebenet *Gem også drivere* være sat. |
| **Programmer** | Gennemgå listen. Fjern flueben ved det du ikke vil have. Søg og tilføj nye fra winget. Gem. |
| **Programmer** (filer) | Hent installationsfiler til dem med *Mangler fil*. Markér rækken → **Tilføj installationsfil…** → Gem. |
| **Filer og indstillinger** | Luk de programmer, hvis indstillinger skal gemmes (fx Thunderbird og VS Code). Klik **Tag backup nu**. (Første gang tager længst.) |

## 3. Lav USB-nøglen

1. Hent **Media Creation Tool** fra Microsoft (microsoft.com/software-download/windows11).
   Vælg **dansk**, **Windows 11**, **USB-flashdrev** (mindst 8 GB, alt på nøglen slettes).
2. Åbn Windows Opsætning → fanen **Installér** → **Gør USB-nøgle klar…** → vælg USB-drevet.
   Der lægges en svarfil og et startscript på nøglen. Gemte netkortdrivere kommer også med.
   `USB/autounattend.xml` er valgfri: findes den ikke, får nøglen `USB/autounattend.example.xml` med pc-navnet (fra `BackupName`
   i `config.json`) og den nuværende Windows-brugers navn udfyldt.

> Brug **ikke** Rufus' "tilpas Windows"-valg. De laver deres egen svarfil, som overskriver denne.

## 4. Før du sletter noget, tjek de her ting

- **Aktivering:** Indstillinger → System → Aktivering skal sige *aktiveret med en digital licens knyttet til din
  Microsoft-konto*. Så aktiverer den nye Windows sig selv.
- **Backup er frisk:** fanen *Filer og indstillinger* viser *Seneste backup: (i dag)*.
- **Spil** (Steam, Battle.net, Epic m.fl.) bliver ikke gemt, de er for store. Launcherne installeres, men
  spillene skal hentes igen. Log ind på launcherne efter installationen.
- **Logins** i browsere og de fleste programmer skal laves igen. Mail i Thunderbird og PuTTY-sessioner kommer med i backup.

## 5. Installér

1. Sæt USB-nøglen i. Tænd og tryk boot-menu-tasten (fx **F11** eller **F12**) → vælg USB-nøglen (UEFI).
2. Windows spørger **kun om disken**: vælg den disk, Windows skal på → slet partitionerne → vælg den tomme plads →
   *Næste*. Rør ikke USB-nøglen i listen.
3. Resten kører selv. Pc'en logger ind én gang automatisk, og vinduet **"Næsten færdig"** kommer frem:
   - vælg adgangskode til din konto
   - Unraid-share: `\\SERVER\PC-Reinstall\WinSetup` (står der allerede)
   - din Unraid-bruger og adgangskode
4. Klik **Start installation**. Et vindue viser hvert trin. Det tager typisk 30 til 60 minutter (store programmer og
   drivere tager længst). Til sidst spørger den om genstart: svar **J**.

Lukker du vinduet ved et uheld, ligger genvejen **Fortsæt Windows-opsætning** på skrivebordet.
Bagefter ligger genvejen **Windows Opsætning** på skrivebordet. Den åbner det samme vindue som i trin 2.

## 6. Senere: hold det opdateret

- **Nyt program?** Fanen Programmer → søg → *Tilføj valgte* → *Gem liste*. Fanen Installér → *Start*
  installerer det med det samme. Ved næste geninstallation kommer det automatisk med.
- **Ny baggrund eller ændret opsætning?** Klik *Hent baggrund og opsætning fra denne pc* igen.
  Eller *Vælg en anden baggrund…* for at lægge et billede ind direkte.
- **Backup:** klik *Tag backup nu* en gang imellem.

---

## 🛟 Hjælperen: retter problemer for dig

Hjælperen er bygget ind i Fleetdeck. Den forklarer hver alarm på dansk og kan rette den.

| Hvor | Hvad du kan |
|---|---|
| **Alarmer** (Helbred, Oversigt, maskinens Helbred-fane) | Hver alarm viser **Hvorfor** og **Hjælperen** plus en knap som fx *Slå Microsoft Defender til*. Status på rettelsen vises under alarmen |
| **Maskine → fanen Hjælper** | Problemer der kan rettes, en værktøjskasse og hjælperens seneste arbejde |
| **Jobs** | Fejlede jobs forklares (fx winget- og MSI-fejlkoder) med knapper: *Prøv igen*, *Prøv igen som bruger/system*, *Reparér winget* |
| **Profil → Hjælper: automatisk rettelse** | Vælg hvad hjælperen må rette selv. Pause 6 timer mellem forsøg og højst 3 forsøg |
| **Oversigt → Hjælperen (sidste 7 dage)** | Hvad er rettet, hvad fejlede, og hvad skete automatisk |

**Værktøjer:** Slå Defender til · Opdatér virusdefinitioner · Hurtig virusscanning · Slå firewall til · Windows Update ·
Opdatér alle programmer · Genstart med nedtælling · Ryd op på disken (dine filer og papirkurven røres ikke) · Analysér nedbrud
(blå skærme, strømtab, WHEA/overclock) · Reparér Windows (DISM + SFC) · Gennemgå hændelsesloggen · Nulstil netværk · Reparér winget ·
Prøv fejlede programmer igen · Tag backup nu · **Forbered geninstallation** · **Geninstallér fra netværket**.

**Sikkerhed:** Kun *sikre* værktøjer kan køre automatisk. Defender, firewall, Windows Update, genstart og netværk kræver et klik.
*Geninstallér fra netværket* kræver, at du skriver maskinens navn, **og** at der findes en backup fra det sidste døgn.

### Geninstallér en pc (den nemme måde)
1. Maskinen → **Hjælper** → **Forbered geninstallation**. Det gemmer opsætning, drivere og laver en frisk backup. Vent til alle tre jobs er OK.
2. **Hjælper** → **Geninstallér fra netværket** → skriv maskinens navn. Pc'en genstarter om 2 minutter og booter fra netværket.
3. Skriv dit Unraid-login på pc'en, og vælg disk. Resten kører af sig selv.

## Hvad bliver gemt og lagt tilbage

| Område | Indhold |
|---|---|
| Programmer | Fra winget, Microsoft Store og egne installationsfiler fra `Installers\` |
| Drivere | Alle tredjepartsdrivere (fx grafik, chipset, netkort) via `pnputil` |
| Baggrund | Din baggrund (også spænd over skærme), eventuelt også på låseskærmen |
| Windows-opsætning | Mørkt tema, accentfarve, Stifinder (filendelser, skjulte filer), proceslinje (placering, knapper, fastgjorte genveje), startmenu-fastgørelser, søgefelt, mus, tastatur, strømplan |
| Dine filer | Skrivebord, Dokumenter, Billeder, Videoer, Musik, Overførsler |
| Programindstillinger | Det, der står i `config.json`, fx Notepad++, VS Code, Thunderbird (mail og konti), FileZilla, Git, SSH-nøgler, Windows Terminal, PuTTY, 7-Zip |
| Sprog og konto | Dansk Windows og tastatur, tidszone København, pc-navn og lokal konto fra svarfilen |

Kommer **ikke** med: skærmopløsning og skalering, natlys, Wi-Fi, gemte adgangskoder i browsere og spil.

## Filer i sharen

| Fil eller mappe | Hvad |
|---|---|
| `Start-Opsaetning.cmd` | Åbner vinduet |
| `WinSetup.ps1` / `Run.ps1` / `Lib.ps1` | Vinduet, motoren og fælles funktioner |
| `apps.json` | Programlisten (redigeres i vinduet; laves fra `apps.example.json` første gang) |
| `config.json` | Hvilke mapper, programindstillinger og registry-nøgler der gemmes (laves fra `config.example.json`) |
| `USB\` | `Bootstrap.ps1` og evt. din egen `autounattend.xml` (ellers bruges `autounattend.example.xml`); kopieres til nøglen |
| `Fleetdeck\` | Fleetdeck-løsningen: server, agent, web, tests, Dockerfile og Unraid-skabelon |
| `agent.json`, `Agent\` | Skrives af Fleetdeck: serverens adresse, tilmeldingsnøgle og agenten til USB-nøglen |
| `Installers\` `Drivers\` `Settings\` `Backup\` `Logs\` | Indhold der bliver fyldt op efterhånden |

## Fejlfinding

- **"Kan ikke finde \\SERVER\PC-Reinstall\WinSetup\Run.ps1"**: Prøv `\\<ip>\PC-Reinstall\WinSetup` i stedet. Tjek også at kablet sidder
  i et netkort, Windows har driver til.
- **Et program fejler**: Se `Logs\` på serveren. Kør *Installér → Programmer* igen. Allerede installerede
  programmer springes over.
- **Windows ikke aktiveret**: Indstillinger → System → Aktivering → *Fejlfinding* → "Jeg har for nylig ændret
  hardware" → log ind med din Microsoft-konto.
- **Svarfilen bliver ikke brugt**: `autounattend.xml` skal ligge i roden af USB-nøglen, og sproget i Fleetdeck
  (Udrulning → Sprog, tastatur og tidszone) skal findes på Windows-mediet.


---

# Fleetdeck: styring af alle dine Windows-pc'er

Fleetdeck er en web-app i Docker på Unraid, der virker på både pc og mobil, og som kan føjes til hjemmeskærmen som en app. Den styrer alle dine Windows-pc'er, både din egen og børnenes. På hver pc kører **Fleetdeck Agent** som en rigtig Windows-tjeneste. Den har en åben live-forbindelse til serveren, så det du trykker på i appen, sker med det samme.

```
 Mobil / pc-browser ──►  Fleetdeck-server (Docker på serveren:8585, ASP.NET Core + React)
                              │   SignalR (live)           ▲
                              ▼                            │ HTTPS/HTTP + token
                     Fleetdeck Agent (Windows-tjeneste) på STUE-PC, børnenes pc'er …
                              │
                     /mnt/user/PC-Reinstall/WinSetup: installationsfiler, drivere, backup, agent til USB
```

**Teknologi:** C# **.NET 10 LTS** til server og agent, med fælles kontrakter mellem dem. Webdelen er **React 19 + TypeScript** med Mantine.
Data ligger i Entity Framework Core med SQLite og automatiske migrationer. Live-opdateringer kører over SignalR. Der er unittests (xUnit)
og central versionsstyring af pakker med sikkerhedsaudit.

| Område | Hvad du kan |
|---|---|
| **Oversigt** | Online, sunde maskiner, profiloverholdelse, ventende opdateringer, fejlede jobs, alarmer og backup-alder. Opdateres live |
| **Maskiner** | Søg og filtrér. Markér flere og kør en handling på dem alle: installér eller afinstallér programmer og spil, opdatér alt, Windows Update, script, besked, lås skærm, log af, genstart, sluk, Wake-on-LAN, nyinstallation, tildel profil eller fjern agenten |
| **Maskine** | Hardware, Windows, netkort, drev, profil, tags og noter. Faner med helbred, profiloverholdelse, software og Steam-spil, opdateringer og jobs |
| **Helbred** | CPU, RAM og disk med grafer over 6 timer, 24 timer og 7 dage. Temperaturer, SMART (slid, temperatur, læsefejl, driftstimer), antivirus og Defender, firewall, BitLocker, nedbrud og blå skærme, Windows Update og ventende genstart. Score 0–100, alarmer der kan kvitteres, og beskeder til Discord eller ntfy |
| **Pakker og spil** | winget (10.000+ programmer, søgbart), Microsoft Store, egne .exe/.msi, **Steam-spil** (app-id) og **launcher-links** (Epic, Battle.net) |
| **Profiler** | Pakker kan være *Påkrævet*, *Tilgængelig* eller *Afinstallér*. Profilen gennemtvinges automatisk. Planlagte programopdateringer, **Windows Update** og backup, plus hvad nyinstallationen skal gøre |
| **Jobs / Scripts** | Kø med output, varighed, stop, annullér og prøv igen. Gemte PowerShell-scripts kan køre som System, Bruger eller Bruger (admin) |
| **WinSetup / Log / Indstillinger** | Baggrund, drivere, installationsfiler, backup og logs. Revisionslog. Brugere med roller, alarmgrænser, webhook og tilmeldingsnøgle |

## A. Start serveren på Unraid

1. Kopiér `WinSetup` til sharen (trin 1 ovenfor), så `\\SERVER\PC-Reinstall\WinSetup\Fleetdeck` findes.
2. **Unraid → Terminal** (ikonet `>_`). Byg billedet (første gang tager det 5–10 minutter, fordi web og agent bygges):

```bash
docker build -t fleetdeck:latest /mnt/user/PC-Reinstall/WinSetup/Fleetdeck
```

3. Læg skabelonen ind, så containeren vises i Docker-fanen med ikon og WebUI-knap:

```bash
cp /mnt/user/PC-Reinstall/WinSetup/Fleetdeck/deploy/unraid/fleetdeck.xml /boot/config/plugins/dockerMan/templates-user/my-Fleetdeck.xml
```

4. **Docker → Add Container → Template: Fleetdeck → Apply.** Netværk er *br0* med egen IP (fx 192.168.1.29), så netværksboot og Wake-on-LAN virker uden at røre andre tjenester på Unraid.
5. Åbn **http://192.168.1.29:8585** på pc eller mobil. På telefonen kan du vælge *Føj til hjemmeskærm*. Første gang opretter du administrator-brugeren.
   Kataloget og profilen **Fra WinSetup** importeres automatisk fra `apps.json`.
6. **Indstillinger → WinSetup-share:** skriv din Unraid-bruger og adgangskode. Agenten bruger dem som SYSTEM til drivere.
7. **Indstillinger → Server:** adressen er fx `http://192.168.1.29:8585` (sat af skabelonen). Ændrer du den, flytter agenterne selv med.

**Opdatering senere:** i Fleetdeck under **Indstillinger → Generelt → Opdatering af Fleetdeck** (Søg efter opdatering → Opdatér nu, eller automatisk om natten) – se [docs/OPDATERING.md](docs/OPDATERING.md). Udviklerne kan stadig bruge *G. Udvikling* (`deploy/deploy.sh` lægger koden i `Fleetdeck/` på sharen og bygger billedet; kun de 3 nyeste versioner gemmes). Klik derefter **Docker → Fleetdeck → Force update**, og ryd det gamle billede med `docker image prune -f`.
Databasen ligger i `appdata`, og migrationer kører automatisk. **Agenterne på pc'erne opdaterer sig selv** til den nye version.

## B. Tilmeld maskiner

- **Ny pc fra USB-nøglen (det hele sker automatisk):**
  1. Kør *Gør USB-nøgle klar…* i Windows Opsætning, efter at serveren er startet. Så kommer agenten (`Fleetdeck.exe`) og
     `SetupComplete.cmd` med på nøglen.
  2. Lige efter Windows-installationen, før nogen logger ind, installerer `SetupComplete` agenten som Windows-tjeneste.
     Den tilmelder sig serveren, så snart der er netværk.
  3. Serveren starter **nyinstallations-sekvensen**: drivere → profilens programmer og spil → dine filer → baggrund og opsætning →
     Windows Update → genstart. Jobs, der skal køre i din session (fx Spotify, Steam-spil, gendannelse), venter, til du er logget ind
     og har skrevet Unraid-login i vinduet "Næsten færdig". Rækkefølgen holdes, så genstarten kommer til sidst.
  4. Du følger med i appen eller i vinduet "Fleetdeck – nyinstallation" på pc'en.
- **En pc der kører nu** (din egen eller børnenes): **Maskiner → + Tilføj maskine** → kopiér kommandoen → på pc'en: højreklik Start →
  *Terminal (administrator)* → indsæt → Enter.
  - Windows-kommandoen: `irm "<server>/install.ps1?key=<nøgle>" | iex`. Skriv `$env:FLEETDECK_PROVISION = '1'` først,
    hvis nyinstallations-sekvensen også skal køre.
  - Eller dobbeltklik på `Fleetdeck.exe`: et vindue spørger om serverens adresse (eller tryk **Find automatisk**, som finder
    serveren på det lokale net via UDP 8586) og tilmeldingsnøglen. Du kan også indsætte hele kommandoen fra *+ Tilføj maskine*.
  - Linux (x64, ARM64 eller 32-bit ARM som Raspberry Pi, med systemd), i en almindelig terminal: `curl -fsSL "<server>/install.sh?key=<nøgle>" | sudo bash`,
    eller `./fleetdeck-agent setup` (små vinduer med zenity/kdialog, ellers spørgsmål i terminalen).
  - Kommandoen kan køres igen på samme pc uden at skade; maskinen genkendes på navnet. Trin for trin for begyndere og
    fejlfinding: [KOM-I-GANG.md](KOM-I-GANG.md).

- **En Mac – med appen:** *Tilføj maskine* → *Mac* → **Hent Fleetdeck.dmg** (Apple Silicon eller Intel; også på `<server>/hent`).
  Åbn filen, træk **Fleetdeck** over i **Programmer**, og åbn den. Første gang siger macOS, at appen ikke kan kontrolleres (den er kun
  ad hoc-signeret, ikke købt-signeret hos Apple):
  - macOS 15 (Sequoia) og nyere: tryk *Udført* → **Systemindstillinger → Anonymitet og sikkerhed** → rul ned → **Åbn alligevel** → adgangskode → *Åbn alligevel*.
  - macOS 14 og ældre: højreklik (ctrl-klik) på Fleetdeck → **Åbn** → **Åbn**.

  Vinduet er som på Windows: serverens adresse (eller *Find automatisk*) og nøglen (eller hele kommandoen) → **Test forbindelse** → vælg
  profil → **Installér og forbind** (Mac'en beder om adgangskoden; appen henter agenten fra serveren og tjekker dens signatur). Bagefter viser
  vinduet, hvordan du giver **Fuld diskadgang** (knappen åbner *Anonymitet og sikkerhed → Fuld diskadgang*; tryk +, ⌘⇧G,
  `/usr/local/fleetdeck/fleetdeck-agent`, slå til) og venter, til adgangen ses – tjenesten genstartes selv, om nødvendigt med adgangskoden.
  Fleetdeck ligger derefter i **menulinjen** (alle brugere, ved login): status (forbindelse, profil, opdateringer, alarmer, helbred),
  *Synkronisér nu*, *Opdatér alt nu*, *Sæt på pause i 1 time* (kræver administrator), *Udsæt genstart*, *Åbn Fleetdeck*, *Skift server*,
  *Om / Fejlfinding* (log, genstart af agenten, kopiér fejlfindingsinfo) og *Fuld diskadgang mangler – ret det*. Afinstallér: *Skift server…* →
  **Afinstallér Fleetdeck…**. På Apple silicon skal ejeren selv bekræfte macOS-opdateringer i Softwareopdatering (Fleetdeck henter dem og åbner vinduet).
- **En Mac – med Terminal:** kommandoen fra fanen *Mac* (samme `curl … | sudo bash` som Linux; den vælger selv Apple Silicon eller Intel).
  Installér derefter udvidelsen *Homebrew* (og evt. *App Store*) på maskinens side, så Fleetdeck kan installere programmer.
- **Send et link:** *Tilføj maskine* har links til downloadsiden `<server>/hent` (uden login) – med eller uden nøgle.

### Anmod om adgang (uden nøgle)

En maskine, der har hentet agenten og fundet serveren (*Find automatisk*), men ingen nøgle har, kan bede dig om lov:

- **Windows:** i opsætningsvinduet → **Har du ingen nøgle? Anmod om adgang** (navn og en kort besked). Eller i PowerShell som administrator:
  `irm "<server>/install.ps1" | iex` (uden `?key=`; `$env:FLEETDECK_MESSAGE = 'Annas nye bærbare'` først giver en besked med).
- **Mac:** i Fleetdeck-appen → **Har du ingen nøgle? Anmod om adgang**. **Linux:** `./fleetdeck-agent setup` og lad nøglefeltet stå tomt,
  eller `curl -fsSL "<server>/install.sh" | sudo bash` (evt. `WS_MESSAGE=…`). `sudo fleetdeck-agent status` viser, hvordan det går.
- Maskinen viser en **kode** (fx `7K4-Q2M`): i vinduet, i ikonet ved uret/menulinjen ("Venter på godkendelse – kode 7K4-Q2M") og i terminalen.
- Du får en notifikation (Home Assistant/webhook), og anmodningen står øverst på **Maskiner** med et rødt tal i menuen: navn, platform,
  styresystem, model, hvem der beder, besked, IP og koden. **Spørg brugeren om koden**, og tryk **Godkend…**: vælg profil (eller *Ingen profil*),
  ret evt. navnet, og sæt flueben i *Koden … er bekræftet med brugeren*. Inden for et halvt minut tilmelder maskinen sig selv.
- **Afvis…** (med en begrundelse, som brugeren ser) eller **Send nøgle i stedet…**, som laver en almindelig engangsnøgle og et link
  (`<server>/hent#k=…`), du selv sender. Nøglen vises kun én gang.

Sikkerhed:
- Indstillinger → *Agent-sikkerhed* → *Tilmelding*: **Fra**, **Kun hjemmenet og VPN** (standard på nye installationer: private adresser,
  100.64/10 og socket-filen på Unraid-værten, aldrig gennem Cloudflare) eller **Overalt**; højst antal åbne anmodninger (10) og hvor mange
  timer de gælder (24). **Opdaterer du en server, der allerede har maskiner, er funktionen slået fra**, til du selv slår den til.
- Højst 5 nye anmodninger i timen pr. adresse (IPv6 pr. /64) og 2 åbne pr. IP-adresse. Lofterne tjekkes, før en ældre anmodning erstattes.
  Klienten ser hverken profiler, maskiner eller nøgler, før du har godkendt. Er funktionen slået fra (eller kommer kaldet udefra ved
  *Kun hjemmenet*), afvises også maskinernes forespørgsler med det samme.
- Notifikation kun for nye anmodninger – ikke når en maskine beder igen (*Anmod igen* lukker dens egen tidligere anmodning) – og højst 10 i
  timen; derefter én samlet besked. Kortet viser, hvornår maskinen sidst spurgte, og **Maskinen svarer ikke**, når den ikke har spurgt i 3 minutter.
- Maskinen laver en hemmelighed og et nøglepar. Først sender den kun et hash af dem; serveren svarer med et tilfældigt tal, og først derefter
  sendes hemmeligheden (inden for et minut). **Koden** (6 tegn, fx `7K4-Q2M`) regnes ud fra hemmeligheden, serverens tal, TLS-fingeraftrykket
  og maskinens offentlige nøgle. En mellemmand kan derfor ikke prøve sig frem til en kode, der passer, og med et andet certifikat eller en anden
  nøgle får han en anden kode på maskinen end i Fleetdeck. Fingeraftrykket er tjekket, når serveren kender sit eget (egen https eller
  *TLS-fingeraftryk* udfyldt); ellers står det ved anmodningen. Over almindelig http står der **Ukrypteret forbindelse**.
- Godkendelsen giver en engangskode, der er **krypteret til maskinens nøgle** (en, der lytter med, kan ikke bruge den) og kun virker sammen
  med hemmeligheden og kun én gang. Den kan **aldrig overtage en eksisterende maskine** (heller ikke en forberedt maskine eller en, der er ved at
  blive geninstalleret): findes navnet, venter maskinen på godkendelse som erstatning eller ny, som før – og **Godkend som ny** giver den den
  profil, du valgte, og standardudvidelserne. Gik svaret på tilmeldingen tabt (timeout, genstart), og har maskinen aldrig tjekket ind, får
  den en ny kode, der kun må tage netop den maskine.
- Et selvsigneret certifikat fastlåses ved tilmeldingen; et certifikat, systemet stoler på, kontrolleres almindeligt (det må gerne skifte).
- En maskine, der allerede er tilmeldt, beder kun om adgang igen med `--force` (Windows-opsætningen spørger først; `$env:FLEETDECK_FORCE = '1'`
  eller `WS_FORCE=1` med scriptet). Kan maskinen ikke nå serveren i flere forsøg, viser ikonet/menulinjen/`status` det.
- Revisionsloggen får `access.request`, `access.approve`, `access.reject`, `access.key_sent`, `access.enrolled`, `access.reissue` og
  `access.bad_secret` (højst én pr. adresse i timen) – aldrig hemmeligheden eller nøglen.

### Mac: Hold vågen på strøm (også med lukket låg)

Til en bærbar Mac, der skal kunne bruges af Fleetdeck (fx som byggemaskine), når den står til opladning – også med lukket låg.

- **Slå til:** på maskinens side under **Strøm og dvale** (kun administrator; ændringen logges), eller på Mac'en i menulinjen
  (*Hold vågen på strøm (også med lukket låg)* med flueben) og i Fleetdeck-appens statusvindue. Sidste ændring vinder; kommer den fra
  Mac'en, står der *Ændret på Mac'en* og tidspunktet.
- **Tilstand:** *Strøm · holdes vågen*, *Batteri · sover normalt*, *Fra* – eller *Sat af brugeren*, hvis dvale allerede var slået fra med
  `pmset` på Mac'en, før Fleetdeck rørte den (så lader Fleetdeck den være).
- **Sådan virker det:** agenten ser strømkilden hvert 5. sekund. På strøm sætter den `pmset -a disablesleep 1` og skriver en markørfil
  (`/etc/fleetdeck/keepawake.marker`); på batteri, eller når indstillingen slås fra, straks `disablesleep 0`. Den ændrer kun det, den selv har sat.
- **Sikkerhed:** på batteri sover Mac'en altid normalt. En lille vagt (`com.fleetdeck.keepawake`, hvert 30. sekund) slår dvale til igen,
  hvis Mac'en kører på batteri, og markøren findes – også hvis agenten er gået ned. Stoppes agenten, eller afinstalleres Fleetdeck,
  slås dvale til igen. Kan serveren ikke nås, bruges den sidst kendte indstilling (stadig kun på strøm).
- **Tjek på Mac'en:** `pmset -g | grep -i sleep` (SleepDisabled 1 på strøm, 0 på batteri), `pmset -g ps` (strømkilde) og
  `sudo launchctl print system/com.fleetdeck.keepawake` (vagten).

## C. Push opdateringer, programmer og spil

| Hvad | Hvor |
|---|---|
| Opdatér alle programmer nu | Maskine → **Opdatér alt**, eller markér flere maskiner → Handling → *Opdatér alle programmer* |
| Windows Update nu | Handling → *Installér Windows-opdateringer* (eventuelt også drivere) |
| Automatisk hver nat | Profil → *Programopdateringer* og *Windows Update* → Dagligt kl. 03:00, eventuelt med genstart når det er nødvendigt |
| Nyt program til alle | Pakker og spil → *Find i winget* → Tilføj → Profil → *+ Tilføj* som *Påkrævet* → Gem. Installeres automatisk overalt |
| Steam-spil | Pakker og spil → **Tilføj Steam-spil** → app-id (står i adressen store.steampowered.com/app/**730**) → *Installér på…* eller sæt det i en profil. Steam skal være logget ind på pc'en |

## D. Børnenes pc'er

1. **Profiler → + Ny profil**, fx "Børne-pc". Sæt programmer og spil, de skal have, som *Påkrævet*, og det, de ikke må have, som
   *Afinstallér*. Slå *Gennemtving* til, og sæt opdateringer om natten.
2. Tilmeld hver pc og tildel profilen. Giv den et **tag** med barnets navn.
3. Fra mobilen kan du se helbred, installere spil, sende en besked, **låse skærmen**, **logge af**, genstarte eller slukke, og tænde pc'en med Wake-on-LAN.

## E. Sikkerhed

- Login med sikker cookie (HttpOnly, SameSite=Strict). Adgangskoder gemmes som PBKDF2-SHA256 med 210.000 iterationer.
  Der er begrænsning på loginforsøg, og alle sessioner lukkes, når en adgangskode skiftes. Roller: *Administrator* og *Læser*. Alt bliver logget i revisionsloggen.
- Hver agent har sit eget token. Serveren gemmer kun et SHA-256-hash af det, og på pc'en er tokenet krypteret med DPAPI.
  Agentens mappe kan kun læses af SYSTEM og administratorer. Selvopdatering verificeres med SHA-256, før den nye version tages i brug.
- Websiden kører over http på dit lokale net. Skal den bruges uden for huset, så sæt den bag en reverse proxy med https (fx Nginx
  Proxy Manager eller SWAG på Unraid) eller brug **Tailscale**. **Åbn ikke port 8585 direkte mod internettet.**

## F. Fejlfinding

| Problem | Løsning |
|---|---|
| Maskinen vises ikke | Kør tilmeldingskommandoen igen som administrator. Tjek at serverens adresse (fx `http://192.168.1.29:8585`) kan åbnes fra pc'en. På en ny pc: se `C:\Windows\Setup\Scripts\Fleetdeck-install.log` |
| Job står i "Venter på bruger" | Jobbet skal køre i brugerens session (Spotify, Discord, VS Code, Steam, backup). Det kører, når nogen logger ind |
| Et job hænger | Tryk **Stop** under Jobs. Ventede en installation på et klik, står det i jobbets output |
| "Kan ikke nå sharen" | Udfyld Unraid-bruger og adgangskode under Indstillinger |
| Ingen temperaturer | Start LibreHardwareMonitor (står på listen) med *Options → Remote Web/WMI* |
| Agentens log | `C:\ProgramData\Fleetdeck\agent.log` (kræver administrator) |
| Tjenesten | `services.msc` → **Fleetdeck Agent**. Genstarter selv ved fejl |
| Fjern agenten | Maskine → Handling → *Fjern agenten*, eller som administrator: `"C:\Program Files\Fleetdeck\Fleetdeck.exe" uninstall` |

## G. Udvikling (hvis du vil bygge videre)

```
fleetdeck/
  src/Fleetdeck.Shared          fælles kontrakter (server ↔ agenter)
  src/Fleetdeck.Server          ASP.NET Core: API, SignalR, EF Core, baggrundsjobs, helbredsregler
  src/Fleetdeck.Agent.Windows   Fleetdeck.exe: Windows-tjeneste, installationsvindue og ikon i proceslinjen
  src/Fleetdeck.Agent.Unix      fleetdeck-agent: Linux (x64/ARM), Raspberry Pi, Mac og Unraid
  src/Fleetdeck.MacApp          Fleetdeck.app: menulinjen og installationsvinduet på Mac (Avalonia, osx-arm64/osx-x64)
  src/Fleetdeck.MacPack         byggeværktøj: .app-pakke, ad hoc-signatur, HFS+ og DMG i ren .NET (kører på Linux)
  tests/Fleetdeck.Tests         xUnit
  web/                          React + TypeScript + Vite + Mantine (stilarterne Command og Aurora, lyst/mørkt)
  deploy/docker/                start.sh og dnsmasq.conf (netværksboot i samme container)
  deploy/unraid/fleetdeck.xml   Unraid-skabelon
  deploy/deploy.sh              lægger koden på sharen og bygger billedet (--provision: også Windows Opsætning, USB, PXE; --source-only: kun koden, til opdatering fra Fleetdeck)
  provision/windows/            Windows Opsætning (WinSetup.ps1, Lib.ps1, Run.ps1, eksempelfiler)
  provision/usb/                USB-svarfiler og Bootstrap.ps1
  provision/pxe/gui/WinSetupGui.c   det grafiske installationsprogram i netværksbootet (C/Win32, byg med provision/pxe/gui/build.sh)
  provision/pxe/menu.cmd            tekstmenu som nødløsning (hentes fra sharen ved hver boot)
  provision/pxe/build/pxe-build.ps1 bygger boot.wim + BCD (kør som administrator på Windows med Windows 11-ISO'ens boot.wim)
```

**Kildekoden ligger på GitHub.** Store og personlige filer (backup, drivere, installationsfiler, Windows-/Ubuntu-filer til PXE,
`apps.json`, `config.json`, `autounattend.xml`, agent.json med tilmeldingsnøglen) er udeladt via `.gitignore`.

**Deploy:** `deploy/deploy.sh` læser `HOST` og `SHARE` fra filen `site.env`, fx:

```
HOST=root@192.168.1.10
SHARE=/mnt/user/PC-Reinstall/WinSetup
```

**Netværksboot:** `provision/pxe/build/pxe-build.ps1 -Server 192.168.1.10 [-Share PC-Reinstall] [-ServerName "Min server"]` (serverens adresse skal angives).

Lokalt: `dotnet test`, `cd web && npm ci && npm run dev` (proxy til serveren på 8585). Ny databaseændring: `dotnet ef migrations add <Navn> -p src/Fleetdeck.Server -o Data/Migrations`.
