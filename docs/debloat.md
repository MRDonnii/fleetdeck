# Debloat og privatliv (Windows)

Fleetdeck kan fjerne reklamer, forslag, telemetri og medfølgende apps fra Windows 10/11, og det kan fortryde det igen.
Det samme sted styrer strømprofil, brugervenlighed, opdateringspolitik og valgfrie Windows-funktioner.
Kun administratorer har adgang. Hver handling bliver et job i jobhistorikken og en linje i revisionsloggen.

## Sådan virker det

- **Kataloget** ligger i `src/Fleetdeck.Server/Services/DebloatCatalog.cs`. Det har 101 punkter i 13 kategorier.
  - Hvert punkt har et id, en dansk titel og forklaring, en kategori og en risiko (sikker, moderat eller avanceret).
  - Teksterne følger knapperne: titlen siger, hvad *Anvend* gør ("Slå reklame-id fra", "Vis filtypenavne", "Fjern Clipchamp"), og forklaringen beskriver det samme.
  - Tilstanden skrives med punktets egne ord: `Subject` + `On`/`Off` ("Reklame-id: fra", "Filtypenavne: vises", "Clipchamp: fjernet"). Uden egne ord bruges kategoriens. De samme ord står i jobbets output.
  - `DebloatCatalog.UndoText` siger, hvad *Fortryd* gør, ud fra det, punktet ændrer (strømplan, apps fra Store, funktioner, Wake-on-LAN …). `Undo` kan give punktet sin egen tekst. `NeedsLogoff` markerer punkter, der først ses efter log af og på.
  - Hvert punkt hører til en eller flere standardprofiler og har en Windows-build eller -udgave, det gælder for.
  - Et punkt består af registreringsværdier, tjenester, planlagte opgaver, AppX-pakker, winget-programmer, strømindstillinger og/eller Windows-funktioner.
- **Profiler**:
  - *Let* (14 punkter): reklamer, forslag og reklame-id.
  - *Anbefalet* (54 punkter): Let plus telemetri på laveste niveau, Copilot/Recall, websøgning, widgets, de typiske medfølgende apps, synlige filtypenavne, "Afslut opgave", hurtig start fra og Wake-on-LAN.
  - *Maks* (71 punkter): Anbefalet plus de avancerede punkter.
  - OneDrive, strømplaner, opdateringspolitik, netværk og Windows-funktioner er aldrig med i en profil. Hver maskine kan desuden vælge punkter til og fra.
- **Profilversioner**: Profilerne har en version (`DebloatProfiles.Current`), og hver maskine husker den version, den valgte (`MachineDebloat.ProfileVersion`).
  - Maskiner, der valgte en profil før version 2, beholder præcis det indhold, de sagde ja til, også ved "Anvend" og håndhævelse.
  - Siden viser, hvilke punkter der er kommet til. De kommer først med, når nogen trykker "Tag dem med i valget" eller vælger en profil igen.
- **Motoren** ligger i `Services/DebloatEngine.ps1`.
  - Den køres som et almindeligt script-job som SYSTEM i Windows PowerShell 5.1, så agenten behøver ingen opdatering.
  - Serveren lægger en plan (JSON) foran scriptet. Motoren skriver en maskinlæsbar rapport som sidste linje (`##FLEETDECK-DEBLOAT##`). Serveren læser rapporten og fjerner den fra jobbets output.
- **Brugerprofiler**: HKCU-værdier bliver sat for alle eksisterende profiler og for standardprofilen (`C:\Users\Default`), så nye brugere også får dem.
  - Profiler for brugere, der ikke er logget ind, indlæses med `reg load` og lukkes igen med det samme. Det sker i en `finally`-blok, så de også lukkes ved fejl.
  - Et tjek (detect) indlæser aldrig andres profiler. Kun indloggede brugere og standardprofilen bliver læst.
- **Før-værdier**: Før hver ændring bliver den gamle værdi gemt på serveren (`MachineDebloat.BeforeJson`).
  - Værdien gemmes pr. profil for registrering, starttypen gemmes for tjenester, og det gemmes om en opgave var slået til.
  - Kun det *første* billede gemmes. En genanvendelse efter en Windows-opdatering overskriver det aldrig.
- **Fortryd**:
  - En værdi sættes kun tilbage, hvis den stadig er Fleetdecks. Har nogen ændret den siden, får deres valg lov at blive.
  - Fjernede apps installeres igen fra Microsoft Store med deres Store-id. Det sker som almindelige installationsjobs i brugerens session.
  - Apps uden Store-id (udgåede apps) er markeret som "kan ikke installeres igen" i kataloget og i brugerfladen.
