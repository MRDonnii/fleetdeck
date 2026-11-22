// Diskhelbred, batteri, tidslinje og ændringslog på Helbred-fanen: typer og ren logik (testes med npm test).

export type Tone = 'green' | 'yellow' | 'orange' | 'red' | 'gray' | 'blue';
export interface Shown { text: string; tone: Tone }

// ------------------------------------------------------------------ diske
export interface DiskHealth {
  name: string; media?: string | null; bus?: string | null; sizeGb?: number | null; health?: string | null; status?: string | null;
  wear?: number | null; temp?: number | null; readErrors?: number | null; powerOnHours?: number | null;
  id?: string | null; kind?: string | null; state?: string | null; lifeLeft?: number | null; reallocated?: number | null; pending?: number | null;
  mediaErrors?: number | null; criticalWarning?: number | null; spare?: number | null; tempMax?: number | null; source?: string | null; standby?: boolean;
  reallocatedFirst?: number | null; reallocatedRoseAt?: string | null; mediaErrorsRoseAt?: string | null;
}

const has = (v: number | null | undefined): v is number => v !== null && v !== undefined;
export const dk = (v: number, d = 0) => v.toLocaleString('da-DK', { minimumFractionDigits: d, maximumFractionDigits: d });

export const DISK_STATE: Record<string, Shown> = {
  ok: { text: 'OK', tone: 'green' }, warning: { text: 'Advarsel', tone: 'yellow' }, failing: { text: 'Fejl', tone: 'red' },
};

/** Samlet vurdering (serverens state; ældre data: ud fra SMART-helbredet). */
export function diskState(d: DiskHealth): Shown {
  if (d.state && DISK_STATE[d.state]) return DISK_STATE[d.state];
  const h = d.health?.toLowerCase();
  if (h === 'healthy') return DISK_STATE.ok;
  if (h === 'warning') return DISK_STATE.warning;
  if (h === 'unhealthy' || h === 'failing') return DISK_STATE.failing;
  return { text: 'Ukendt', tone: 'gray' };
}

/** NVMe, SSD eller HDD (ældre data: medie og bus). */
export function diskKind(d: DiskHealth): string {
  if (d.kind) return d.kind;
  if (d.bus?.toLowerCase() === 'nvme') return 'NVMe';
  return d.media && d.media !== '?' ? d.media : 'Disk';
}

export function diskSize(gb?: number | null): string {
  if (!has(gb) || gb <= 0) return '';
  return gb >= 1000 ? `${dk(gb / 1000, gb % 1000 === 0 ? 0 : 1)} TB` : `${dk(gb)} GB`;
}

/** Resterende levetid: procent og farve (SSD/NVMe). Null for HDD eller ukendt. */
export function diskLife(d: DiskHealth): { pct: number; text: string; tone: Tone } | null {
  const left = has(d.lifeLeft) ? d.lifeLeft : has(d.wear) && diskKind(d) !== 'HDD' ? Math.max(0, 100 - d.wear) : null;
  if (left === null) return null;
  const pct = Math.max(0, Math.min(100, left));
  return { pct, text: `${dk(pct)} % tilbage`, tone: pct <= 10 ? 'red' : pct <= 20 ? 'orange' : pct <= 40 ? 'yellow' : 'green' };
}

/** Driftstid i timer og som år/dage. */
export function powerOn(h?: number | null): string {
  if (!has(h)) return '–';
  const days = h / 24;
  const human = days >= 365 ? `${dk(days / 365, 1)} år` : days >= 2 ? `${dk(days)} dage` : `${dk(h)} t`;
  return days >= 2 ? `${dk(h)} t (${human})` : human;
}

export function diskTemp(d: DiskHealth, limit = 70): (Shown & { max?: string }) | null {
  if (!has(d.temp)) return null;
  return { text: `${dk(d.temp)} °C`, tone: d.temp >= limit ? 'red' : d.temp >= limit - 10 ? 'yellow' : 'green', max: has(d.tempMax) ? `maks. ${dk(d.tempMax)} °C` : undefined };
}

const NVME_BITS: [number, string][] = [[1, 'få reserveblokke tilbage'], [2, 'temperatur uden for grænsen'], [4, 'pålideligheden er nedsat'], [8, 'skrivebeskyttet'], [16, 'backup-hukommelsen svigter']];

