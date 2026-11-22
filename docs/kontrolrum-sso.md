# Log ind fra et dashboard (Kontrolrum-SSO)

Viser du Fleetdeck indlejret i et dashboard, fx Kontrolrum, kan dashboardet logge ind for brugeren af sig selv.
Før skete det med et gemt brugernavn og en adgangskode. Det holder op med at virke, så snart brugeren får
totrinsbekræftelse, for dashboardet kan ikke taste koden.

Med single sign-on (SSO) gemmer dashboardet ikke længere adgangskoden. Det får i stedet en delt nøgle fra Fleetdeck.
Når det skal logge ind, signerer det en kort besked med nøglen: "log ind som brugeren X". Beskeden gælder kun i kort
tid og kun én gang. Fleetdeck giver så den samme session som efter et almindeligt login. Har brugeren
totrinsbekræftelse, regnes den for opfyldt, så der ikke spørges efter en kode.

## Sikkerhed: hvad du siger ja til

SSO betyder, at Fleetdeck **stoler på dashboardet som identitetsudbyder** for de brugere, der har fluebenet
**Må logges ind fra Kontrolrum**. Den, der har nøglen, kan logge ind som de brugere uden adgangskode og uden kode.

- Brug det kun, hvis dashboardet selv kræver et sikkert login, gerne med passkey eller totrinsbekræftelse, før det
  viser Fleetdeck.
- Giv kun fluebenet til de brugere, dashboardet skal logge ind som. Det er slået fra for alle som standard, og kun
  administratorer kan ændre det.
- Behandl nøglen som en adgangskode. Den vises kun én gang. Fleetdeck gemmer den krypteret og viser den aldrig igen.
- Er du i tvivl om, hvorvidt nøglen er kommet ud, så klik **Ny nøgle**. Den gamle holder op med at virke med det samme.

## Sådan parrer du et dashboard

1. Åbn **Indstillinger → Kontrolrum** i Fleetdeck som administrator.
2. Under **Log ind fra Kontrolrum** klikker du **Lav SSO-nøgle**.
3. Kopiér nøglen (64 tegn) og indsæt den i dashboardet. I Kontrolrum lægges den som **SSO-nøgle** på
   Fleetdeck-tjenesten, og brugernavnet skrives under **Automatisk login**. En gemt adgangskode kan derefter fjernes.
4. Sæt flueben ved **Må logges ind fra Kontrolrum** for den eller de brugere, dashboardet skal logge ind som.
5. Åbn Fleetdeck gennem dashboardet. I **Revisionslog** står loginet som `login.kontrolrum`.

**Ny nøgle** laver en ny nøgle og gør den gamle ugyldig. **Slå fra** sletter nøglen, så ingen kan logge ind med SSO.
Sessioner, der allerede er åbne, fortsætter. Flyttes Fleetdeck til en ny server uden sine nøgler (`data/keys`), kan
den gemte nøgle ikke læses. Så laver du en ny.

## Til dig, der bygger et dashboard

`POST /api/auth/kontrolrum` med headerne `Content-Type: application/json` og `X-Fleetdeck: 1`. Kroppen er:

```json
{ "v": 1, "aud": "fleetdeck", "sub": "<brugernavn>", "iat": 1763467200, "exp": 1763467260,
  "nonce": "<16-48 tegn: a-z, A-Z, 0-9, - eller _>", "sig": "<hex>" }
```

- `iat` og `exp` er Unix-tid i sekunder. `exp - iat` må højst være 120 sekunder.
- `sig` er HMAC-SHA256 i hex (små eller store bogstaver). Nøglen er den viste nøgle **som tekst** (UTF-8), altså de
  64 tegn, og ikke de 32 bytes, de står for. Teksten, der signeres, er de syv linjer
  `kontrolrum-sso`, `v`, `aud`, `sub`, `iat`, `exp` og `nonce`, adskilt af `\n` (uden afsluttende linjeskift).

Eksempel i Node:

```js
const a = { v: 1, aud: 'fleetdeck', sub: 'brugernavn', iat, exp: iat + 60, nonce: crypto.randomBytes(16).toString('hex') };
a.sig = crypto.createHmac('sha256', key)
  .update(['kontrolrum-sso', a.v, a.aud, a.sub, a.iat, a.exp, a.nonce].join('\n')).digest('hex');
```

Fleetdeck tjekker i denne rækkefølge:

| Svar | Hvornår |
|---|---|
| `403 {"error":"sso_disabled"}` | Der er ingen SSO-nøgle (slået fra) |
| `400 {"error":"bad_request"}` | Et felt mangler, eller `nonce` har et forkert format |
| `401 {"error":"bad_signature"}` | Signaturen passer ikke. Den sammenlignes i konstant tid |
| `401 {"error":"bad_version"}` / `"bad_audience"` | `v` er ikke 1, eller `aud` er ikke `fleetdeck` |
| `401 {"error":"expired"}` | Udløbet, mere end 120 sekunder lang eller udstedt i fremtiden. Der tåles 60 sekunders urforskel |
| `401 {"error":"replay"}` | Samme `nonce` er brugt før. Brugte værdier gemmes i databasen, så det gælder også efter en genstart |
| `401 {"error":"unknown_user"}` | Brugeren findes ikke eller har ikke fluebenet |
| `429` | For mange forsøg: 10 pr. 5 minutter pr. klient-IP (bag en betroet proxy bruges `X-Forwarded-For`), eller brugeren er låst efter forkerte adgangskoder |
| `200 {"username","role","mustEnroll"}` | Logget ind. Session-cookien `ws_session` sættes |

Sessionen er den samme som efter et almindeligt login. Kræver politikken totrinsbekræftelse, og har brugeren endnu ikke
sat den op, er `mustEnroll` `true`, og sessionen kan kun bruges til at sætte den op. Alle afslag skrives i revisionsloggen
som `login.kontrolrum_failed` med grunden.
