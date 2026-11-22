# Terminal i Fleetdeck: design og teknisk plan

Status: bygget på grenen `terminal` (forslag A), ikke udrullet og ikke afprøvet på rigtige maskiner. Skitser: `terminal-mockups/` (kilde) og `Fleetdeck/skitser/terminal-{A,B,C}-*.png`.

## 1. Mål

Én liste med alle maskiner og enheder, hvor ét tryk åbner en terminal, uden at man skal huske adgangskoder, og med gemte kommandoer til det daglige.

## 2. Designforslag

| Forslag | Idé | Styrke | Svaghed |
|---|---|---|---|
| **A: Arbejdsbord** | Værtsliste, faner og kommandoer side om side | Alt er ét tryk væk, og det passer til mange åbne sessioner | Kræver plads, så iPhone får sin egen visning |
| **B: Startplade** | Store kort først, sessioner i fuld skærm | Let at gå til og god på telefon | Mange skift mellem liste og session |
| **C: Dok** | Tabel med terminalen i en skuffe nederst | Ligner resten af Fleetdeck | Lille terminal, dårlig på iPad og telefon |

**Valg: A.** Telefonen bruger samme indhold som en liste med faner og tastaturrække.

## 3. Forbindelsestyper

| | Via agenten | Direkte SSH |
|---|---|---|
| Til | Tilmeldte Windows-, Linux- og Mac-maskiner | Router, NAS, switch og andre uden agent |
| Login | Intet: agenten starter skallen | Nøgle eller adgangskode fra nøgleboksen |
| Netværk | Agenten forbinder udad, ingen åben port | Serveren forbinder til værtens SSH-port |
| Tillid | Signeret billet i check-in-svaret | Værtsnøgle fastlåst ved første forbindelse |

**Anbefaling:** agenten, hvor der er en, ellers direkte SSH.

Unraid-værten kræver særlig opmærksomhed. Containeren på `br0` (macvlan) kan ikke nå sin egen vært, så direkte SSH virker ikke dertil. Værtens agent taler gennem `fleetdeck.sock` og har ingen live-forbindelse (`Program.cs` i Unix-agenten), så den får først billetten ved næste check-in, og åbningen kan tage op til et poll-interval.

## 4. Sikkerhedsbeslutninger

| Emne | Beslutning |
|---|---|
| Adgang | Kun administratorer |
| Oplåsning | Terminal og nøgleboks kræver en kode fra autentificerings-appen. Brugere uden totrinsbekræftelse får ikke adgang |
| Fornyet bekræftelse | Efter 15 minutter uden brug skal adgangskode eller kode gives, før et gemt login bruges |
| Hemmeligheder | Krypteret på serveren, sendes aldrig til browseren og skrives aldrig i log, audit eller job-output |
| Værtsnøgler | Fastlåses ved første forbindelse. Ved ændring stoppes forbindelsen, til en administrator har godkendt den nye |
| Revision | Hver forbindelse, kommandokørsel og ændring i nøgleboksen skrives i revisionsloggen |
| Optagelse | Ingen optagelse af sessioner. Tastetryk og output gemmes ikke |
| WebSocket | Engangsbillet fra et CSRF-beskyttet kald, Origin-tjek, kun https, grænser for antal og hastighed |
| Inaktivitet | Sessionen lukkes efter 15 minutter uden brug |
| Beskyttede maskiner | Pr. maskine. Risikable kommandoer kræver, at maskinens navn skrives |

### Fund i den eksisterende kode, som designet tager højde for

- `TwoFactorEndpoints.Prove` godtager adgangskode **eller** kode, og `tfa`-claimet siger kun, at brugeren har 2FA slået til. Kontrolrum-SSO og "husk enheden" logger ind uden kode. Terminalen har derfor sin egen oplåsning, der kun godtager en kode.
- CSRF-middlewaren dækker kun `/api` og ikke WebSocket-opkald. Billetten hentes derfor med et beskyttet POST-kald.
- Kontrolrums proxy overskriver `Origin` og lægger selv session-cookies på WebSocket-opkald (`proxy.js`). Et Origin-tjek kan altså ikke stå alene, og terminalen virker kun ved direkte https-adgang til Fleetdeck.
- En lang WebSocket overlever logud, fordi cookien kun tjekkes ved opkaldet. Sessioner genkontrollerer derfor brugerens `SecurityStamp` løbende.
- Selvopdateringen kopierer `fleetdeck.db` og `keys/` ukrypteret til `opdatering-backup/`. Nøgleboksens hovednøgle ligger derfor i `vault/`, som den kopi ikke omfatter, mens den krypterede `.fdbackup` tager den med.

## 5. Trusselsmodel

