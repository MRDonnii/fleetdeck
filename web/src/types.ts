// Typer der spejler serverens JSON (camelCase)

import type { TpmEncryption, TpmInfo } from './design/tpmText';
import type { NetBrief, NetworkInfo } from './design/networkText';
import type { BatteryInfo, DiskHealth } from './design/healthHistoryText';
export type RunAs = 'system' | 'user' | 'user_admin';
export type JobStatus = 'pending' | 'running' | 'success' | 'failed' | 'cancelled' | 'waiting_user' | 'expired' | 'restarting' | 'hung' | 'partial';
export type HealthState = 'ok' | 'warning' | 'critical' | 'unknown';
export type Severity = 'critical' | 'warning' | 'info';
export type PackageType = 'winget' | 'msstore' | 'file' | 'steam' | 'uri' | 'apt' | 'pkg' | 'snap' | 'flatpak' | 'brew' | 'mas';
export type Intent = 'required' | 'available' | 'uninstall';

export interface Me {
  id: number; username: string; role: 'admin' | 'viewer' | 'parent'; version: string; perms: string[];
  /** mustEnroll: politikken kræver totrinsbekræftelse, og brugeren har den ikke endnu (kun opsætningen er tilgængelig) */
  twoFactor: { enabled: boolean; required: boolean; mustEnroll: boolean };
}
export interface TwoFactorStatus { enabled: boolean; enabledAt: string | null; recoveryLeft: number; required: boolean; policy: TwoFactorPolicy }
export type TwoFactorPolicy = 'optional' | 'admins' | 'all';
export interface SecuritySettings { twofaPolicy: TwoFactorPolicy; trustedProxies: string; trustedProxiesDefault: string; clientIp: string; viaProxy: boolean }

export interface ComplianceSummary { total: number; ok: number; missing: number; failed: number; pending: number; unknown: number; pct: number }

export interface MachineRow {
  /** name = det viste navn (admin kan omdøbe), hostname = værtsnavnet fra agenten; renamed = har et selvvalgt navn */
  id: number; name: string; hostname: string; renamed: boolean; platform: 'windows' | 'linux' | 'mac'; online: boolean; lastSeen: string | null; onlineSince?: string | null; ip: string | null; mac: string | null; user: string | null;
  profileId: number | null; profile: string | null; tags: string[]; os: string | null; osVersion: string | null; model: string | null;
  /** Linux: distroens ID fra os-release (fx "linuxmint") */
  distro?: string | null;
  agentVersion: string | null; agentCurrent: boolean; compliance: ComplianceSummary; upgrades: number; jobsActive: number;
  healthScore: number | null; healthState: HealthState; cpu: number | null; ram: number | null; alerts: number;
  /** planlagt genstart/slukning */
  power?: { mode: 'reboot' | 'shutdown' | null; at: string } | null;
  /** jobbet, der kører lige nu */
  running?: { id: number; title: string; progress: number | null; step: string | null } | null;
  /** forsidens kort: fuldeste drev (%), CPU-temperatur, oppetid og fjernskrivebord */
  disk?: number | null; temp?: number | null; uptimeH?: number | null;
  /** hovedløse browsere (fx efterladt af automatisering) */
  headless?: number | null;
  /** primær netværksadapter: kablet/Wi-Fi, hastighed, signal */
  net?: NetBrief | null;
  remote?: { id: string; error?: string | null } | null;
  webUrl?: string | null;
  /** Agent-sikkerhed: replacesId != null = ny agent med et navn, der fandtes i forvejen (venter på godkendelse) */
  replacesId?: number | null; replacesName?: string | null;
  enrollMethod?: 'key' | 'code' | 'token' | 'deploy' | 'new' | 'approved' | string | null;
  jobTrust?: 'unpinned' | 'pinned' | 'mismatch' | null; trustError?: string | null; trustErrorAt?: string | null;
  transport?: 'http' | 'https' | 'socket' | null; tlsPinned?: boolean;
}

