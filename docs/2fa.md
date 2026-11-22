# Totrinsbekræftelse (2FA)

Med totrinsbekræftelse skal man ved login bruge både sin adgangskode og en 6-cifret kode fra en
autentificerings-app på telefonen. En stjålet eller gættet adgangskode er så ikke nok til at komme ind.

Fleetdeck bruger standarden TOTP (RFC 6238: SHA-1, 6 cifre, 30 sekunder), som alle almindelige apps forstår.
Det kan fx være Google Authenticator, Microsoft Authenticator, Aegis, 2FAS eller en adgangskodemanager med
TOTP-understøttelse.

## Slå det til for din egen konto

1. Åbn **Min konto**:
   - Administratorer og læsere: klik på dit navn øverst til højre og vælg **Min konto**.
   - Forældre: knappen **Min konto** øverst.
2. Klik **Sæt totrinsbekræftelse op**.
3. Scan QR-koden med appen. Kan du ikke scanne, så tast nøglen ind manuelt (tidsbaseret, 6 cifre).
4. Skriv den kode, appen viser, og klik **Bekræft og slå til**.
5. Gem de 10 **gendannelseskoder**. De vises kun denne ene gang.
   - Kopiér dem, eller hent dem som fil.
   - Gem dem et sikkert sted *uden for* telefonen, fx i en adgangskodemanager eller på papir.

Når totrinsbekræftelse slås til, logges dine andre sessioner ud.

## Login

1. Skriv brugernavn og adgangskode som før.
2. Skriv koden fra appen.
   - Klik **Brug en gendannelseskode**, hvis telefonen ikke er ved hånden. Hver gendannelseskode virker kun én gang.
3. Sæt evt. hak i **Husk denne enhed i 30 dage**. Så spørges der ikke om kode på denne browser i 30 dage.

En husket enhed glemmes automatisk, når du:

- skifter adgangskode,
- slår totrinsbekræftelse fra eller får den nulstillet,
- får ændret din rolle.

Samme kode fra appen kan ikke bruges to gange. Har du lige logget ind eller slået totrinsbekræftelse til,
så vent på den næste kode.

## Gendannelseskoder, ny telefon og slå fra

Under **Min konto → Totrinsbekræftelse**:

- **Nye gendannelseskoder** laver 10 nye koder. De gamle holder op med at virke.
  Siden viser, hvor mange koder du har tilbage.
- **Slå fra** fjerner totrinsbekræftelse fra kontoen.
  - Ny telefon: slå fra, og sæt det op igen med den nye telefon.

Begge dele kræver enten en kode fra appen (eller en gendannelseskode) eller din adgangskode.

## Politik for hele Fleetdeck (administrator)

Under **Indstillinger → Brugere og sikkerhed → Sikkerhed** vælges:

| Politik | Betydning |
|---|---|
| **Valgfri** | Hver bruger vælger selv. |
| **Påkrævet for administratorer** | Administratorer uden totrinsbekræftelse skal sætte den op lige efter adgangskoden, før de kan bruge Fleetdeck. |
| **Påkrævet for alle** | Det samme for alle roller, også læsere og forældre. |

Når en politik kræver totrinsbekræftelse:

- En bruger uden totrinsbekræftelse kan kun se opsætningen og logge ud, indtil den er sat op.
- Det gælder også sessioner, der allerede er logget ind.

Er du selv administrator uden totrinsbekræftelse og vælger en påkrævet politik, bliver du bedt om at sætte den
op med det samme.

Under **Brugere** viser mærket **2FA**, hvem der har totrinsbekræftelse slået til.

## En bruger har mistet telefonen

### Brugeren har sine gendannelseskoder

1. Log ind med adgangskoden og en gendannelseskode.
2. Gå til **Min konto**, slå totrinsbekræftelse fra, og sæt den op igen med den nye telefon.
3. Lav nye gendannelseskoder, hvis der er få tilbage.

### Brugeren har ingen gendannelseskoder

En administrator klikker **Nulstil 2FA** ud for brugeren under **Brugere**. Brugeren:

