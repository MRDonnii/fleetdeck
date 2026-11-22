# Tredjepartskode i Fleetdeck

Fleetdeck selv er udgivet under PolyForm Noncommercial License 1.0.0 (se [LICENSE](LICENSE)).
Repoet indeholder også kode og filer fra andre projekter. De beholder deres egne licenser:

## Medfølgende i repoet

| Hvad | Hvor | Licens | Kilde |
|---|---|---|---|
| **ESP Web Tools** (bygget bundt) | `web/public/esp-web-tools/` | Apache-2.0 (se `web/public/esp-web-tools/LICENSE`) | https://github.com/esphome/esp-web-tools |
| – heri: esptool-js og Espressifs flasher-stubs | samme mappe (`esp32*.js`, `stub_flasher_*.js`) | Apache-2.0 (esptool-js); flasher-stubs: Apache-2.0 / MIT (esp-flasher-stub) | https://github.com/espressif/esptool-js, https://github.com/espressif/esp-flasher-stub |
| – heri: Lit og Material Web-komponenter | samme mappe | BSD-3-Clause (Lit), Apache-2.0 (Material Web) | https://lit.dev, https://github.com/material-components/material-web |
| – heri: pako | samme mappe | MIT og Zlib | https://github.com/nodeca/pako |

Opdateres ESP Web Tools, skal `LICENSE`-filen i mappen følge med.

## Pakker, der hentes ved byg (ikke i repoet)

Pakkerne hentes af `dotnet restore` og `npm ci` og indgår i de byggede programmer og Docker-billedet.

**.NET (NuGet, se `Directory.Packages.props`)**
- Microsoft.EntityFrameworkCore.*, Microsoft.AspNetCore.SignalR.Client, Microsoft.Extensions.*, System.Management,
  System.Security.Cryptography.*: MIT.
- SQLitePCLRaw: Apache-2.0. SQLite selv: public domain.
- BCrypt.Net-Next: MIT. SSH.NET: MIT. BouncyCastle.Cryptography: MIT.
- Avalonia (Mac-appen): MIT. Avalonia.Fonts.Inter indeholder skrifttypen Inter: SIL Open Font License 1.1.
- Kun til test: xunit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), coverlet.collector (MIT).

**Webfladen (npm, se `web/package.json`)**
- React, React Router, Mantine, Tabler Icons, TanStack Query, @microsoft/signalr, xterm.js, Recharts, Day.js og
  qrcode-generator: MIT.
- Skrifttypen Inter (@fontsource-variable/inter): SIL Open Font License 1.1.
- Den fulde liste med versioner står i `web/package-lock.json`. Licenserne følger med i `node_modules`.