export interface VolumeInfo { drive: string; sizeGb: number; freeGb: number; pctUsed: number }
export interface LiveFacts {
  hostname?: string; user?: string; ip?: string; mac?: string; cpu?: number; ram?: number; ramUsedGb?: number; ramTotalGb?: number;
  uptimeH?: number; pendingReboot: boolean; powerMode?: string | null; powerAt?: string | null; keepAwake?: KeepAwakeState | null; temps: { cpu?: number; gpu?: number; source?: string }; volumes: VolumeInfo[];
}
/** Mac "Hold vågen på strøm": tilstanden lige nu fra agenten. */
export interface KeepAwakeState { setting: boolean; onAc?: boolean | null; state: 'held' | 'battery' | 'off' | 'user' | 'error'; error?: string | null; changedAt?: string | null; changedFrom?: string | null }
export type { DiskHealth } from './design/healthHistoryText';
export interface RemoteDesktopInfo { tool: string; id: string; error?: string | null }
export interface AmtInfo { present: boolean; version?: string | null; mode?: string | null; activated?: boolean; amtIp?: string | null; error?: string | null }
export interface HealthReport {
  remote?: RemoteDesktopInfo | null;
  amt?: AmtInfo | null;
  kept?: { name: string; active: boolean; state: string; restarts24h: number; lastRestart?: string | null; lastError?: string | null; skipped?: boolean }[] | null;
  live: LiveFacts; disks: DiskHealth[];
  defender?: { enabled?: boolean; realtime?: boolean; mode?: string; sigAgeDays?: number; quickScanAgeDays?: number; tamper?: boolean; threatsLast30Days: number; version?: string};
  antivirus: { name: string; enabled: boolean; upToDate: boolean }[];
  firewall: Record<string, boolean>; bitLocker?: string;
  events: { crashes7d?: number; errors24h?: number; lastCrash?: string };
  updates?: { pending?: number; titles: string[]; error?: string }; lastUpdate?: string;
  unraid?: UnraidInfo | null;
  /** diskkryptering (kun det, TPM-afsnittet bruger; resten hentes af krypteringspanelet) */
  encryption?: TpmEncryption | null;
  tpm?: TpmInfo | null;
  /** netværksadaptere (null = ældre agent) */
  network?: NetworkInfo | null;
  /** bærbare: batterihelbred (tom = intet batteri) */
  batteries?: BatteryInfo[] | null;
}
export interface SystemFacts {
  hostname?: string; os?: string; osVersion?: string; build?: string; edition?: string; installed?: string; lastBoot?: string;
  manufacturer?: string; model?: string; board?: string; bios?: string; cpuName?: string; cores?: number; threads?: number; ramGb?: number;
  gpus: string[]; nics: { name: string; status: string; speed?: string; mac?: string }[]; domain?: string; timeZone?: string;
  firmware?: string; secureBoot?: boolean; agentBuild?: string;
  /** BIOS-tjekket (nyere agenter). Ældre agenter sender kun `bios` ("VER (dato)"). Serienumre hentes ikke. */
  biosVendor?: string; biosVersion?: string; biosDate?: string; biosRelease?: string;
  productName?: string; productVersion?: string; sku?: string; boardVendor?: string; boardName?: string; isVirtual?: boolean;
  /** BIOS-indstillinger, agenten kan læse (null = ingen). */
  biosSettings?: BiosSettingsInfo | null;
}
export type BiosSettingsDriver = 'thinklmi' | 'dell-wmi-sysman' | 'hp-bioscfg' | 'lenovo-wmi';
export interface BiosSettingsInfo { driver: BiosSettingsDriver; count: number; adminPassword: boolean; saveMode?: string | null; pendingReboot?: boolean | null; writable: boolean }
export type BiosSnapshotReason = 'manual' | 'pre-update' | 'post-boot' | 'pre-restore' | 'post-restore';
export type BiosRuleClass = 'common' | 'ask' | 'never';
export interface BiosSettingItem { name: string; display?: string | null; type: string; value: string; possible?: string[] | null; min?: number | null; max?: number | null; rule?: BiosRuleClass | null }
/** GET /api/bios/snapshots/{id} (snapshot) og diff/check-svarene. */
export interface BiosSnapshotMeta {
  id: number; takenAt: string; reason: BiosSnapshotReason; driver: BiosSettingsDriver; biosVersion?: string | null; model?: string | null;
  count: number; adminPassword: boolean; pendingReboot?: boolean | null; by: string;
}
export interface BiosDiffRow {
  name: string; display?: string | null; before?: string | null; now?: string | null; state: 'same' | 'changed' | 'new' | 'gone';
  rule: BiosRuleClass; warn?: string | null; valid: boolean; selectable: boolean; preselected: boolean; why?: string | null;
}
/** GET /api/bios/machines/{id}/settings/diff */
export interface BiosDiff {
  from: BiosSnapshotMeta; to: BiosSnapshotMeta; rows: BiosDiffRow[];
  counts: { all: number; changed: number; preselected: number; ask: number; never: number; invalid: number; added: number; gone: number };
  canRestore: boolean; cannotRestore?: string | null; adminPassword: boolean;
}
/** POST /api/bios/machines/{id}/settings/restore – adgangskoden huskes ikke (kun i serverens hukommelse til det ene job). */
export interface BiosRestoreRequest { from: number; to?: number | null; names: string[]; acknowledged?: string[]; password?: string | null }
/** GET /api/bios/machines/{id}/settings/check: grøn = som forventet, gul = normaliseret, rød = afvist eller mangler. */
export interface BiosCheck {
  jobId: number; status: string; finished?: string | null; expected: number; snapshot: BiosSnapshotMeta | null;
  rows: { name: string; expected: string; actual?: string | null; color: 'green' | 'yellow' | 'red'; why?: string | null }[] | null;
}
/** BIOS-status for en maskine (GET /api/bios/machines/{id} og /api/bios/fleet). */
export type BiosState = 'newest' | 'newer' | 'uncertain' | 'unsupported' | 'unchecked' | 'catalog_error' | 'virtual' | 'mac' | 'pi' | 'fwupd';
export interface BiosChange { version: string; wu?: string | null; lines: { text: string; important: boolean }[] }
export interface BiosStatus {
  machineId: number; vendor: 'lenovo' | 'dell' | 'hp' | 'none'; catalogKey?: string | null; matchQuality?: 'exact' | 'fallback' | 'uncertain' | null;
  current?: string | null; currentNumeric?: string | null; currentDate?: string | null;
  latest?: string | null; latestNumeric?: string | null; latestDate?: string | null;
  state: BiosState; critical: boolean; changes: BiosChange[]; detail?: string | null; fwupdVersion?: string | null; checkedAt: string; source: string;
}
export interface BiosVendorStatus {
  vendor: string; name: string; supported: boolean; fetchedAt: string | null; lastAttempt: string | null; error: string | null; errorSince: string | null;
  models: number; machines: number; text: string;
}
/** route: vejen inde fra styresystemet, som serveren tilbyder (Linux kun med en rigtig UEFI-kapsel i pakken), ellers null. */
export interface BiosFleetRow { machineId: number; machine: string; platform: string; model?: string | null; status: BiosStatus | null; backupAt?: string | null; route?: BiosUpdateRoute | null }
export interface BiosPackageRow {
  id: number; vendor: string; catalogKey: string; version: string; kind: 'usb-zip' | 'win-exe' | 'bin' | 'readme'; fileName: string; size: number;
  sha256: string | null; shaSource: 'catalog' | 'tofu'; state: 'downloading' | 'ready' | 'failed'; error: string | null;
  created: string; downloadedAt: string | null; lastVerifiedAt: string | null; lastUsedAt: string | null; requestedBy: string;
  bytes: number; total: number; stale: boolean;
}
// BIOS-opdatering inde fra styresystemet (bios_update): kun på et tryk, aldrig i "Opdatér alt", altid med kopi af indstillingerne først
export type BiosUpdateRoute = 'capsule' | 'lenovo-exe';
export type BiosUpdateState = 'checking' | 'checked' | 'blocked' | 'staging' | 'staged' | 'updated' | 'rejected' | 'not_applied' | 'unknown' | 'failed';
/** Tilstanden som den skal vises nu (ud over BiosUpdateState): genstart bestilt, venter på melding, ingen melding, gået i stå. */
export type BiosUpdateEffective = BiosUpdateState | 'rebooting' | 'awaiting_report' | 'no_report' | 'stale';
export interface BiosUpdateCheck { id: string; state: 'ok' | 'warn' | 'fail'; text: string }
export interface BiosUpdateReport {
  runId: number; phase: 'check' | 'stage' | 'verify'; outcome: string; message: string; checks: BiosUpdateCheck[];
  currentVersion?: string | null; targetVersion?: string | null; deviceId?: string | null; versionRaw?: number | null; targetRaw?: number | null;
  fwupdState?: string | null; fwupdError?: string | null; esrtStatus?: number | null; esrtAttemptVersion?: number | null;
  bootOrderChanged?: boolean | null; stagedAt?: string | null; bootAt?: string | null;
}
export interface BiosUpdateRun {
  id: number; state: BiosUpdateState; effective: BiosUpdateEffective; route: BiosUpdateRoute; fromVersion?: string | null; targetVersion: string;
  message?: string | null; checks: BiosUpdateCheck[]; preSnapshotId?: number | null; jobId?: number | null; jobStatus?: JobStatus | null;
  createdAt: string; updatedAt: string; stagedAt?: string | null; rebootRequestedAt?: string | null; finishedAt?: string | null; createdBy: string;
  report?: BiosUpdateReport | null; canReboot: boolean; canVerify: boolean; canCompare: boolean;
}
/** GET /api/bios/machines/{id}/update */
export interface BiosUpdateInfo {
  route: BiosUpdateRoute | null; target?: string | null; current?: string | null; serverChecks: BiosUpdateCheck[]; canCheck: boolean; canStart: boolean;
  /** Hvorfor der ingen vej er inde fra styresystemet (fx Lenovos rå ROM på Linux: kun USB-stik). */
  routeWhy?: string | null;
  /** Linux: den hentede USB-pakke indeholder en rigtig UEFI-kapsel (true), en rå ROM (false) eller er ikke hentet (null). */
  capsule?: boolean | null;
  package?: { id: number; fileName: string; size: number; state: 'downloading' | 'ready' | 'failed'; shaSource: 'catalog' | 'tofu'; error?: string | null } | null;
  run: BiosUpdateRun | null;
}
/** POST /api/bios/machines/{id}/update/start: target skal være præcis den version, siden viste. */
export interface BiosUpdateStartRequest { confirm: boolean; target: string }
export interface JobBrief { id: number; type: string; status: JobStatus; created: string; finished: string | null }
export interface ComplianceItem { packageId: number; name: string; type: PackageType; category: string; intent: Intent; state: string; installed: boolean | null; lastJob: JobBrief | null }
export interface Software { name: string; version?: string; publisher?: string; scope?: string; description?: string | null; installPath?: string | null; exe?: string | null; iconHash?: string | null; canRemove?: boolean }
export interface Upgrade {
  name: string; wingetId: string; current?: string; available?: string; locked?: 'alle' | 'maskine' | 'profil' | null;
  /** En driver fra Windows Update ("Opdatér alt" tager dem ikke). */
  driver?: boolean;
  /** Hvorfor den ikke anbefales (fx gammel driver), ellers null. */
  advice?: string | null;
  /** Kan ikke installeres på maskinen (fx firmware, som maskinens firmware afviser) – forklaringen. Tæller ikke med. */
  blocked?: string | null;
}
export interface JobRow {
  id: number; machineId: number; machine?: string; type: string; title: string; status: JobStatus; source: string; runAs: RunAs;
  created: string; createdBy: string; started: string | null; finished: string | null; exitCode: number | null;
  progress?: number | null; step?: string; stepSince?: string | null; wingetId?: string | null;
}
export interface Insight { title: string; text: string; actions: string[] }
export interface JobDetail extends JobRow { output: string; params: Record<string, unknown>; insight: Insight | null }
export type FixRisk = 'safe' | 'confirm' | 'danger';
export interface AlertRow {
  id: number; machineId: number; machine: string; key: string; severity: Severity; title: string; detail: string;
  firstSeen: string; lastSeen: string; resolved: string | null; acknowledged: string | null; ackBy: string | null;
  /** why = hvad fejlen betyder; steps = "Sådan løser du det" (1-4 trin); what = trinene i én tekst (ældre visninger). */
  why: string | null; what: string | null; steps?: string[];
  fix: { id: string; title: string; risk: FixRisk; description?: string; button?: string } | null;
  fixJob: { id: number; status: JobStatus; finished: string | null; createdBy: string } | null; fixAttempts: number;
}
export interface HelperFix { id: string; title: string; description: string; risk: FixRisk; group: string; tool: boolean; autoAllowed: boolean; runAs: RunAs; platforms?: string[] }
export interface HelperStats { total: number; ok: number; failed: number; automatic: number; recent: { id: number; machineId: number; machine: string; title: string; status: JobStatus; created: string; createdBy: string }[] }
export interface MachineDetail extends MachineRow {
  facts: SystemFacts; health: HealthReport; notes: string; enrolled: string | null; lastInventory: string | null; lastHealth: string | null;
  /** Printere: dem fra Fleetdeck med status og maskinens egne. Null = platformen kan ikke (Unraid, iPad). */
  printers?: import('./design/printers').MachinePrinterRow[] | null;
  drives?: { id: number; host: string; share: string; letter?: string | null; name: string; fromProfile?: boolean; adopted?: boolean; state?: { ok: boolean; target: string; error?: string | null; lastOk?: string | null; since?: string | null } | null }[];
  complianceItems: ComplianceItem[]; software: Software[]; upgradeList: Upgrade[]; jobs: JobRow[]; alertList: AlertRow[]; serverAgentVersion: string;
  /** Skjult i Windows Update – kan vises igen. */
  hiddenUpgrades?: Upgrade[];
  /** Den seneste færdige "Opdatér alt" og det, den sprang over (null = ingen, eller en kører). */
  upgradeAll?: import('./design/upgradeSkipped').UpgradeAllResult | null;
  ignoredAlerts: { key: string; title: string; by: string; at: string; all: boolean }[];
  /** Kun Mac: indstillingen i Fleetdeck (sidste ændring vinder, fra web eller Mac'en) og tilstanden fra agenten. */
  keepAwake?: { on: boolean; at: string | null; source: 'web' | 'mac' | null; by: string | null; live: KeepAwakeState | null };
  maintenance: MaintenanceRun | null;
  /** Automatisk opdatering: slået fra i profilen, eller spærret (beskyttet maskine / "aldrig automatisk"). */
  autoUpdate?: { off: boolean; blocked: string | null; noProfile?: boolean };
  agentUpdate?: { blocked: boolean; requested: boolean };
}
export type MaintState = 'waiting' | 'running' | 'rebooting' | 'reboot_wait' | 'hung' | 'done' | 'partial' | 'failed' | 'offline' | 'skipped' | 'blocked' | 'superseded';
export interface UpdateRunReport {
  installed: string[]; failed: { title: string; code: string }[]; waiting: { title: string; reason: string }[];
  skipped: { title: string; reason: string }[]; remaining: string[]; rebootPending: boolean; error?: string | null;
}
export interface MaintenanceRun {
  id: number; created: string; finished: string | null; state: MaintState; summary: string; wasOffline: boolean; wakeSent: boolean;
  engine?: boolean; kind?: string; step?: string; round?: number; maxRounds?: number; reboots?: number; catchUp?: boolean;
  notBefore?: string; catchUpUntil?: string; nextAt?: string | null; stopReason?: string; phase?: string | null;
  currentJob?: { id: number; title: string; status: JobStatus; progress: number | null; step: string } | null;
  report?: UpdateRunReport | null;
}
export interface Metric { ts: string; cpu: number | null; ram: number | null; disk: number | null; cpuTemp: number | null; gpuTemp: number | null }

