// BIOS på maskinsiden, i guiderne og under Opdateringer: tekster og afledte tilstande (rene funktioner, testet i tests/bios.test.ts).
import type { BiosDiffRow, BiosFleetRow, BiosSnapshotReason, BiosStatus, BiosUpdateCheck, BiosUpdateRoute, BiosUpdateRun } from '../types';
import type { Shown, Tone } from './biosUpdateText';

export type { Shown, Tone };

/** Farven til BIOS-mærket (tæt på "Firmware", men sin egen). */
export const BIOS_COLOR = 'violet';
export const UPDATE_ALL_SKIPS_BIOS = 'Opdatér alt springer BIOS over.';
export const UPDATE_ALL_NEVER_BIOS = 'Opdatér alt tager aldrig BIOS med. Hver maskine opdateres for sig, og altid med backup først.';
export const USB_ONLY_LOCAL = 'USB-stik kan kun laves fra den pc, du sidder ved. Åbn Fleetdeck via genvejen på denne pc, eller hent pakken.';
export const ERASE_WARNING = 'Alt på USB-stikket bliver slettet.';

const VENDOR: Record<string, string> = { lenovo: 'Lenovo', dell: 'Dell', hp: 'HP' };
export const vendorName = (v: string | null | undefined) => (v ? VENDOR[v] ?? v : '–');

/** Antal versioner mellem den nuværende og den nyeste (changelog-listen indeholder kun nyere versioner). */
export const versionsBehind = (s: Pick<BiosStatus, 'changes' | 'state'>) => (s.state === 'newer' ? Math.max(s.changes.length, 1) : 0);

/** Linjen "Nyeste" på maskinsiden (design 4.1). */
export function biosStateLine(s: BiosStatus | null | undefined, catalogError?: string | null): Shown {
  if (!s) return { text: 'Ikke tjekket endnu. Agenten har ikke sendt BIOS-oplysninger.', tone: 'gray' };
  switch (s.state) {
    case 'newest': return { text: 'Nyeste version', tone: 'green' };
    case 'newer': {
      const n = versionsBehind(s);
      return { text: n > 1 ? `${n} versioner nyere` : 'Nyere findes', tone: s.critical ? 'red' : 'yellow' };
    }
    case 'uncertain': return { text: 'Modellen kunne ikke genkendes sikkert. Vi viser ingen opdatering.', tone: 'gray' };
    case 'unsupported': return { text: `Leverandøren understøttes ikke endnu${s.detail ? ` (${s.detail})` : ''}.`, tone: 'gray' };
    case 'unchecked': return { text: 'Ikke tjekket endnu. Agenten har ikke sendt BIOS-oplysninger.', tone: 'gray' };
    case 'catalog_error': return { text: catalogError ?? 'Leverandørens katalog kunne ikke hentes. Sidste kendte svar vises.', tone: 'yellow' };
    case 'virtual': return { text: 'Virtuel maskine. BIOS styres af værten.', tone: 'gray' };
    case 'mac': return { text: 'Firmware opdateres sammen med macOS.', tone: 'gray' };
    case 'pi': return { text: 'Bootloaderen opdateres under Firmware.', tone: 'gray' };
    case 'fwupd': return { text: 'Kan opdateres med fwupd under Opdateringer.', tone: 'blue' };
    default: return { text: s.state, tone: 'gray' };
  }
}

/** Mærket øverst i panelet og i flådetabellen. */
export function biosBadges(s: BiosStatus | null | undefined): { text: string; tone: Tone; important?: boolean }[] {
  if (!s) return [{ text: 'Ikke tjekket', tone: 'gray' }];
  const imp = s.changes.some((c) => c.lines.some((l) => l.important));
  switch (s.state) {
    case 'newer': {
      const n = versionsBehind(s);
      return [{ text: n > 1 ? `${n} versioner bagud` : 'Nyere findes', tone: 'yellow' }, ...(imp || s.critical ? [{ text: 'Vigtig', tone: 'red' as Tone, important: true }] : [])];
    }
    case 'newest': return [{ text: 'Nyeste', tone: 'green' }];
    case 'unsupported': return [{ text: 'Leverandør ikke understøttet', tone: 'gray' }];
    case 'uncertain': return [{ text: 'Model usikker', tone: 'gray' }];
    case 'unchecked': return [{ text: 'Ikke tjekket', tone: 'gray' }];
    case 'catalog_error': return [{ text: 'Katalog nede', tone: 'yellow' }];
    case 'virtual': return [{ text: 'Virtuel maskine', tone: 'gray' }];
    case 'mac': return [{ text: 'Opdateres med macOS', tone: 'gray' }];
    case 'pi': return [{ text: 'Bootloader under Firmware', tone: 'gray' }];
    case 'fwupd': return [{ text: 'fwupd', tone: 'blue' }];
    default: return [{ text: s.state, tone: 'gray' }];
  }
}