/** Det, man skal lægge mærke til: sektorer, fejl, NVMe-advarsler, sovende disk. Tone afgør farven. */
export function diskNotes(d: DiskHealth, now = new Date()): Shown[] {
  const out: Shown[] = [];
  const week = 7 * 86400_000;
  const rose = (at?: string | null) => !!at && now.getTime() - new Date(at).getTime() < week;
  if (d.health && !['healthy', 'unknown'].includes(d.health.toLowerCase())) out.push({ text: `SMART: ${d.status ?? d.health}`, tone: diskState(d).tone });
  if (has(d.criticalWarning) && d.criticalWarning > 0) {
    const bits = NVME_BITS.filter(([b]) => (d.criticalWarning! & b) !== 0).map(([, t]) => t);
    out.push({ text: `NVMe-advarsel: ${bits.join(', ') || d.criticalWarning}`, tone: (d.criticalWarning & 12) !== 0 ? 'red' : 'yellow' });
  }
  if (has(d.pending) && d.pending > 0) out.push({ text: `${dk(d.pending)} ulæselige sektorer venter`, tone: 'yellow' });
  if (has(d.reallocated) && d.reallocated > 0) {
    const was = has(d.reallocatedFirst) && d.reallocatedFirst < d.reallocated ? ` (var ${dk(d.reallocatedFirst)})` : '';
    out.push({ text: `${dk(d.reallocated)} reallokerede sektorer${was}${rose(d.reallocatedRoseAt) ? ' – stiger' : ''}`, tone: rose(d.reallocatedRoseAt) ? 'yellow' : 'gray' });
  }
  if (has(d.mediaErrors) && d.mediaErrors > 0) out.push({ text: `${dk(d.mediaErrors)} mediefejl${rose(d.mediaErrorsRoseAt) ? ' – stiger' : ''}`, tone: rose(d.mediaErrorsRoseAt) ? 'yellow' : 'gray' });
  if (d.standby) out.push({ text: 'Disken sov – tallene er fra sidste gang', tone: 'gray' });
  return out;
}

export const DISK_SOURCE: Record<string, string> = { smartctl: 'smartctl', wmi: 'Windows (Storage)', diskutil: 'diskutil', sysfs: 'kun systeminfo – SMART-værktøjet mangler' };

// ------------------------------------------------------------------ batteri
export interface BatteryInfo {
  name: string; manufacturer?: string | null; chemistry?: string | null; design?: number | null; full?: number | null; unit?: string | null;
  healthPct?: number | null; cycles?: number | null; status?: string | null; chargePct?: number | null; condition?: string | null; source?: string | null;
}

/** Batteriets helbred (fuld opladning i forhold til som nyt). Under grænsen = rød, lidt over = gul. */
export function batteryHealth(b: BatteryInfo, limit = 60): Shown {
  if (b.condition && /service|replace|poor|check/i.test(b.condition)) return { text: has(b.healthPct) ? `${dk(b.healthPct)} %` : 'Service', tone: 'red' };
  if (!has(b.healthPct)) return { text: 'Ukendt', tone: 'gray' };
  return { text: `${dk(b.healthPct)} %`, tone: b.healthPct < limit ? 'red' : b.healthPct < limit + 15 ? 'yellow' : 'green' };
}

export const BATTERY_STATUS: Record<string, string> = { charging: 'Lader', discharging: 'På batteri', full: 'Fuldt opladet', ac: 'På strøm (lader ikke)' };

export function batteryNow(b: BatteryInfo): string {
  const s = b.status ? BATTERY_STATUS[b.status] ?? b.status : null;
  const c = has(b.chargePct) ? `${dk(b.chargePct)} %` : null;
  return [c, s].filter(Boolean).join(' · ') || '–';
}

export function batteryCapacity(b: BatteryInfo): string {
  if (!has(b.design)) return '–';
  return `${has(b.full) ? dk(b.full) : '?'} af ${dk(b.design)} ${b.unit ?? ''}`.trim();
}

// ------------------------------------------------------------------ tidslinje
export type SegState = 'online' | 'offline' | 'unknown';
export type MarkerKind = 'boot' | 'update' | 'event' | 'change';
export interface TimelineSegment { from: string; to: string; state: SegState }
export interface TimelineMarker { time: string; kind: MarkerKind; severity: 'info' | 'warning' | 'critical'; title: string; detail?: string | null; ref?: string | null; count: number }
export interface TimelineView {
  from: string; to: string; segments: TimelineSegment[]; markers: TimelineMarker[]; onlinePct?: number | null; knownSince?: string | null;
  boots: number; updates: number; events: number; changes: number; offlineCount: number;
}

export const TIMELINE_RANGES = [{ value: '1', label: '24 t' }, { value: '7', label: '7 dage' }, { value: '30', label: '30 dage' }] as const;

/** Statusbåndets farver: Mantines egne variabler, så de passer i både lyst og mørkt tema. */
export const SEG_STYLE: Record<SegState, { label: string; color: string }> = {
  online: { label: 'Online', color: 'var(--mantine-color-green-6)' },
  offline: { label: 'Offline', color: 'var(--mantine-color-red-6)' },
  unknown: { label: 'Ukendt (før tidslinjen begyndte)', color: 'var(--mantine-color-gray-5)' },
};

/** Markørernes rækker (øverst de alvorligste) – hver type sin egen række, så de ikke dækker hinanden. */
export const LANES: { kind: MarkerKind; label: string; color: string }[] = [
  { kind: 'event', label: 'Hændelser', color: 'var(--mantine-color-orange-6)' },
  { kind: 'update', label: 'Opdateringer', color: 'var(--mantine-color-blue-6)' },
  { kind: 'change', label: 'Ændringer', color: 'var(--mantine-color-grape-5)' },
  { kind: 'boot', label: 'Opstarter', color: 'var(--mantine-color-teal-5)' },
];