- logges ud overalt,
- mister sine huskede enheder,
- sætter totrinsbekræftelse op igen ved næste login (med det samme, hvis politikken kræver det).

En administrator kan ikke nulstille sin egen totrinsbekræftelse på den måde. Brug **Min konto** eller
afsnittet herunder.

## En administrator har mistet telefonen

### Med gendannelseskoder

Log ind med en gendannelseskode, og følg trinene for *Brugeren har sine gendannelseskoder* ovenfor.

### Der er en anden administrator

Bed den anden administrator om at klikke **Nulstil 2FA** ud for dig.

### Ingen gendannelseskoder og ingen anden administrator

Brug administrator-nulstillingen fra kommandolinjen på serveren (se `docs/backup.md`):

```sh
docker exec -it fleetdeck dotnet Fleetdeck.Server.dll reset-admin <brugernavn>
```

Den giver en midlertidig adgangskode og slår totrinsbekræftelse fra for brugeren. Log derefter ind, skift
adgangskoden og sæt totrinsbekræftelse op igen.

Det kræver adgang til serveren (fx en shell på Docker-værten). Det er altså ikke en vej uden om
totrinsbekræftelse for nogen udefra.

## Sikkerhed bag kulisserne

### Hemmeligheder og koder

- **TOTP-hemmeligheden** gemmes krypteret med serverens DataProtection-nøgle (mappen `keys/` i datamappen).
  - Mistes `keys/`, kan koderne fra appen ikke længere kontrolleres.
  - **Gendannelseskoderne virker stadig**, fordi de ikke afhænger af nøglen.
  - Tag derfor backup af hele datamappen, også `keys/`.
- **Gendannelseskoderne** gemmes kun som hash (PBKDF2). En brugt kode slettes.

### Login

- **Login-trinnet:** Efter den rigtige adgangskode får browseren kun en kortlivet billet (10 minutter) til kodetrinnet.
  Den giver ikke adgang til noget.
- **Husk denne enhed:** cookien `ws_trust` er:
  - krypteret og signeret,
  - `HttpOnly` og `SameSite=Lax`,
  - bundet til brugeren og brugerens sikkerhedsstempel, som skifter ved ny adgangskode eller ændret totrinsbekræftelse.

### Spærring

Efter 5 forkerte forsøg i træk spærres brugeren midlertidigt. Det gælder både adgangskode og kode.

- Den første spærring varer 1 minut.
- Derefter fordobles ventetiden for hvert nyt fejlforsøg, op til 15 minutter.
- Et vellykket login nulstiller tælleren.
- Derudover er der som før en grænse for login-forsøg pr. IP-adresse.

### CSRF

Ændringer via API'et med login-cookie kræver headeren `X-Fleetdeck: 1`, som brugerfladen altid sender.

- Andre websider, også andre apps på samme vært, kan ikke sætte den.
- Indlejring i et andet dashboard (fx gennem en proxy under en understi) virker som før.
- Agenter og scripts uden login-cookie berøres ikke.

### Betroede proxyer

Klientens IP-adresse læses kun fra `X-Forwarded-For`, når forespørgslen kommer fra en betroet proxy. Den bruges
i loggen og til grænsen for login-forsøg.

- Standard er loopback og Dockers interne net (`127.0.0.0/8, ::1, 172.16.0.0/12`).
  Standarden kan ændres med miljøvariablen `TRUSTED_PROXIES`.
- Kører en reverse proxy eller et dashboard på en anden maskine, tilføjes dens IP under
  **Indstillinger → Brugere og sikkerhed → Betroede proxyer**. Ellers ser alle brugere bag den ud til at komme fra
  samme adresse.
- Understien (`X-Forwarded-Prefix`) virker uanset denne liste.

### Audit-log

Følgende skrives i audit-loggen:

- `login`, `logout` og `login.failed`,
- `login.2fa_failed` og `login.locked`,
- `2fa.enabled`, `2fa.disabled`, `2fa.reset`, `2fa.recovery_used`, `2fa.recovery_regenerated` og `2fa.proof_failed`,
- `settings.security`.