- **Gendannelsespunkt**: Motoren prøver at oprette et gendannelsespunkt, før den ændrer noget.
  - Er systemgendannelse slået fra, står det i rapporten. Fleetdeck slår ikke selv systemgendannelse til.
- **Hold håndhævet**: Maskinen bliver tjekket én gang i døgnet, og med det samme når agenten melder en ny Windows-build.
  - Er et anvendt punkt slået til igen, kommer der en alarm (`debloat_drift`).
  - I stedet for en alarm kan punktet anvendes igen, højst én gang hvert 20. time. Hjælper det ikke, kommer alarmen alligevel.

## Strøm og ydelse

- **Strømprofil**: Balanceret, Høj ydelse eller Ultimativ ydelse. Vælgeren står øverst på maskinens fane og i maskinens Strøm-menu og viser den plan, pc'en kører med nu, også en plan fra producenten.
  - Ultimativ ydelse er en skjult skabelon (`e9a42b02-d5df-448d-aa00-03f14749eb61`). Den kopieres til et fast GUID, når den vælges, så Fleetdeck kan kende sin egen kopi. Kopien slettes igen, når den ikke længere er i brug.
  - Windows' egne planer og producentens planer bliver aldrig slettet.
  - Fortryd sætter den plan tilbage, pc'en havde, før Fleetdeck første gang skiftede, også efter flere skift.
- **Wake-on-LAN** slås kun til, aldrig fra. Fortryd lader det stå, fordi Fleetdeck bruger det til at vække pc'en. Netkortet genstartes ikke undervejs.
- **Hurtig start fra** er med i Anbefalet, fordi hurtig start giver problemer med Wake-on-LAN fra slukket tilstand og med dual-boot.
- Tidsgrænser, USB- og PCIe-strømbesparelse gælder den aktive strømplan.

## Opdateringspolitik

- **Standard**: Windows' egne indstillinger.
- **Kun sikkerhed**: udsætter nye Windows-versioner i 365 dage og månedlige rettelser i 4 dage med de dokumenterede politikker `DeferFeatureUpdates` og `DeferQualityUpdates`. Virker på Pro og Enterprise.
- Fleetdecks opdateringsjobs søger og installerer det, Windows tilbyder, så de virker i begge tilstande. I *Kun sikkerhed* kommer rettelserne blot 4 dage senere.
- At slå Windows Update fra tilbydes ikke, og det er testet, at kataloget ikke kan gøre det.

## Windows-funktioner og reparationer

- WSL, Hyper-V, Windows Sandbox, .NET 3.5, NFS-klient og ældre mediekomponenter installeres med `Enable-WindowsOptionalFeature` og vises med "kræver genstart". Fortryd fjerner kun det, Fleetdeck selv slog til. Fleetdeck genstarter ikke selv pc'en.
- Hjælperen har fået **Nulstil Windows Update**, **Geninstallér winget** og **Grundig diskoprydning**. SFC/DISM, nulstilling af netværk og oprydning af midlertidige filer fandtes i forvejen. Reparationerne kører kun på Windows.

## Det er med vilje ikke taget med fra winutil

| winutil | Hvorfor ikke |
|---|---|
| Tjenester sat til manuel | Giver næsten ingen gevinst på nyere Windows, kan give fejl, der er svære at finde, og store opdateringer sætter starttyperne tilbage, så det ville give falske alarmer. |
| Windows Update slået fra | Fleetdeck styrer selv opdateringer, og en pc uden sikkerhedsrettelser er ikke et rimeligt valg. |
| `AUOptions` og drivere udelukket fra Windows Update | Ændrer, hvordan opdateringer installeres, ud over det at udsætte dem. |
| IPv6 slået fra, Teredo via `DisabledComponents` | Kan ødelægge netværket. Teredo slås i stedet fra med Microsofts politik, som ikke rører IPv4-præferencen. |
| Delivery Optimization, Store-søgning og Reserved Storage | Rører Windows Update eller Microsoft Store. |
| Fjernelse af Edge, BitLocker slået fra, Adobe-blokering i hosts-filen | Bryder grundreglerne. |
| Windows Search-indeksering, svchost-opdeling, S3-søvn og skjulte funktionsflag | Risikoen er større end gevinsten, og flere af dem kan ikke fortrydes pålideligt. |
| `DISM /ResetBase` | Installerede opdateringer kan ikke afinstalleres bagefter. |
| Programinstallation (`applications.json`) | Fleetdeck har sit eget programkatalog. |