| Trussel | Værn |
|---|---|
| Stjålen session-cookie | Kode ved oplåsning, kort oplåsning, kun administratorer |
| Falsk server eller mand-i-midten mod agenten | Billetten ligger i det signerede svar med agentens nonce. Agenten kræver https eller lokal socket |
| Genbrug af billet | Engangsbrug, kort levetid, bundet til maskine og session |
| Anden side åbner en WebSocket | Billet fra CSRF-beskyttet kald og Origin-tjek |
| Stjålen database eller opdateringsbackup | Hemmeligheder er krypteret med en hovednøgle, der ligger et andet sted |
| Hele datamappen stjålet | **Ikke dækket:** serveren skal kunne låse op uden hjælp |
| Ondsindet eller kompromitteret administrator | **Ikke dækket:** administratorer kan i forvejen køre scripts som SYSTEM. Revisionsloggen gør det sporbart |
| Forkert fane eller tastefejl på en vigtig maskine | Beskyttet-markering og navnebekræftelse. Det er en sikkerhedssele mod uheld, ikke en adgangskontrol: det, man selv taster i skallen, kan ikke fortolkes pålideligt |
| Falsk SSH-vært | Fastlåst værtsnøgle med hårdt stop ved ændring |

## 6. Teknik

- **Browser:** `@xterm/xterm` 6 (MIT), ca. 86 KB gzip, plus fit-tilføjelsen. Indlæses kun på terminalsiden.
- **SSH på serveren:** `SSH.NET` 2026.0.1 (MIT, aktivt vedligeholdt, understøtter `net10.0`). Ed25519-nøgler laves med `BouncyCastle.Cryptography`, som SSH.NET allerede afhænger af.
- **Windows-agent:** ConPTY (`CreatePseudoConsole`, Windows 10 1809 eller nyere).
- **Linux og Mac:** `forkpty` kan ikke kaldes sikkert fra .NET, fordi barnet ville fortsætte i runtimen efter `fork`. Agenten åbner derfor PTY'en med `posix_openpt` og starter sig selv i en lille hjælpetilstand, der gør PTY'en til styrende terminal og kalder `execve`.
- **Kanal:** browser og agent forbinder hver sin WebSocket til serveren, som kobler dem sammen. Ældre agenter ignorerer det nye felt i check-in-svaret, og serveren tilbyder kun terminal til agenter, der melder understøttelse.

## 7. Faser

1. Værtsliste og agent-PTY på Windows, Linux og Mac.
2. Direkte SSH, nøgleboks og nøglegenerering.
3. Gemte kommandoer med variabler.
4. Faner, delt visning og tastaturrække på mobil.

## 8. Sådan er det bygget

| Del | Filer |
|---|---|
| Fælles protokol, billetter og Unix-PTY | `src/Fleetdeck.Shared/Terminal.cs`, `UnixPty.cs` |
| Windows-PTY | `src/Fleetdeck.Agent.Windows/ConPty.cs` |
| Oplåsning, sessioner, nøgleboks og SSH | `src/Fleetdeck.Server/Services/Terminal/` |
| API og WebSocket | `src/Fleetdeck.Server/Endpoints/TerminalEndpoints.cs` |
| Brugerflade | `web/src/pages/Terminal.tsx`, `web/src/terminal/` |

Beskyttede maskiner styres med feltet `Machine.Protected`. Miljøvariablen `TERMINAL_PROTECTED_MACHINES` (kommasepareret, standard ingen) markerer maskiner ved serverstart. Det sker én gang pr. navn, så en administrator kan fjerne markeringen igen.

### Forudsætninger for at bruge terminalen

- Fleetdeck skal åbnes direkte over https (`HTTPS_PORT`). Over http eller gennem Kontrolrums proxy er terminalen spærret.
- Agenter skal forbinde over https eller den lokale socket og have fastlåst jobnøglen.
- Brugeren skal være administrator og have totrinsbekræftelse slået til.

### Afgrænsninger

- Sessioner optages ikke. Der findes ingen optagefunktion.
- Gemte kommandoer køres i åbne sessioner, ikke på maskiner uden en åben session.
- Værtsnøglen fastlåses ved første forbindelse, så den allerførste forbindelse til en vært er uden beskyttelse mod en falsk vært.

### Skal afprøves på rigtige maskiner

1. Agent-PTY på Windows (ConPTY, størrelse, lukning af procestræet).
2. Agent-PTY på Mac, især Apple Silicon, og på Linux med musl (Alpine) og ARM.
3. Unraid-værten gennem socket-filen, hvor åbningen venter på næste check-in.
4. Direkte SSH mod en rigtig vært: nøgle, adgangskode, installation af nøgle og ændret værtsnøgle.
5. Tastaturrække og skærmtastatur på iPhone og iPad.