export interface Package {
  id: number; name: string; type: PackageType; identifier: string; args: string; runAs: RunAs; detect: string; category: string; notes: string;
  profiles: number; fileOk: boolean;
}
export interface Profile {
  id: number; name: string; description: string; enforce: boolean;
  upgradeMode: string; upgradeTime: string; upgradeDay: number; upgradeExclude: string[]; rebootPolicy: string;
  windowsUpdateMode: string; windowsUpdateTime: string; windowsUpdateDay: number;
  backupMode: string; backupTime: string; backupDay: number;
  provDrivers: boolean; provRestore: boolean; provAppearance: boolean; provWindowsUpdate: boolean; autoFix: string[];
  maintMode: string; maintTime: string; maintDay: number; maintWake: boolean; maintTasks: string[]; maintAfter: string; maintReport: boolean; nextMaint: string | null;
  updDrivers: boolean; updFirmware: boolean; updFeatureUpdates: boolean; updMaxRounds: number; updCatchUpDays: number;
  updNeverAuto: boolean; updRebootNoWarning: boolean; updQuietFrom: string; updQuietTo: string; autoUpdateOff?: boolean;
  keepRunning: string[]; kitPaths: string[]; kitMode: string; kitTime: string; kitDay: number; nextKit: string | null;
  machines: number; packageCount: number; isDefault: boolean; nextUpgrade: string | null; nextWindowsUpdate: string | null; nextBackup: string | null;
}
export interface ProfileDetail {
  profile: Profile;
  packages: { packageId: number; intent: Intent; name: string; type: PackageType; identifier: string; category: string }[];
  machineList: { id: number; name: string }[];
}
export interface Script { id: number; name: string; description: string; runAs: RunAs; body: string; created: string }
export interface Backup { name: string; date: string | null; computer: string | null; ageDays: number | null }
export interface Dashboard {
  machines: MachineRow[];
  totals: { machines: number; online: number; compliant: number; upgrades: number; healthy: number; jobsActive: number; jobsFailed24h: number; jobsOk24h: number };
  alerts: AlertRow[]; recentJobs: (JobRow & { machine: string })[]; backups: Backup[];
  running: (JobRow & { machine: string })[]; queued: (JobRow & { machine: string })[];
}
export interface WingetHit { id: string; name: string; version: string | null; inCatalog: boolean; perUser: boolean }
export interface WinSetupStatus {
  available: boolean; path: string; share: string;
  appearance?: { wallpaperFile?: string; wallpaperStyle?: string; captured?: string; capturedFrom?: string; lockScreen?: boolean } | null;
  drivers?: { count: number; date: string | null }; installers?: { name: string; sizeMb: number }[]; backups?: Backup[]; logs?: string[]; appsJson?: boolean;
}
export type SignedJobs = 'off' | 'sign' | 'enforce';
export interface AgentSecurity {
  mode: SignedJobs; jobKey: string; enrollKeyEnabled: boolean;
  accessRequests?: { mode: 'off' | 'lan' | 'on'; max: number; hours: number };
  https: { port: number | null; pin: string | null; selfSigned: boolean; error: string | null };
  tlsPin: string | null;
  machines: { total: number; pinned: number; unpinned: number; mismatch: number; old: number; https: number; tlsPinned: number; errors: number; pending: number };
}
export interface EnrollCode {
  id: number; hint: string; label: string; created: string; createdBy: string; expires: string; usedAt: string | null; profileId: number | null;
  replaceMachineId: number | null; replaceName: string | null; usedByMachineId: number | null; usedByName: string | null; state: 'active' | 'used' | 'expired';
}
/** "Anmod om adgang": en maskine uden nøgle, der beder om lov (GET /api/access-requests). */
export type AccessState = 'pending' | 'approved' | 'issued' | 'enrolled' | 'rejected' | 'expired' | 'key_sent' | 'superseded';
export interface AccessRequestRow {
  id: number; code: string; name: string; platform: string; os: string | null; model: string | null; requestedBy: string | null; message: string | null;
  ip: string; created: string; expires: string; lastPoll: string | null; state: AccessState; profileId: number | null; approvedName: string | null;
  decidedBy: string | null; decidedAt: string | null; rejectReason: string | null; machineId: number | null; machineName: string | null;
  pinVerified: boolean; conflict: string | null;
  /** Anmodningen kom over almindelig http (ingen TLS): koden binder stadig maskinens nøgle, men andre kan se anmodningen. */
  unencrypted: boolean;
  /** Maskinens MAC-adresser fra anmodningen ("AA:BB:…") – kan blokeres sammen med IP'en. */
  macs: string[];
  /** Det, en IP-blokering rammer: IPv4-adressen, IPv6 pr. /64 ("2001:db8:1:2::/64"), "socket" for serverens egen vært. */
  ipKey: string;
  /** IP'en er sandsynligvis delt (betroet proxy, routeren, går igen i andre anmodninger/maskiner): forklaringen – ellers null. */
  ipShared: string | null;
}
/** En blokering af "Anmod om adgang" (IP pr. /64 for IPv6, MAC eller maskinnavn), altid med udløb. */
export interface AccessBlockRow {
  id: number; kind: 'ip' | 'mac' | 'name'; value: string; label: string; created: string; until: string; createdBy: string; reason: string | null;
  accessRequestId: number | null; requestName: string | null; hits: number; lastHit: string | null;
}
export interface SentKey { code: string; link: string; linux: string; windows: string }
export interface NewEnrollCode { code: string; id: number; expires: string; linux: string; windows: string }
export type Settings = Record<string, string> & { hasSharePass?: boolean; enrollKey?: string; agentVersion?: string; agentAvailable?: boolean; version?: string };
export interface UserRow { id: number; username: string; role: 'admin' | 'viewer' | 'parent'; created: string; ipadAll: boolean; ipads: number[]; perms: string[]; lastLogin: string | null; twoFactor: boolean }