/**
 * Den fulde forklaring på et mærke (tooltip i flådetabellen): mærkerne vises altid i fuld længde, men tooltippen siger, hvad
 * "4 versioner bagud" dækker over (fra → til og de nyere versioner), og hvorfor en opdatering er "Vigtig".
 */
export function biosBadgeTip(s: BiosStatus | null | undefined, badge: { text: string; important?: boolean }): string {
  if (!s) return biosStateLine(s).text;
  if (badge.important) {
    const imp = importantChange(s);
    return imp ? `Vigtig: ${shortVersion(imp.version)} – ${imp.text}` : 'Leverandøren markerer opdateringen som kritisk.';
  }
  if (s.state === 'newer') {
    const versions = s.changes.map((c) => shortVersion(c.version));
    return `${s.current ?? '?'} → ${s.latest ?? '?'}` + (versions.length ? ` · nyere versioner: ${versions.join(', ')}` : '');
  }
  return biosStateLine(s).text;
}

/** Den første vigtige linje fra changelog'en (Secure Boot, CA/KEK, TPM, sikkerhed) – vises som advarsel øverst. */
export function importantChange(s: BiosStatus | null | undefined): { version: string; text: string } | null {
  for (const c of s?.changes ?? []) for (const l of c.lines) if (l.important) return { version: c.version, text: l.text };
  return null;
}

export const isSecureBootLine = (t: string) => /secure ?boot|\bKEK\b|\bCA 20\d\d|\bdb\b|\bdbx\b/i.test(t);

/** "79A" ud af "M1UKT79A" (eller hele versionen, hvis den ikke ligner Lenovos kode). */
export const shortVersion = (v: string) => (/^[A-Z0-9]{4}T([0-9A-F]{2}[A-Z])$/i.exec(v)?.[1] ?? v);

/** usb: USB-stikket kan bruges (Lenovo, ikke Mac) – den eneste vej, når der ingen vej er inde fra styresystemet. */
export function routeLabel(route: BiosUpdateRoute | null | undefined, vendor?: string | null, usb?: boolean): string {
  if (route === 'capsule') return 'UEFI-kapsel ved genstart';
  if (route === 'lenovo-exe') return 'Lenovos program';
  if (usb) return 'USB-stik';
  if (vendor === 'dell') return 'Dells program';
  if (vendor === 'hp') return 'HPs program';
  return '–';
}

/** Kapsel-vejen tilbydes kun, når pakken har en rigtig UEFI-kapsel; ellers serverens forklaring (fx rå ROM: kun USB-stik). */
export function routeLong(route: BiosUpdateRoute | null | undefined, why?: string | null): string {
  if (route === 'capsule') return "UEFI-kapsel, som BIOS'en installerer ved næste genstart. Pakken indeholder en rigtig UEFI-kapsel, så Secure Boot skal ikke slås fra.";
  if (route === 'lenovo-exe') return "Lenovos eget program inde fra Windows. BitLocker sættes på pause i to genstarter.";
  return why || USB_ONLY;
}

export const USB_ONLY = 'Kun med USB-stik: Secure Boot slås fra, mens der flashes, og til igen bagefter.';

export const SNAPSHOT_REASON: Record<BiosSnapshotReason, string> = {
  manual: 'Manuel', 'pre-update': 'Før opdatering', 'post-boot': 'Efter genstart', 'pre-restore': 'Før gendannelse', 'post-restore': 'Efter gendannelse',
};