## Grundregler (håndhævet af tests i `DebloatTests.cs`)

- Intet slår Windows Update, Defender, aktivering, Microsoft Store eller netværket fra. Der bruges ingen hosts-fil og ingen firewall-blokering.
  - De eneste undtagelser er navngivet i testene: udsættelse af opdateringer, foretræk IPv4 (kun én bit lægges til, IPv6 forbliver slået til) og Teredo-politikken.
- En app, der allerede er væk fra Windows' standardbillede (fil eller sti ikke fundet, `0x80070002`/`0x80070003`), tæller som udført og ikke som en fejl.
- Beskyttede pakker kan aldrig fjernes, heller ikke via jokertegn. Motoren springer desuden rammeværker og ikke-fjernbare pakker over.
  - Beskyttet: Store, Lommeregner, Fotos, Notesblok, Terminal, Paint, Klippeværktøj, App Installer/winget, VCLibs, UI.Xaml, .NET, WindowsAppRuntime, codecs, Edge, Quick Assist m.fl.
- Dokumenterede gruppepolitik-nøgler (`HKLM\SOFTWARE\Policies\…`) bruges, hvor de findes.
  - `AllowTelemetry` sættes til 1 ("Påkrævet"). Det er det laveste niveau, Home og Pro overholder, og det påvirker ikke Windows Update.
- Kendte begrænsninger står i punkternes bemærkninger:
  - `DisableWindowsConsumerFeatures` virker kun fuldt på Enterprise og Education.
  - `TurnOffWindowsCopilot` gør ikke meget på nye builds. Der er det appen, der skal fjernes.
  - Appraiser-opgaverne kan forsinke tilbud om nye Windows-versioner.

## Brugerflade (kun administratorer)

- **Maskinfanen "Debloat & privatliv"** (kun Windows-pc'er) har:
  - strømprofilen øverst;
  - niveauvælger og tal for anvendt / slået til igen / mangler;
  - "Hold håndhævet" med "Giv besked" (standard) eller "Anvend igen";
  - et kort pr. kategori med en kontakt pr. punkt.
- Hvert punkt kan foldes ud og viser:
  - hvad der ændres (registrering, tjenester, opgaver, apps) og før-værdierne;
  - status pr. brugerprofil;
  - knapper til at anvende eller fortryde netop dét punkt.
- Før "Anvend" og "Fortryd" vises en opsummering: punkter pr. kategori, apps der fjernes eller hentes igen, og advarsler (fx apps der ikke kan hentes igen).
- Kørende debloat-jobs vises med fremdrift, og resultatet vises pr. punkt bagefter.
- **Siden "Debloat & privatliv"** (under Programmer og opdateringer) har:
  - en flådeoversigt med alle Windows-pc'er og tilstanden pr. kategori;
  - "Tjek" og "Anvend" for flere pc'er på én gang;
  - en guide i tre trin: niveau → tilpas → anvend.

## API (kræver rollen admin)

| Metode | Sti | |
|---|---|---|
| GET | `/api/debloat/catalog` | Katalog, kategorier og profiler |
| GET | `/api/debloat/fleet` | Alle Windows-maskiner med valg og tilstand |
| GET/PUT | `/api/debloat/machines/{id}` | Maskinens valg (profil, til/fravalg, håndhævelse) og tilstand |
| POST | `/api/debloat/machines/{id}/scan\|apply\|undo` | Body `{ "items": [...] }` (valgfri) |
| POST | `/api/debloat/machines/{id}/powerplan` | `{ "plan": "balanced"\|"high"\|"ultimate" }`: vælg og anvend strømplanen |
| POST | `/api/debloat/bulk` | `{ "machineIds": [...], "action": "scan"\|"apply" }` |

## Kilder og tak

Registreringsnøglerne, app-listerne, strømplanerne og Windows-funktionerne er sammenholdt med to open source-projekter, begge under MIT-licens. Fleetdeck kopierer ikke deres kode.

- [Raphire/Win11Debloat](https://github.com/Raphire/Win11Debloat): `Regfiles/*.reg` og `Config/Apps.json`
- [ChrisTitusTech/winutil](https://github.com/ChrisTitusTech/winutil): `config/tweaks.json`, `config/feature.json`, `config/appx` og reparationerne i `functions/`

Microsoft Store-id'erne er slået op i Microsoft Store-kataloget.