export type IpadMode = 'free' | 'school' | 'bed' | 'pause';
export type YtMode = 'open' | 'moderate' | 'strict' | 'kids' | 'blocked';
export type KidsMode = 'allowed' | 'blocked';
export type DeviceType = 'ipad' | 'iphone' | 'appletv' | 'android';
export interface IpadRow {
  id: number; name: string; kind: 'child' | 'family'; deviceType: DeviceType; owner: string | null; model: string | null; lanIp: string | null; lastSeen: string | null; applyError: string | null; applied: boolean;
  mode: IpadMode; until: string | null; nextChange: string | null; nextMode: IpadMode; extra: boolean;
  yt: YtMode | 'window' | 'quota' | 'school'; ytMode: YtMode; ytUsed: number; ytLimit: number;
  kids: KidsMode | 'window' | 'quota' | 'school'; kidsMode: KidsMode; kidsLinked: boolean; kidsUsed: number; kidsLimit: number; minutesToday: number; blockedServices: string[];
  /** Apps med en nyere version i App Store. */
  outdatedApps?: number;
  control: IpadControl;
}
/** Om styringen bruges: active, stale (ikke set i et stykke tid) eller missing (profil ikke installeret / ikke set). */
export interface IpadControl {
  state: 'active' | 'stale' | 'missing'; seen: string | null; lanSeen: string | null; watch: boolean;
  alertSince: string | null; alertText: string | null; snoozeUntil: string | null; hours: number;
}
/** Et trin i tjeklisten. source: auto = registreret af Fleetdeck, manual = markeret af en voksen (ikke kontrolleret). */
export interface IpadSetupStep {
  key: string; title: string; lines: string[]; note: string | null; links: { label: string; url: string }[]; auto: boolean; done: boolean; source: 'auto' | 'manual' | null;
}
export interface IpadList { can: string[]; admin: boolean; error: string | null; watchHeld?: boolean; ipads: IpadRow[]; /** Typer, brugeren har enheder af. */ types?: DeviceType[] }
export interface IpadDetail {
  row: IpadRow; schedule: string; blockedServices: string[]; allowedDomains: string[]; blockedDomains: string[]; filterAdult: boolean; safeSearch: boolean;
  ytMode: YtMode; ytWindows: string | null; ytDailyMin: number; can: string[];
  kidsMode: KidsMode; kidsLinked: boolean; kidsWindows: string | null; kidsDailyMin: number;
  hiddenApps: string[]; allowAppInstall: boolean; allowInAppPurchases: boolean; allowAccountChanges: boolean; ageRatingApps: number | null;
  locked: IpadLocked;
  setup: IpadSetupStep[];
}
/** Statisk tilstand: den låste profil og den linkede enhed. */
export interface IpadLocked {
  version: number; applied: number | null; supervised: boolean; ecid: string | null; serial: string | null; osVersion: string | null; deviceSeen: string | null;
  dns: boolean; identifier: string;
}
export interface IosDevice {
  ecid: string; name: string | null; deviceType: string | null; deviceClass: string | null; serial: string | null; firmware: string | null;
  supervised: boolean | null; paired: boolean | null; battery: number | null; charging: boolean | null; passcode: boolean | null;
  profiles: { identifier: string; name: string | null; version: number | null }[] | null; ipadId: number | null; ipadName: string | null;
}
export interface IosMac {
  id: number; name: string; online: boolean; lastSeen: string | null; devicesAt: string | null; devices: IosDevice[];
  job: { id: number; title: string; action: string | null; order?: string | null; ipadId: number | null; status: string; created: string; finished: string | null; step: string; progress: number | null; stepSince: string | null; output: string } | null;
}
/** iPad-backup i Fleetdeck (sendt fra Mac'en, i dele). */
export interface IpadBackupSet {
  set: string; created: string; complete: boolean; parts: number; bytes: number; encrypted: boolean; passwordVerified: boolean | null;
  deviceName: string | null; firmware: string | null; machine: string | null;
}
/** Husstandens overvågningsidentitet (kun certifikatets oplysninger). */
export interface IpadSupervisionInfo {
  organization: string; identity: { subject: string; notAfter: string; thumbprint: string } | null; backupPassword: boolean;
  /** "iPad via USB"s gamle identitet (fra før Mac og USB fik én fælles), hvis den er en anden end husstandens. */
  previousUsbIdentity?: string | null;
}
/** "Frigiv iPad" (Opsætning): hvad der kan bruges lige nu. */
export interface IpadReleaseInfo {
  kind: 'child' | 'family'; supervised: boolean; staticLocked: boolean; lockedApplied: number | null;
  mdm: { state: string; locked: boolean } | null;
  macs: { id: number; name: string; online: boolean; seen?: string | null }[];
  usb: { machineId: number; name: string; udid: string; supervised?: boolean | null }[];
  job: { id: number; title: string; action: string; status: string; step?: string | null; progress?: number | null; created: string; finished?: string | null; output: string } | null;
}
/** iPad via USB: en iPad i kablet på en pc med udvidelsen "iPad via USB". */
export interface UsbDevice {
  udid: string; ecid: string | null; name: string | null; deviceType: string | null; serial: string | null; firmware: string | null;
  supervised: boolean | null; paired: boolean | null; battery: number | null; charging: boolean | null; activation: string | null;
  profiles: { identifier: string; name: string | null; version: number | null }[] | null; ipadId: number | null; ipadName: string | null;
}
export interface UsbMachine {
  id: number; name: string; platform: string; online: boolean; ready: boolean; problem: string | null; at: string; devices: UsbDevice[];
  job: { id: number; title: string; action: string | null; ipadId: number | null; udid: string | null; status: string; created: string; finished: string | null; step: string; progress: number | null; stepSince: string | null; output: string } | null;
}
/** "Overvåg uden datatab" (Opsætning): backupperne fra Mac og pc, vejene og det seneste job. */
export interface NoWipeBackup {
  store: 'mac' | 'usb'; set: string; created: string; bytes: number; parts: number; encrypted: boolean | null; verified: boolean | null;
  sourceEmpty: boolean | null; deviceName: string | null; pinned: boolean; ineligible: string | null;
}
export interface NoWipeInfo {
  name: string; linked: boolean; backups: NoWipeBackup[];
  macs: { id: number; name: string; online: boolean; seen: boolean }[];
  usb: { machineId: number; name: string; platform: string; online: boolean; udid: string; deviceName: string | null; supervised: boolean | null; firmware: string | null }[];
  job: { id: number; title: string; status: string; created: string; finished: string | null; step: string; progress: number | null; stepSince: string | null;
    method: string; freshBackup: boolean; route: 'mac' | 'usb'; output: string } | null;
}
export interface UsbBackup { set: string; parts: number; bytes: number; created: string; machine: string | null; deviceName: string | null }
export interface AgService { id: string; name: string; group: string; icon: string | null }
export interface IpadActivity { days: { day: string; total: number; services: { id: string; minutes: number }[] }[]; timeline: { slot: number; minutes: number }[] }
export interface FamilySettings { watchHours: number; adguardUrl: string; adguardUser: string; hasPass: boolean; dohHost: string; schoolGroups: string; lastError: string | null; lastRun: string | null }
export interface AuditRow { id: number; ts: string; user: string; action: string; detail: string }