// ------------------------------------------------------------------ flåden (fanen BIOS)

export type FleetFilter = 'behind' | 'all' | 'unchecked';

export function fleetCounts(rows: BiosFleetRow[]) {
  return {
    behind: rows.filter((r) => r.status?.state === 'newer').length,
    all: rows.length,
    unchecked: rows.filter((r) => !r.status || r.status.state === 'unchecked').length,
  };
}

export function fleetFilter(rows: BiosFleetRow[], f: FleetFilter, q: string): BiosFleetRow[] {
  const s = q.trim().toLowerCase();
  return rows.filter((r) => (f === 'all' || (f === 'behind' ? r.status?.state === 'newer' : !r.status || r.status.state === 'unchecked'))
    && (!s || `${r.machine} ${r.model ?? ''} ${r.status?.current ?? ''}`.toLowerCase().includes(s)));
}

/** Dem, der hverken er bagud eller ikke tjekket (til telefonens "N andre maskiner …"). */
export const fleetQuiet = (rows: BiosFleetRow[]) => rows.filter((r) => r.status && r.status.state !== 'newer' && r.status.state !== 'unchecked').length;

// ------------------------------------------------------------------ "Opdatér BIOS"-guiden

export const UPDATE_STEPS = ['Backup', 'Kontrol', 'Bekræft', 'Opdatering', 'Efter genstart'] as const;

/** Hvilket trin guiden skal stå på for en kørsel, der allerede er i gang (null = start forfra med backup). */
export function wizardStepFor(run: Pick<BiosUpdateRun, 'effective'> | null | undefined): number | null {
  if (!run) return null;
  switch (run.effective) {
    case 'staging': case 'staged': case 'rebooting': case 'awaiting_report': return 3;
    case 'updated': case 'rejected': case 'not_applied': case 'unknown': case 'no_report': case 'stale': return 4;
    case 'failed': return 4;
    default: return null;
  }
}

/** Kontrollerne i trin 2: serverens og agentens, uden dubletter (agentens vinder), de der stopper først. */
export function mergeChecks(server: BiosUpdateCheck[], agent: BiosUpdateCheck[] | null | undefined): BiosUpdateCheck[] {
  const by = new Map<string, BiosUpdateCheck>();
  for (const c of server) by.set(c.id, c);
  for (const c of agent ?? []) by.set(c.id, c);
  const rank = { fail: 0, warn: 1, ok: 2 } as const;
  return [...by.values()].sort((a, b) => rank[a.state] - rank[b.state]);
}

export const confirmTickText = (machine: string) => `Jeg ved, at ${machine} er væk i op til 10 minutter og ikke må slukkes imens.`;

export type TlState = 'done' | 'run' | 'wait' | 'bad';
export interface TlItem { state: TlState; text: string; sub?: string }