export function markerColor(m: Pick<TimelineMarker, 'kind' | 'severity'>): string {
  if (m.kind === 'event') return m.severity === 'critical' ? 'var(--mantine-color-red-6)' : 'var(--mantine-color-orange-6)';
  return LANES.find((l) => l.kind === m.kind)?.color ?? 'var(--mantine-color-gray-5)';
}

/** Placering i procent af bredden (0–100), uden for intervallet klemmes til kanten. */
export function pos(t: string | number | Date, from: string, to: string): number {
  const a = new Date(from).getTime(); const b = new Date(to).getTime(); const x = new Date(t).getTime();
  if (!(b > a)) return 0;
  return Math.max(0, Math.min(100, ((x - a) / (b - a)) * 100));
}

/** Aksens mærker: hver 4. time (24 t), hver dag (7 dage) eller hver 5. dag (30 dage), i dansk tid. Smal skærm: færre og kortere. */
export function ticks(from: string, to: string, days: number, compact = false): { pct: number; label: string }[] {
  const a = new Date(from); const b = new Date(to);
  const out: { pct: number; label: string }[] = [];
  const t = new Date(a);
  if (days <= 1) {
    const step = compact ? 6 : 4;
    t.setMinutes(0, 0, 0); t.setHours(Math.ceil((t.getHours() + (a.getMinutes() || a.getSeconds() ? 1 : 0)) / step) * step);
    for (; t <= b; t.setHours(t.getHours() + step)) out.push({ pct: pos(t, from, to), label: `${String(t.getHours()).padStart(2, '0')}:00` });
  } else {
    t.setHours(0, 0, 0, 0); t.setDate(t.getDate() + 1);
    const step = days <= 7 ? 1 : compact ? 10 : 5;
    for (; t <= b; t.setDate(t.getDate() + step))
      out.push({ pct: pos(t, from, to), label: compact ? `${t.getDate()}/${t.getMonth() + 1}` : days <= 7 ? t.toLocaleDateString('da-DK', { weekday: 'short', day: 'numeric' }) : t.toLocaleDateString('da-DK', { day: 'numeric', month: 'short' }) });
  }
  return out.filter((x) => x.pct > 0 && x.pct < 100);
}

/** Kort varighed: "3 t 20 min", "2 d 4 t". */
export function span(from: string, to: string): string {
  const m = Math.max(0, Math.round((new Date(to).getTime() - new Date(from).getTime()) / 60000));
  if (m < 60) return `${m} min`;
  if (m < 1440) return `${Math.floor(m / 60)} t${m % 60 ? ` ${m % 60} min` : ''}`;
  return `${Math.floor(m / 1440)} d${Math.floor((m % 1440) / 60) ? ` ${Math.floor((m % 1440) / 60)} t` : ''}`;
}

/** Oppetidens farve (kun den kendte tid tæller). */
export function uptimeTone(pct?: number | null): Tone {
  if (!has(pct)) return 'gray';
  return pct >= 99 ? 'green' : pct >= 90 ? 'yellow' : 'orange';
}

// ------------------------------------------------------------------ ændringslog
export type ChangeAction = 'installed' | 'removed' | 'updated';
export interface ChangeRow { id: number; time: string; kind: 'program' | 'driver'; action: ChangeAction; name: string; from?: string | null; to?: string | null; publisher?: string | null }
export interface ChangeLog { days: number; total: number; counts: Partial<Record<ChangeAction, number>>; entries: ChangeRow[]; drivers: number; tracking?: string | null }

export const ACTIONS: Record<ChangeAction, { label: string; color: Tone; sign: string }> = {
  installed: { label: 'Installeret', color: 'green', sign: '+' },
  updated: { label: 'Opdateret', color: 'blue', sign: '↑' },
  removed: { label: 'Fjernet', color: 'red', sign: '−' },
};

export function versionText(r: Pick<ChangeRow, 'action' | 'from' | 'to'>): string {
  if (r.action === 'updated') return `${r.from ?? '?'} → ${r.to ?? '?'}`;
  return (r.action === 'installed' ? r.to : r.from) ?? '';
}

/** Rækkerne samlet pr. dag (nyeste først), med dagens overskrift på dansk. */
export function byDay(rows: ChangeRow[], now = new Date()): { day: string; label: string; rows: ChangeRow[] }[] {
  const key = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  const today = key(now); const y = new Date(now); y.setDate(y.getDate() - 1); const yesterday = key(y);
  const map = new Map<string, ChangeRow[]>();
  for (const r of [...rows].sort((a, b) => b.time.localeCompare(a.time))) {
    const k = key(new Date(r.time));
    if (!map.has(k)) map.set(k, []);
    map.get(k)!.push(r);
  }
  return [...map.entries()].map(([day, list]) => ({
    day, rows: list,
    label: day === today ? 'I dag' : day === yesterday ? 'I går' : new Date(list[0].time).toLocaleDateString('da-DK', { weekday: 'long', day: 'numeric', month: 'long' }),
  }));
}