export interface UnraidDisk { name: string; type: string; device?: string | null; status: string; tempC?: number | null; sizeGb?: number | null; usedGb?: number | null; fsType?: string | null; ssd?: boolean | null }
export interface UnraidContainer { name: string; image: string; state: string; started?: string | null; icon?: string | null; webUi?: string | null; updateAvailable: boolean; autostart: boolean; cpu?: number | null; memMb?: number | null; network?: string | null; ip?: string | null }
export interface UnraidVm { name: string; state: string; vcpus?: number | null; memoryMb?: number | null; autostart: boolean }
export interface UnraidInfo { version: string; arrayState: string; syncErrors?: number | null; parityLast?: string | null; parityProgress?: number | null; disks: UnraidDisk[]; containers: UnraidContainer[]; vms: UnraidVm[]; collected: string }
/** Apps på iPad'en (Apps-fanen). */
export interface IpadApp {
  bundleId: string; name: string; version: string | null; build: string | null; outdated: boolean; seen: string;
  storeVersion: string | null; storeName: string | null; iconUrl: string | null; storeUrl: string | null; released: string | null;
  store: 'pending' | 'found' | 'missing' | 'error'; storeChecked: string | null;
  hidden: boolean; service: string | null; serviceName: string | null; serviceBlocked: boolean;
}
export interface IpadAppsInfo {
  scannedAt: string | null; source: 'mac' | 'mdm' | null; error: string | null; requested: string | null; requestedVia: 'mac' | 'mdm' | null;
  ecid: string | null; mdm: boolean; mdmPending: boolean;
  macs: { id: number; name: string; online: boolean; seenAt: string | null }[];
  job: { id: number; title: string; status: string; created: string; finished: string | null; progress: number | null; step: string; stepSince: string | null } | null;
  outdated: number; pendingLookups: number; apps: IpadApp[]; can: string[];
}