/** Minutter:sekunder siden et tidspunkt (til "BIOS'en installerer opdateringen … 2:41"). */
export function clock(since: string | null | undefined, now: number): string {
  if (!since) return '';
  const s = Math.max(0, Math.floor((now - new Date(since).getTime()) / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

export function offlineFor(a: string | null | undefined, b: string | null | undefined): string | null {
  if (!a || !b) return null;
  const s = Math.max(0, Math.round((new Date(b).getTime() - new Date(a).getTime()) / 1000));
  return s >= 60 ? `${Math.floor(s / 60)} min ${s % 60} s` : `${s} s`;
}

const hhmm = (t: string | null | undefined) => (t ? new Date(t).toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' }) : '');

/** Forløbet (trin 4 og 5) ud fra kørslen. */
export function updateTimeline(run: BiosUpdateRun, now: number, route: BiosUpdateRoute | null | undefined): TlItem[] {
  const e = run.effective;
  const after = ['updated', 'rejected', 'not_applied', 'unknown', 'no_report', 'stale', 'failed'].includes(e);
  const rebooted = after || e === 'awaiting_report';
  const staged = rebooted || e === 'staged' || e === 'rebooting';
  const capsule = route !== 'lenovo-exe';
  const items: TlItem[] = [
    { state: run.preSnapshotId || staged ? 'done' : e === 'staging' ? 'run' : 'wait', text: 'Backup af BIOS-indstillingerne', sub: run.preSnapshotId ? hhmm(run.createdAt) : undefined },
    { state: staged ? 'done' : e === 'staging' ? 'run' : e === 'failed' && !staged ? 'bad' : 'wait', text: capsule ? 'Pakke kontrolleret og kapsel lagt klar' : 'Pakke kontrolleret og opdateringen lagt klar', sub: run.stagedAt ? hhmm(run.stagedAt) : undefined },
    { state: rebooted || e === 'rebooting' ? 'done' : 'wait', text: 'Genstarter', sub: run.rebootRequestedAt ? hhmm(run.rebootRequestedAt) : e === 'staged' ? 'Venter på genstart' : undefined },
    {
      state: rebooted ? 'done' : e === 'rebooting' ? 'run' : 'wait', text: "BIOS'en installerer opdateringen",
      sub: e === 'rebooting' ? `Maskinen er offline. Det er normalt. ${clock(run.rebootRequestedAt, now)} · Fleetdeck venter op til 15 min.`
        : rebooted && offlineFor(run.rebootRequestedAt, run.report?.bootAt ?? run.finishedAt) ? `Offline i ${offlineFor(run.rebootRequestedAt, run.report?.bootAt ?? run.finishedAt)}` : undefined,
    },
    { state: after ? (e === 'no_report' || e === 'stale' ? 'bad' : 'done') : e === 'awaiting_report' ? 'run' : 'wait', text: 'Online igen', sub: e === 'no_report' ? 'Ingen melding fra agenten.' : undefined },
    {
      state: e === 'updated' ? 'done' : ['rejected', 'not_applied', 'unknown', 'failed'].includes(e) ? 'bad' : 'wait', text: 'Kontrol af version og Secure Boot',
      sub: e === 'updated' ? `Version ${run.targetVersion} ✓` : run.report?.currentVersion ? `Version ${run.report.currentVersion}` : undefined,
    },
    { state: e === 'updated' && run.canCompare ? 'done' : 'wait', text: 'Ny backup og sammenligning' },
  ];
  return items;
}

// ------------------------------------------------------------------ sammenlign og gendan

export function diffKind(r: Pick<BiosDiffRow, 'state' | 'rule' | 'valid'>): { label: string; tone: Tone | 'violet'; icon: 'check' | 'alert' | 'lock' | 'sparkles' | 'minus' } {
  if (r.state === 'new') return { label: 'Ny', tone: 'violet', icon: 'sparkles' };
  if (r.state === 'gone') return { label: 'Forsvundet', tone: 'gray', icon: 'minus' };
  if (r.rule === 'never') return { label: 'Gendannes ikke', tone: 'gray', icon: 'lock' };
  if (!r.valid) return { label: 'Ikke gyldig nu', tone: 'gray', icon: 'lock' };
  if (r.rule === 'ask') return { label: 'Kræver eget valg', tone: 'yellow', icon: 'alert' };
  return { label: 'Almindelig', tone: 'green', icon: 'check' };
}

/** Opsummeringen under listen: "2 valgt · 1 kræver eget valg · 1 gendannes ikke · 1 ny". */
export function diffSummary(rows: BiosDiffRow[], selected: Set<string>): { text: string; tone: Tone }[] {
  const ch = rows.filter((r) => r.state !== 'same');
  const ask = ch.filter((r) => r.state === 'changed' && r.selectable && r.rule === 'ask' && !selected.has(r.name)).length;
  const never = ch.filter((r) => r.state === 'changed' && !r.selectable).length;
  const added = ch.filter((r) => r.state === 'new').length;
  const gone = ch.filter((r) => r.state === 'gone').length;
  const o: { text: string; tone: Tone }[] = [{ text: `${selected.size} valgt`, tone: 'blue' }];
  if (ask) o.push({ text: `${ask} kræver eget valg`, tone: 'yellow' });
  if (never) o.push({ text: `${never} gendannes ikke`, tone: 'gray' });
  if (added) o.push({ text: `${added} ny`, tone: 'gray' });
  if (gone) o.push({ text: `${gone} forsvundet`, tone: 'gray' });
  return o;
}

export const restoreButton = (n: number) => (n === 1 ? 'Gendan 1 indstilling…' : `Gendan ${n} indstillinger…`);

// ------------------------------------------------------------------ USB-stikket

export const USB_STEPS = ['Pakke og backup', 'Denne pc', 'USB-stik', 'Bekræft', 'Skriver', 'Færdig'] as const;

export const onLocalhost = (hostname: string) => ['localhost', '127.0.0.1', '[::1]', '::1'].includes(hostname);

/** Adressen til Fleetdeck gennem agenten på denne pc (samme side, men på localhost). */
export const localUrl = (path: string, port: number = 8587) => `http://localhost:${port}${path.startsWith('/') ? path : `/${path}`}`;

/** Linket til siden på denne pc: altid i et nyt vindue. Vises Fleetdeck inde i en anden side (fx Kontrolrums iframe), ville
 *  localhost-siden ellers åbne I iframen, hvor den er tredjepart – så gemmer browseren ikke login-cookien, og login "holder ikke". */
export const localLink = (path: string, port?: number) => ({ href: localUrl(path, port), target: '_blank', rel: 'noopener' } as const);

export function bytes(n: number | null | undefined): string {
  if (n == null) return '–';
  const f = (v: number, u: string) => `${v.toLocaleString('da-DK', { maximumFractionDigits: v < 10 ? 1 : 0 })} ${u}`;
  if (n >= 1e9) return f(n / 1e9, 'GB');
  if (n >= 1e6) return f(n / 1e6, 'MB');
  if (n >= 1e3) return f(n / 1e3, 'kB');
  return `${n} B`;
}

export interface UsbDiskLike { vendor: string; model: string; size: number; serial: string; selectable: boolean; large: boolean; why?: string | null }
export const usbDiskName = (d: Pick<UsbDiskLike, 'vendor' | 'model' | 'size'>) => `${[d.vendor, d.model].filter(Boolean).join(' ') || 'USB-stik'} · ${bytes(d.size)}`;

/** Agentens tekst for en disk over grænsen (BiosUsb.TooLarge). */
export const usbTooLarge = (maxBytes: number) => `Disken er større end ${Math.floor(maxBytes / 1e9)} GB.`;

/**
 * Kan stikket vælges i guiden? Som BiosUsbDisk.Usable: store diske kun, når størrelsen er den eneste grund (en stor disk, der sad i
 * før guiden – fx en backupdisk – kan aldrig vælges).
 */
/** Agentens tekst for en ekstern harddisk/SSD (BiosUsb.ExternalDrive) – kræver den samme ekstra bekræftelse som en stor disk. */
export const USB_EXTERNAL_DRIVE = 'Det ligner en ekstern harddisk eller SSD, ikke et USB-stik.';

export const usbChoosable = (d: UsbDiskLike, maxBytes: number) => d.selectable || (d.large && (d.why === usbTooLarge(maxBytes) || d.why === USB_EXTERNAL_DRIVE));

/** Kan agenten på pc'en, guiden kører på, lave stikket? Linux (lsblk) og Mac (diskutil). */
export const USB_WRITER_PLATFORMS: readonly string[] = ['linux', 'windows', 'mac'];
export const usbWriterHere = (platform: string | null | undefined) => !!platform && USB_WRITER_PLATFORMS.includes(platform);

/** Kolonnen "Backup" i flådetabellen: "I dag 14:02", "3. nov." eller "Ingen". */
export function backupLabel(at: string | null | undefined, now = Date.now()): string {
  if (!at) return 'Ingen';
  const d = new Date(at);
  const n = new Date(now);
  if (d.toDateString() === n.toDateString()) return `I dag ${d.toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' }).replace('.', ':')}`;
  const day = `${d.getDate()}. ${d.toLocaleDateString('da-DK', { month: 'short' }).replace(/\.$/, '')}.`;
  return d.getFullYear() === n.getFullYear() ? day : `${day} ${d.getFullYear()}`;
}

/** Er backuppen frisk nok til guiden (under 24 timer)? */
export const backupFresh = (takenAt: string | null | undefined, now: number) => !!takenAt && now - new Date(takenAt).getTime() < 24 * 3600_000;
