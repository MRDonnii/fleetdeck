# Fleetdeck: Apple MDM ("Apple-administration") design

Status: Draft v1, 2026-11-17. Scope: iPadOS/iOS device management for a household without Apple Business Manager, built generically so other households can install it.

## 0. Executive summary and gating decision

- **Base:** use **NanoHUB** (MIT; NanoMDM + KMFDDM + NanoCMD in one Go binary, `ghcr.io/micromdm/nanohub`, pinned by digest, v0.2.x) as a headless sidecar container. Fleetdeck stays the brain: inventory, policy, UI, permissions, workflow. Fleetdeck's SQLite stays the source of truth. Do not use MicroMDM (deprecated), Fleet (heavy, open-core, duplicates the UI) or nanodep (needs ABM).
- **Gate:** the APNs MDM push certificate decides whether there is a project. Apple's terms (ADPLA "Use of MDM", MDM Certificate Agreement) are written for organizations and exclude consumer/personal use. mdmcert.download's terms explicitly exclude individuals. **Fleetdeck will not steer households to mdmcert.download, and will not ship or share a vendor signing certificate.** Etape 0 settles which legitimate route this owner has (see §2.4). If none exists, Fleetdeck still delivers a large part of the child-protection value through **"Statisk tilstand"**: Apple Configurator supervision plus Fleetdeck-generated profiles plus the existing AdGuard control. That mode needs no APNs.
- **Device split:** the 2 children's iPads are **supervised** with Apple Configurator, which erases them, and then enrolled. The 2 family iPads are **unsupervised** with profile-based Device Enrollment. They are not erased, which is enough for OS-update enforcement and inventory.
- **OS updates:** use only **DDM** (`softwareupdate.enforcement.specific`, `softwareupdate.settings`). The legacy ScheduleOSUpdate, AvailableOSUpdates and OSUpdateStatus commands are removed in iPadOS 27 and must not be built.

## 1. Base and deployment topology

### 1.1 Containers

| Container | Image | Network | Role |
|---|---|---|---|
| Fleetdeck | `fleetdeck:latest` (existing) | br0, static LAN IP | UI/API, brain, enrollment-profile server, webhook receiver |
| fleetdeck-mdm (NanoHUB) | `ghcr.io/micromdm/nanohub@sha256:…` | br0, static LAN IP (setting; check that the address is free) | MDM protocol `/mdm` (+ `/checkin`), DDM, command queue, APNs push |
| cloudflared-br0 (planned for DoH, <lan-ip>) | `cloudflare/cloudflared` | br0 | Public ingress for `dns.<domain>` and `mdm.<domain>` |

- Ship `deploy/unraid/fleetdeck-mdm.xml` (Unraid template) and `deploy/compose/mdm.yml` (generic docker-compose) with placeholders only. Hard-coded IPs, domains and names are not allowed.
- NanoHUB config is set through env vars: `NANOHUB_API_KEY` (random 48 bytes; Fleetdeck generates it and the admin pastes it into the template, or Fleetdeck writes it via the Unraid agent), `NANOHUB_WEBHOOK_URL=http://<fleetdeck-ip>:<port>/api/mdm/hook/<secret>`, `NANOHUB_CA=/data/ca/device-ca.pem`, storage `file` at `/data` → `/mnt/user/appdata/fleetdeck-mdm`.
- Fleetdeck calls NanoHUB LAN-to-LAN on br0. The macvlan containers can reach each other; only the Unraid host cannot.
- Storage: NanoHUB `file` backend is enough for up to about 100 devices. MySQL is optional later. There is no SQLite backend, and that is deliberate: Fleetdeck holds the state that matters.
- Outbound needs: NanoHUB → `api.push.apple.com:443` (HTTP/2). Fleetdeck → `gdmf.apple.com` (OS version feed, §4.6). iPads → `*.push.apple.com:5223` (falls back to 443).

### 1.2 Public endpoint (Cloudflare Tunnel, default)

Hostname `mdm.<domain>` on the br0 cloudflared connector. Ingress rules are evaluated in order:

1. `mdm.<domain>`, path `^/mdm$` and `^/checkin$` → `http://<nanohub-ip>:9004`
2. `mdm.<domain>`, path `^/enroll/[A-Za-z0-9_-]{43}$` → `http://<fleetdeck-ip>:<port>` (rewritten to Fleetdeck's anonymous `/mdm-tilmelding/{token}`)
3. `mdm.<domain>` catch-all → `http_status:404`

The following are never publicly reachable: NanoHUB `/api/v1/*`, the Fleetdeck UI and `/api/*`. The admin UI must stay off the public hostname, because of the cookie SameSite=Lax/SecurePolicy=SameAsRequest settings.

Cloudflare settings: WAF custom rule "Skip bots for MDM" on `mdm.<domain>` that skips Bot Fight Mode / Super Bot Fight Mode and managed challenges. Devices (UA `MDM/1.0`, `MDMClient`, `ManagedClient`) cannot solve challenges.

**Alternative topology "Direkte"** (a setting, for households that do not want Cloudflare in the path): router port-forward 443 → a Caddy container on br0 with Let's Encrypt. This gives end-to-end TLS. Both topologies use the same device-side config (§1.3), so switching only needs a DNS change, provided the hostname stays the same. ServerURL can never change after enrollment, so **the hostname is chosen once in the wizard and locked.**

### 1.3 TLS and device identity

- Server TLS: Cloudflare edge certificate (publicly trusted; meets iPadOS 27 ATS-grade enforcement: TLS 1.2+/FS, ≥RSA2048/ECDSA256, SHA-256). The hop from cloudflared to the origin is plain HTTP inside the LAN.
- Cloudflare terminates TLS, so client-certificate mTLS cannot reach the origin. **The enrollment profile always sets `SignMessage=true`.** The device then signs every request (`Mdm-Signature` header, CMS detached), and NanoMDM verifies this by default against `-ca`.
- Identity issuance, v1 (decision): **Fleetdeck-issued per-device PKCS#12** embedded in the enrollment profile (`com.apple.security.pkcs12`, referenced by `IdentityCertificateUUID`).
  - The device CA is generated by Fleetdeck: ECDSA P-256 root, 10-year life, private key encrypted with DataProtection in `/data/mdm/ca/`. The public cert is copied to the NanoHUB volume.
  - Device certs: CN `fd-dev-<id>-<random>`, 2-year life, serial and thumbprint stored.
  - Why: no extra container, and it fits the existing link/QR delivery.
  - Accepted trade-off: the private key travels inside a single-use profile over TLS. Someone who holds the profile can only impersonate that device to the server; it gives no control of the device.
  - Behind an `IDeviceIdentityIssuer` interface.
- Identity v2 (Etape 6, only if the spike passes): **ACME with hardware-bound keys** (`com.apple.security.acme`, step-ca with `device-attest-01`), so the key never leaves the device. Verify that it works as the MDM identity in a manually installed, non-ADE profile.
- micromdm/scep is not used, because upstream is winding it down.

### 1.4 Backups (critical items)

| Item | Location | If lost |
|---|---|---|
| Device CA key + cert | Fleetdeck `/data/mdm/ca` | **All devices must re-enroll** (NanoMDM rejects every signature) |
| DataProtection keys | Fleetdeck `/data/keys` | Encrypted secrets (AdGuard, NanoHUB API key, CA key, push key) are lost |
| APNs push cert + key | Fleetdeck (encrypted) + NanoHUB storage | Recoverable via "Renew" on the same Apple ID entry (same topic) |
| NanoHUB data | `/mnt/user/appdata/fleetdeck-mdm` | Push tokens are lost until devices check in again; queued commands are lost |
| SQLite | `/data/fleetdeck.db` | Inventory and policy are lost |

- Wizard step "Nødkopi": a downloadable, password-encrypted bundle (CA key, push key/cert, topic, settings). The UI shows "Sidst gemt" and warns if the bundle is older than the last cert change.
- Restore drill is an Etape 6 acceptance test.

## 2. One-time setup wizard (Indstillinger → Apple-administration)

Each step has a "Test" button and a persisted state. The wizard can be resumed.

**Trin 1: Forudsætninger og vilkår.** Explain what MDM is, and that the children's iPads will be erased during supervision. Explain Apple's licensing: a push certificate requires an organization-eligible route (§2.4). Checkboxes: the user has a Mac with Apple Configurator, knows the Apple Account passwords and Find My status for all iPads, and has read the terms.

**Trin 2: Offentlig adresse.**
- Choose the topology: Cloudflare (default) or Direkte.
- Enter the hostname `mdm.<domain>` and the cloudflared connector name.
- Fleetdeck shows the exact ingress rules (§1.2) and the WAF skip rule as copyable text, plus an optional ready-made prompt for a browser agent, in the same style as the DoH setup.
- Test: Fleetdeck calls `https://mdm.<domain>/mdm` from outside (via a public check, or the user opens a link on mobile data) and expects a NanoHUB response (405/400, not a Cloudflare challenge). It also checks that `https://mdm.<domain>/api/v1/version` returns 404 publicly. The hostname is then **locked**.

**Trin 3: MDM-server.** Fleetdeck generates the API key and webhook secret, then shows the filled-in Unraid template or compose snippet (image digest, IP, env). On Unraid, there is an optional "Opret container" button via the existing host-agent script job. Test: `GET /version` with Basic auth; a webhook round-trip, done by triggering a NanoHUB test push to a fake ID and checking that the hook is reached, or by sending a synthetic event.

**Trin 4: Push-certifikat (APNs).**
1. Fleetdeck generates `push.key` (RSA 2048, encrypted at rest) and offers `push.csr` for download.
2. Get the CSR vendor-signed through the route chosen in §2.4. For the developer-account route, Fleetdeck can do the signing step itself: the user uploads `mdm.cer` + `mdmcsr.key`, and Fleetdeck builds the Base64 plist (`PushCertRequestCSR`, `PushCertCertificateChain`, `PushCertSignature`). Verify byte-compatibility against `mdmutil mdmcsr-sign` output first.
3. The user signs in at identity.apple.com/pushcert, chooses Create, uploads the request and downloads the `.pem`.
4. The user uploads the `.pem` in Fleetdeck. Fleetdeck checks that the cert matches `push.key`, extracts the topic (UID `com.apple.mgmt.External.<uuid>`) and the expiry, records an Apple ID hint (free text), and does `PUT /api/v1/nanomdm/pushcert`.
5. The topic is now locked. Every later upload must have the same topic, or Fleetdeck refuses it with an explanation.

**Trin 5: Enhedsidentitet.** Generate the device CA and copy the CA cert to the NanoHUB volume (via the agent, or show manual instructions). Prompt to make "Nødkopi" immediately.

**Trin 6: Prøvetilmelding.** Enroll one family iPad (unsupervised, the lowest risk) and confirm that Authenticate → TokenUpdate → DeviceInformation is shown.

### 2.1 Per-device wizard "Tilføj til MDM": family iPad (unsupervised)

1. Choose the device row and set Kind = family.
2. "Lav tilmeldingslink": single-use, 30-min TTL, shown as a QR code.
3. On the iPad: open the link in Safari, then Settings → Profil hentet → Installér, then Settings → General → VPN & Device Management → approve.
4. Fleetdeck shows "Tilmeldt" live (webhook), then fetches the inventory.

### 2.2 Per-device wizard: child's iPad (supervised via Apple Configurator)

1. **Før du starter:** confirm the child's Apple Account email and password, that Find My is on, and the passcode. Put the iPad on charge.
2. **Sikkerhedskopi:** connect it to the Mac. In Finder, choose "Back up all data to this Mac" with **Encrypt local backup** turned on (this keeps Health/keychain data). Wait for it to finish and check the date.
3. **Overvågning (sletter iPad'en):** in Apple Configurator, select the device → Prepare → Manual Configuration → tick **Supervise devices** and **Allow devices to pair with other computers**: off/on according to preference → MDM server: **Do not enroll** in v1 (see the verify item below) → organization: create one with the household name (the supervision identity; export it and store it with "Nødkopi") → choose which Setup Assistant panes to skip → Prepare.
4. **Gendan:** during Setup Assistant choose "Restore from Mac/PC backup" (Configurator: Actions → Restore from Backup), using the encrypted backup. Alternatively use iCloud restore.
5. **Kontrol:** Settings must show the banner "Denne iPad er overvåget og administreret af …". If supervision is lost after the restore, Fleetdeck shows how to repeat step 3 and restore from iCloud instead.
6. **Tilmelding:** Fleetdeck offers an authenticated download of the device's enrollment `.mobileconfig` for the admin on the Mac. In Configurator: Add → Profiles, then approve on the device. A single-use link/QR also works.
7. **Profiler:** Fleetdeck pushes the DNS lock, restrictions and app settings (§3). It confirms `IsSupervised=true` and shows the result.

Verify first, in Etape 0, on a **family** iPad before touching the children's: (a) whether the order "Prepare, then restore from backup" keeps supervision on iPadOS 26/27 without ADE; (b) whether Configurator's "Enroll in MDM server" option with Fleetdeck's enrollment URL works and is preferable to step 6; (c) whether a Configurator-installed MDM profile on a supervised, non-ADE device can be removed (expected: yes, it can).

### 2.3 Activation Lock

On supervised devices, Fleetdeck requests `ActivationLockBypassCode` after enrollment and stores it encrypted. NanoMDM's `escrowkeyunlock` can then be used if a child's iPad is erased while locked.

### 2.4 Legitimate APNs routes (Etape 0 decision, owner action)

1. **The owner's own Apple Developer Program membership** (US$99/yr). Ask Apple Developer Support for the "MDM CSR" certificate type. Describe the use honestly (self-hosted management of the household's own devices). Accept whatever answer Apple gives. If Apple grants it in writing, that is the cleanest route. Unknown: whether individual memberships get it.
2. **A legal entity, if the owner has or creates one** (e.g. a Danish enkeltmandsvirksomhed with CVR, which can get a free D-U-N-S number). That entity could also open the now-free **Apple Business**, which unlocks ADE (non-removable MDM, re-enroll after erase) and Apps and Books/VPP. This is the owner's decision; Fleetdeck must not suggest misrepresenting anything. If chosen, Fleetdeck adds nanodep in a later etape.
3. **No route:** use "Statisk tilstand" (§3.3). The wizard says this plainly.

The wizard for other households presents the same three routes in neutral wording and never recommends mdmcert.download to private persons.

## 3. Feature matrix for this household (no ABM)

Columns: U = unsupervised device enrollment (family iPads), S = Configurator-supervised + enrolled (children's), Statisk = supervised, no APNs/MDM.

| Feature (Danish UI name) | U | S | Statisk | Mechanism |
|---|---|---|---|---|
| Enhedsinfo (model, OS, battery, storage, serial, backup date, Find My) | Yes | Yes | No | DeviceInformation, SecurityInfo |
| Installerede apps | Yes | Yes | No | InstalledApplicationList |
| Tving OS-version inden frist | Yes | Yes | No | DDM `softwareupdate.enforcement.specific` (17+) |
| Udskyd opdateringer / auto-download+installér | No | Yes | No | DDM `softwareupdate.settings` (supervised keys) |
| Opdateringsstatus | Yes | Yes | No | DDM status items `softwareupdate.*`, `device.operating-system.*` |
| Lås skærm med besked | Yes | Yes | No | DeviceLock |
| Slet iPad | Yes | Yes | No | EraseDevice (the device then needs Configurator again) |
| Ryd adgangskode | Yes | Yes | No | ClearPasscode (needs UnlockToken) |
| Genstart / sluk | No | Yes | No | RestartDevice / ShutDownDevice |
| Mistet-tilstand, placering, afspil lyd | No | Yes | No | EnableLostMode, DeviceLocation, PlayLostModeSound |
| Skjul/blokér apps (fx YouTube) | No | Yes | Yes (profile) | 27+: DDM `app.settings`; <27: `blockedAppBundleIDs` |
| Aldersgrænse for apps | Yes | Yes | Yes | `ratingApps` |
| Blokér App Store, sletning af apps, profilinstallation, nulstilling, kontoændringer | No | Yes | Yes | `com.apple.applicationaccess` supervised keys |
| DNS-filter (AdGuard DoH) | Yes | Yes | Yes | `com.apple.dnsSettings.managed` / 27+ DDM `network.dns-settings` |
| Lås DNS-filter (kan ikke slås fra) | No | Yes | Yes | `ProhibitDisablement` (supervised) |
| Indbygget webfilter (voksenindhold) | No | Yes | Yes | `com.apple.webcontent-filter` BuiltIn/AutoFilter |
| Installer gratis App Store-app (bruger godkender, eget Apple ID) | Yes | Yes | No | InstallApplication iTunesStoreID, PurchaseMethod 0 |
| Overtag app som administreret | Yes | Yes | No | ChangeManagementState |
| Activation Lock-omgåelseskode | No | Yes | No | ActivationLockBypassCode |
| Sengetid/skoletid/pause/YouTube restricted (existing) | Yes | Yes | Yes | AdGuard (unchanged) |
| Alarm ved "ikke set i X timer" | Yes | Yes | No | Last check-in + HA Notify |

**Not possible without ABM/ADE** (state this clearly in the UI with "Kræver Apple Business"):
- A non-removable MDM profile. It can be removed even when supervised. Mitigation: block profile installation and erase, and alert when check-ins stop.
- Automatic re-enrollment after an erase.
- Silent app install, app install without an Apple Account, VPP licences, DDM `app.managed`, MDM-controlled app updates.

**Screen Time split:** time limits per app and Ask to Buy stay in Family Sharing/Screen Time. Fleetdeck does DNS schedules, app blocking, locks and updates. Where both apply, the most restrictive setting wins.

### 3.3 Statisk tilstand (no APNs)

Fleetdeck generates signed or unsigned `.mobileconfig` files: the DNS lock with ProhibitDisablement, restrictions, the app block, and the web filter. The admin installs them with Configurator, and updating a profile means connecting the iPad to the Mac again. The existing AdGuard control works unchanged. This is built in Etape 1 because it is useful whatever happens with APNs.

## 4. Data model and API changes

### 4.1 Generalise Ipad to MobileDevice (no data break)

- Rename the C# class `Ipad` to `MobileDevice` with `[Table("Ipads")]`; the table keeps its name and ids. `User.Ipads`/`IpadAll`, `FamilyScope.Sees(id)` and `IpadSlot.IpadId` stay valid.
- New columns (EF migration after `20261117174806_IpadLanIp`):
  - `Platform` (string, default `ipados`)
  - `DnsManaged` (bool, default **true** for existing rows)
  - `MdmMode` (`none|enrolled|static`)
  - `Kind` is kept (child/family).
- **AdGuard couplings to fix in the same change:**
  - `IpadEngine.Apply` must filter `Where(d => d.DnsManaged)`.
  - The orphan cleanup must keep the `fd-ipad-{id}` prefix and only delete clients whose id has no row, or whose row has `DnsManaged=false`.
  - `IpadPolicy.ClientName` is unchanged.

### 4.2 New entities

- `MdmEnrollment` (1:1 MobileDevice): `DeviceId`, `Udid` (unique), `EnrollmentId`, `Serial`, `ProductName`, `Model`, `OsVersion`, `BuildVersion`, `Supervised`, `BatteryLevel`, `StorageTotal/Free`, `LastCheckin`, `LastInventory`, `HasUnlockToken`, `ActivationLockCode` (encrypted), `IdentityCertSerial`, `EnrolledAt`, `CheckedOutAt`, `State` (`pending|enrolled|checked_out`).
- `MdmEnrollToken`: `Token` (hash only), `DeviceId`, `Expires`, `UsedAt`, `Channel` (`link|configurator`). Consumed atomically on first fetch. This also fixes the bug class where the existing `/ipad-profil/{token}` link is reusable, so the DNS-profile link becomes single-use as well.
- `MdmCommand`: `CommandUuid` (PK, generated by Fleetdeck), `DeviceId`, `RequestType`, `PayloadHash`, `Status` (`queued|sent|acknowledged|error|notnow|stale|cancelled`), `RequestedBy`, `Reason`, `Created`, `Completed`, `ErrorChainJson`, `ResultJson` (trimmed).
- `MdmApp`: `DeviceId`, `BundleId`, `Name`, `Version`, `Managed`, `SeenAt`.
- `MdmProfileState`: `DeviceId`, `Identifier`, `DesiredHash`, `InstalledHash`, `Status`. Profiles are desired-state; the engine reconciles them.
- `OsUpdatePolicy`: `Scope` (device or Kind), `Target` (`latest|latest-minor|x.y.z`), `DeadlineDays` (default 7), `InstallWindow` (reuses the 336-char WeekGrid format), `AutoInstall` (supervised only), `DeferDays` (supervised only).
- `MdmSettings` in the `Setting` table: topology, hostname (locked), NanoHUB URL, API key (encrypted), webhook secret (encrypted), push topic, push expiry, Apple ID hint, CA thumbprint.

### 4.3 Services

- `MdmClient` (HttpClient, 15 s timeout, Basic `nanohub:<key>`):
  - `PutPushCert`, `Enqueue(ids, plist, nopush)`, `Push(ids)`
  - DDM: `PutDeclaration`, `PutSet`, `SetDeviceSets`, `GetStatus`
  - Verify the exact paths against the pinned NanoHUB tag in Etape 0, then freeze them in one constants file.
- `PlistWriter`: a shared, typed builder (XML plist, `SecurityElement.Escape`). Payload builders are unit-tested against the apple/device-management schema YAML (Release-v27.2) for keys, types and the supervised flags. The same schema data drives the "Kræver overvåget iPad" greying in the UI.
- `MdmEngine`: its own `BackgroundService` with a Channel kick (same pattern as IpadEngine), and a **broad catch around each loop iteration**, because .NET 10 StopHost would otherwise take down the whole server. Responsibilities:
  - reconcile the desired profiles and DDM sets per device;
  - inventory every 6 h (DeviceInformation + SecurityInfo; InstalledApplicationList every 24 h), coalesced into one push;
  - mark commands stale after 7 days;
  - APNs expiry warnings at 30/14/7/1 days (UI banner + `HomeAssistantBridge.Notify`);
  - "not seen for 48 h" alerts (configurable per Kind).

### 4.4 Webhook handling

- `POST /api/mdm/hook/{secret}`: AllowAnonymous, constant-time secret compare. If NanoHUB supports HMAC (verify; NanoMDM has `-webhook-hmac-key`), also verify `X-Hmac-Signature`. Never trust source IP: `UseForwardedHeaders` currently trusts every proxy. As part of Etape 1, restrict `KnownProxies` to the configured cloudflared/Caddy IPs.
- Exclude `/api/mdm/hook` from the Latin-1 rewrite middleware. Test with a fixture whose device name contains æøå.
- Topics:
  - `mdm.Authenticate`: create or update a `pending` enrollment.
  - `mdm.TokenUpdate`: set `enrolled`, queue the initial inventory and profile reconciliation, HA Notify "Enhed tilmeldt".
  - `mdm.Connect`: decode the base64 plist and match it to `MdmCommand` by `CommandUUID`; store the status, ErrorChain and parsed result (DeviceInformation → MdmEnrollment fields; InstalledApplicationList → MdmApp; ProfileList → MdmProfileState; ActivationLockBypassCode → encrypted).
  - `mdm.CheckOut`: set `checked_out` and send a high-priority HA Notify ("MDM fjernet fra …").
- Device ↔ row binding: ServerURL/CheckInURL carry `?fd=<per-device enrollment key>`. NanoMDM includes URL params in webhook events (verify the field name). The identity cert CN `fd-dev-<id>-…` is the fallback, and the serial is the last resort.
- The receiver only parses, persists, publishes `live.Changed("mdm")` and kicks MdmEngine. No slow work happens inline.

### 4.5 Command queue and push

- Fleetdeck builds the command plist with its own UUID, writes `MdmCommand(queued)`, then calls `Enqueue` (which pushes by default). User-initiated actions push immediately. Bulk and periodic work uses `?nopush=1` and then a single `Push`.
- `NotNow` is shown as "Venter – enheden er optaget/låst" and NanoHUB retries it. The NanoHUB repush interval stays at its default (24 h).
- Destructive commands (EraseDevice, ClearPasscode, EnableLostMode) need an extra step-up: password re-entry plus typing the device name (for erase). They are written to `audit.Log` and announced with HA Notify.

### 4.6 DDM for OS updates

- KMFDDM sets: `fd-kind-family`, `fd-kind-child`, and `fd-dev-<id>` for overrides. A device's sets are its Kind set plus its device set.
- Declarations:
  - `com.apple.configuration.management.status-subscriptions`: `device.operating-system.version`, `device.operating-system.build-version`, `softwareupdate.install-state`, `softwareupdate.pending-version`, `softwareupdate.failure-reason`, `device.power.battery-health`.
  - `com.apple.configuration.softwareupdate.enforcement.specific`: `TargetOSVersion`, `TargetLocalDateTime` = next install-window slot ≥ release + DeadlineDays, in the same timezone as IpadEngine (`TimeZoneInfo.Local`), `DetailsURL` optional.
  - `com.apple.configuration.softwareupdate.settings` (supervised devices only): automatic download/install and deferral days.
- "latest" is resolved from Apple's public feed `https://gdmf.apple.com/v2/pmv`, filtered to the device's model support. Verify the feed format and that it covers iPadOS. If it fails, fall back to manual version entry. Fleetdeck re-checks every 12 h and updates the declaration when a new version appears. The UI shows "Ny version 27.x – installeres senest <dato>".
- Status comes from KMFDDM's status endpoint per enrollment, polled after each DeclarativeManagement check-in (via webhook) and every 6 h.

### 4.7 Endpoints (new `Endpoints/MdmEndpoints.cs`; family/admin groups as in IpadEndpoints)

- Family (scope + permission checked):
  - `GET /api/devices/{id}/mdm` (info)
  - `POST /api/devices/{id}/mdm/refresh`
  - `POST /api/devices/{id}/mdm/lock` (body: message, phone)
  - `POST /api/devices/{id}/mdm/erase`
  - `POST /api/devices/{id}/mdm/restart`
  - `GET /api/devices/{id}/apps`
  - `GET|PUT /api/devices/{id}/updates`
  - `GET /api/devices/{id}/mdm/commands`
- Admin:
  - `POST /api/devices/{id}/mdm/enroll-link`
  - `GET /api/devices/{id}/mdm/enroll-profile` (authenticated download for Configurator)
  - `GET|PUT /api/mdm/settings`
  - `POST /api/mdm/pushcert/csr`
  - `POST /api/mdm/pushcert` (upload .pem)
  - `POST /api/mdm/pushcert/sign` (developer-account route)
  - `POST /api/mdm/ca`
  - `GET /api/mdm/backup-bundle`
  - `POST /api/devices/{id}/static-profiles` (export for Statisk tilstand)
  - lost mode, clear passcode, activation-lock code
- Anonymous: `GET /mdm-tilmelding/{token}` (single-use), `POST /api/mdm/hook/{secret}`.
- Keep the `/api/ipads/*` routes as aliases for one release.

### 4.8 DNS profile via MDM

- Wrap `IpadProfile.Build` in `InstallProfile`, with the new identifier `dk.fleetdeck.mdm.{ClientId}.dns` and stable PayloadUUIDs (hash of ClientId), so the engine only reinstalls when the content hash changes.
- On supervised devices set `ProhibitDisablement=true`.
- If ProfileList shows a manually installed `dk.fleetdeck.ipad.*`, prompt the parent to remove it, since two DNS payloads conflict.
- On iPadOS 27+, prefer DDM `network.dns-settings` once it has been verified with AdGuard DoH ClientID URLs. Until then, the legacy payload (deprecated, still functional) is used on all versions.

## 5. UI changes (web/src/pages/Ipads.tsx → Enheder)

- Device page tabs:
  - **Oversigt/Enhed:** model, OS, battery, storage, Overvåget ja/nej, last check-in, MDM status badge, Find My, last iCloud backup.
  - Regler, YouTube, Aktivitet: hidden when `DnsManaged=false`.
  - **Opdateringer:** current version → target, deadline, install window (WeekGrid), status timeline.
  - **Apps:** inventory with search; managed badge; "Installér gratis app" (App Store link/ID); "Skjul app" (supervised).
  - **Begrænsninger** (children): age rating, App Store, web filter, DNS lock; each control shows "Kræver overvåget iPad" when not available.
  - **Historik:** command log with status.
  - **Opsætning:** existing setup, plus the "Tilføj til MDM" wizard and enroll link/QR.
- Actions bar: Opdatér info, Lås, Genstart*, Mistet-tilstand* (admin), Ryd adgangskode (admin), Slet (danger zone). Items marked * are supervised only and shown greyed out with an explanation.
- Cards/QuickActions: MDM badge (Tilmeldt / Venter / Fjernet / Ikke set 48 t) and an update-pending dot.
- Settings: an "Apple-administration" wizard (§2) and a status panel (push cert expiry countdown, NanoHUB version, last webhook, CA thumbprint, Nødkopi date).
- Parent permissions mapping (`FamilyPerms`; enforced server-side with `sc.Sees(id) && sc.Can(perm)`; today only pause and rules are enforced, and the rest must be added in Etape 1):

| Perm | Covers |
|---|---|
| pause | AdGuard pause (unchanged) |
| rules | DNS rules, YouTube, restriction profiles, DNS profile install, app block/hide |
| update | Update policy, refresh info, Genstart (supervised) |
| lock | DeviceLock with message |
| erase | EraseDevice (not in Default; step-up required) |
| admin-only | Lost mode, location, clear passcode, activation-lock code, enroll/unenroll, certificates |
| future `apps` | Install/remove apps (split out of rules when the Apps tab grows) |

## 6. Staged delivery (etaper) with acceptance tests

**Etape 0: Afklaring og spike** (blocking; little code; the owner does the Apple steps)
- The owner gets a legitimate APNs route (§2.4), or decides to use Statisk tilstand only.
- Spike with a throwaway NanoHUB container, the Cloudflare hostname and one family iPad.
- Accept when: the push cert is uploaded; the iPad enrolls through Cloudflare with SignMessage plus an embedded PKCS#12; a DeviceInformation round-trip completes; the webhook JSON is captured as fixtures (Authenticate, TokenUpdate, Connect, CheckOut, DeclarativeManagement); the URL-param field, HMAC support and the exact API paths are confirmed; Cloudflare passes `Mdm-Signature` and large bodies, and there are no bot challenges (checked in Cloudflare's firewall events log).
- Configurator test on the family iPad: supervise, restore backup, confirm supervision is kept; record whether the MDM profile can be removed.

**Etape 1: Fundament + Statisk tilstand** (no devices needed)
- Ipad→MobileDevice migration, DnsManaged filter in IpadEngine, single-use tokens, KnownProxies, enforcement of the update/lock/erase permissions, MdmEngine skeleton with broad catch, PlistWriter, static profile export.
- Tests:
  - A migration test on a copy of production db: same ids, permissions intact, AdGuard client set unchanged (diff of upserts = 0).
  - Unit tests for payload builders against schema YAML.
  - A token reused twice returns 410.
  - A spoofed `X-Forwarded-For` is ignored.
  - A static DNS-lock profile installed via Configurator on a supervised iPad cannot be turned off. This is the only device test in this etape.

**Etape 2: Certifikater og tilmelding** (mostly no devices needed)
- Wizard steps 1–6, CSR/pem handling with topic lock, the developer-account signing step, device CA, enrollment profile, webhook receiver, NanoHUB template.
- Tests:
  - NanoHUB in docker with inmem storage in CI.
  - Webhook fixture replay populates MdmEnrollment correctly (including æøå names).
  - A wrong-topic upload is refused.
  - A mismatched cert/key is refused.
  - Generated profile: `plutil -lint` passes; its PKCS#12 chain verifies against the CA with openssl.
  - Optional: jessepeterson/mdmb device simulator enrolls against NanoHUB using the generated identity (verify mdmb supports embedded identity; otherwise use SCEP mode in test only).
  - Public check: `/api/v1/*` returns 404 and the Fleetdeck UI is unreachable on `mdm.<domain>`.

**Etape 3: Enhed + kommandoer** (real family iPad)
- Enhed tab, inventory schedule, Lock, Erase (tested on a spare or family iPad only with consent), Restart, command history, HA notifications, check-in alerts.
- Accept when: lock arrives within 30 s with its message shown; a NotNow command completes after unlock; battery, storage and OS match the Settings app; removing the MDM profile on the iPad gives "Fjernet" plus an HA notification within 1 min; a parent without `lock` gets 403.

**Etape 4: OS-opdateringer via DDM** (family iPads)
- Policies, gdmf resolver, declarations, status subscriptions, Opdateringer tab.
- Accept when: setting the target to an available version with a short deadline makes the iPad show Apple's "update required by" UI; the update installs in the install window; Fleetdeck shows status transitions and the final version; a bad target shows the failure reason. Without a device: unit tests for deadline/window computation (DST transitions in Europe/Copenhagen) and the gdmf parser against a recorded feed.

**Etape 5: Børne-iPads** (supervised)
- Supervise both children's iPads via the §2.2 wizard, MDM DNS profile with ProhibitDisablement, restrictions (no profile install, no erase, no account change, App Store according to policy), YouTube hidden via `app.settings` (27+) or `blockedAppBundleIDs`, web filter, activation-lock code escrow, lost mode.
- Accept when: the child cannot turn off DNS, install profiles, erase the device or see YouTube; AdGuard schedules still apply (existing tests pass); lost mode shows its message and location returns; the old manual DNS profile is detected and removed.

**Etape 6: Robusthed og generalitet**
- Renew wizards (push + MDM CSR cert) and an expiry drill (simulated by clock injection), Nødkopi plus a restore drill on a fresh container pair, the Direkte/Caddy topology, a docker-compose example, docs, image-digest update flow, and optionally ACME hardware-bound identity.
- Accept when: a full restore from the bundle onto empty volumes keeps all devices checking in without re-enrollment; a second test household is set up by following only the wizard, with no code edits.

## 7. Risks, privacy, renewal, genericity

**Risks**
- **Licensing (high):** the APNs route may not be available legitimately to a private household. Mitigation: the Etape 0 gate, and Statisk tilstand as a fallback that keeps its value on its own.
- **Removability:** without ADE the MDM profile can be removed. Mitigation: supervised restrictions, check-in alerts, and HA notification on CheckOut.
- **Erase recovery:** an erased device needs the Mac and Configurator again. Mitigation: `allowEraseContentAndSettings=false` on children's iPads, plus the activation-lock escrow.
- **Upstream:** NanoHUB is small (about 42 commits) and KMFDDM is "experimental". Mitigation: pin by digest; Fleetdeck holds all state; the only coupling is the `MdmClient` path constants plus the webhook parser; the fallback is separate NanoMDM + KMFDDM containers using the same APIs.
- **Apple changes:** legacy payloads are deprecated in 27 (DNS, app lists). Mitigation: a schema-driven builder with a per-OS choice of legacy payload or DDM; re-check every Apple schema release.
- **Server stability:** MDM code must never crash the host process (broad catch, separate service).

**Privacy**
- With Cloudflare, the edge sees MDM traffic decrypted: inventory, app lists, commands and the enrollment profile including the device key.
- Mitigations: path-restricted ingress; single-use, 30-min enrollment tokens; no admin UI on the public host; no PII in device names is required; Cloudflare does not log request bodies by default; the "Direkte" topology for households that want end-to-end TLS; the later ACME identity removes the key from the profile.
- The wizard states this plainly in Trin 2.
- Data minimisation: no location polling (location only on lost mode); the app inventory is kept for 90 days.

**Certificate renewal**
- **Push cert:** 365 days; renew on the **same entry with the same Apple ID** (Renew, not Create) using a freshly signed CSR. Fleetdeck enforces the topic match and warns at 30/14/7/1 days.
- The MDM CSR signing cert (developer route) also expires yearly and is tracked the same way.
- **Device identity certs:** 2-year life. Within 60 days of expiry, the engine re-issues a cert and delivers it via `InstallProfile` (a new PKCS#12 identity profile). It must verify that NanoMDM accepts the re-keyed identity for the same enrollment ID; if not, it re-enrolls via a profile replacement. Verify this in Etape 6.
- **Device CA:** 10 years.

**Genericity**
- No household values in code: domain, IPs, container names, organization name, timezone, Kinds and permissions all live in settings.
- Templates ship with placeholders.
- `Platform` allows Android later; it is out of scope now.
- All Danish UI strings sit in the existing i18n structure.
- Wizards detect Unraid (host agent present) versus plain Docker and show matching instructions.

## 8. Relation to the user's actual request (self-update from the UI)

- The sidecar adds a second container whose image must be updated. A UI-driven update mechanism would serve both: update Fleetdeck through a host-agent script job (fetch tag, build, `update_container`) and bump the NanoHUB digest.
- The self-update flow should therefore be built first and kept generic over "managed containers". That flow is what the user asked for in this turn.

## 9. Verify first (ordered)

1. A legitimate APNs route for this owner (Apple Developer Support answer, or a legal-entity decision).
2. NanoHUB v0.2.x: exact `/api/v1/nanomdm` and `/api/v1/ddm` paths, webhook HMAC support, and the URL-param field in webhook events.
3. Enrollment with an embedded PKCS#12 plus SignMessage through Cloudflare free (header pass-through, bot rules).
4. Configurator on iPadOS 26/27: supervision survives a backup restore; MDM profile removability on a supervised non-ADE device; Configurator "Enroll in MDM server" with Fleetdeck's URL.
5. gdmf.apple.com pmv feed format for iPadOS.
6. DDM `network.dns-settings` with AdGuard ClientID DoH URLs on 27.
7. Re-keying a device identity without re-enrollment.
8. ACME hardware-bound identity as the MDM identity without ADE (optional, Etape 6).

No files were written or changed. Repo referenced: the Fleetdeck repo (read-only, via the research).