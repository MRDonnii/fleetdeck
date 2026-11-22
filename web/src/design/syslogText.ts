// Fejlloggen: typer, danske forklaringer på kendte hændelser, filtre og live-rammer.
// Ren logik uden React, så den kan testes med node (web/tests/syslog.test.ts).

export type SysLevel = 'critical' | 'error' | 'warning' | 'info';

/** Ens hændelser samlet: antal, første og seneste gang, og den seneste besked. */
export interface SysLogGroup {
  key: string; level: SysLevel; log: string; source: string; eventId?: number | null; kind?: string | null;
  count: number; first: string; last: string; message: string;
  /** skjult på denne maskine ('machine') eller på alle ('global') */
  ignored?: 'machine' | 'global' | null;
  recent: string[];
}
export interface SysLogEntry { id: number; time: string; level: SysLevel; log: string; source: string; eventId?: number | null; message: string; kind?: string | null; key: string }
export interface MachineSysLog {
  days: number; total: number; hiddenCount: number; groups: SysLogGroup[]; entries: SysLogEntry[];
  sources: { source: string; count: number }[]; levels: Partial<Record<SysLevel, number>>;
  lastReceived?: string | null;
  /** agenten indsamler fejlloggen (3.40.0 eller nyere) */
  collects: boolean;
  /** null = live-loggen kan bruges, ellers årsagen */
  live: string | null;
}
export interface FleetGroup extends SysLogGroup { machineId: number; machine: string; platform: string; online: boolean }
export interface FleetSysLog { days: number; hiddenCount: number; truncated: boolean; groups: FleetGroup[]; machines: { id: number; name: string; platform: string }[] }
/** En linje i live-loggen (samme felter som serverens SysLogEvent). */
export interface LiveEvent { time: string; level: SysLevel; log: string; source: string; eventId?: number | null; message: string; kind?: string | null }

export const LEVELS: Record<SysLevel, { label: string; color: string }> = {
  critical: { label: 'Kritisk', color: 'red' },
  error: { label: 'Fejl', color: 'orange' },
  warning: { label: 'Advarsel', color: 'yellow' },
  info: { label: 'Information', color: 'gray' },
};

/** Niveaufiltret: '' = alt, ellers det valgte og alt mere alvorligt. */
export const LEVEL_FILTERS = [
  { value: '', label: 'Alle' }, { value: 'warning', label: 'Advarsler' }, { value: 'error', label: 'Fejl' }, { value: 'critical', label: 'Kritiske' },
] as const;
export const RANGES = [{ value: '1', label: 'Seneste døgn' }, { value: '7', label: 'Seneste 7 dage' }, { value: '30', label: 'Seneste 30 dage' }] as const;

export type Area = 'nedbrud' | 'hardware' | 'disk' | 'strøm' | 'opdatering' | 'hukommelse' | 'program' | 'støj';
export const AREAS: Record<Area, string> = {
  nedbrud: 'Nedbrud', hardware: 'Hardware', disk: 'Disk', strøm: 'Nedlukninger', opdatering: 'Opdateringer', hukommelse: 'Hukommelse', program: 'Programmer', støj: 'Støj',
};

export interface Known {
  title: string;
  /** hvad hændelsen betyder */
  explain: string;
  /** hvad man gør (i rækkefølge) */
  todo: string[];
  area: Area;
  /** harmløs støj, der kan skjules */
  noise?: boolean;
}

/** Kendte hændelser (typen bestemmes af serveren, SysLogKinds i Fleetdeck.Shared/SysLog.cs). */
export const KNOWN: Record<string, Known> = {
  'unexpected-shutdown': {
    title: 'Uventet nedlukning', area: 'strøm',
    explain: 'Windows blev ikke lukket ordentligt ned: strømmen forsvandt, maskinen frøs og blev slukket på knappen, eller den genstartede efter en blå skærm.',
    todo: [
      'Én gang er som regel et strømsvigt eller en tvungen slukning – ingen grund til bekymring.',
      'Sker det igen, så se efter en blå skærm (BugCheck 1001) lige før, og tjek strømforsyning, stikdåse og temperaturer.',
      'Bærbar: tjek batteriets helbred.',
    ],
  },
  'shutdown-6008': {
    title: 'Forrige nedlukning var uventet', area: 'strøm',
    explain: 'Windows noterer ved opstart, at den sidste nedlukning ikke var planlagt. Den hører sammen med Kernel-Power 41 fra samme tidspunkt.',
    todo: ['Se Kernel-Power 41 og BugCheck 1001 fra samme tidspunkt for årsagen.'],
  },
  bugcheck: {
    title: 'Blå skærm', area: 'nedbrud',
    explain: 'Windows gik ned med en blå skærm og genstartede. Beskeden indeholder stopkoden (fx 0x0000009f) og hvor dumpfilen ligger.',
    todo: [
      'Søg på stopkoden – den peger ofte på en bestemt driver.',
      'Opdatér drivere (især grafik, netværk og lager) og BIOS/UEFI.',
      'Gentager det sig: kør Windows Hukommelsesdiagnosticering og tjek diskens helbred.',
    ],
  },
  'whea-fatal': {
    title: 'Hardwarefejl', area: 'hardware',
    explain: 'Processor, hukommelse, bundkort eller et PCIe-kort har meldt en fejl, som Windows ikke kunne rette. Det kan give nedbrud og ødelagte data.',
    todo: ['Kør en hukommelsestest og tjek temperaturer.', 'Fjern overclocking og XMP-profil i BIOS, og opdatér BIOS.', 'Bliver det ved, så få hardwaren undersøgt.'],
  },
  'whea-corrected': {
    title: 'Rettet hardwarefejl', area: 'hardware',
    explain: 'Hardwaren meldte en fejl, som blev rettet automatisk (ofte en PCIe-forbindelse). Enkelte er harmløse; mange på kort tid kan varsle en fejl.',
    todo: ['Enkelte kan ignoreres.', 'Mange: opdatér chipset-drivere og BIOS, og tjek at kort og kabler sidder ordentligt.'],
  },
  'disk-bad-block': {
    title: 'Dårlig sektor på disken', area: 'disk',
    explain: 'Disken har et område, der ikke kan læses. Det er ofte første tegn på, at disken er ved at gå i stykker.',
    todo: ['Tag backup nu.', 'Tjek diskens SMART-helbred under Helbred.', 'Planlæg at skifte disken.'],
  },
  'disk-paging': {
    title: 'Fejl ved læsning fra disken', area: 'disk',
    explain: 'Windows fik en fejl, mens hukommelse blev flyttet til eller fra disken. Det kan skyldes disken, kablet eller en USB-disk, der blev taget ud.',
    todo: ['Var det en USB-disk, der blev taget ud, kan det ignoreres.', 'Ellers: tjek SMART-helbred og kabler, og tag backup.'],
  },
  'disk-retry': {
    title: 'Disken svarede ikke i tide', area: 'disk',
    explain: 'En læsning eller skrivning måtte prøves igen. Enkelte er normale; mange tyder på et dårligt kabel, strømsparing eller en disk på vej ned.',
    todo: ['Opdatér lagerdriver og diskens firmware.', 'Tjek kabel og SMART-helbred.'],
  },
  'disk-controller': {
    title: 'Fejl i diskcontrolleren', area: 'disk',
    explain: 'Forbindelsen mellem pc og disk gav en fejl. Det skyldes oftest kabel, stik eller controllerens driver.',
    todo: ['Tjek eller skift kablet.', 'Opdatér chipset- og lagerdriver.'],
  },
  'ntfs-corrupt': {
    title: 'Filsystemet er beskadiget', area: 'disk',
    explain: 'Windows har fundet fejl i filsystemet på et drev. Filer kan være ødelagte.',
    todo: ['Kør chkdsk /f på drevet (C: kræver en genstart).', 'Tjek diskens helbred, og tag backup.'],
  },
  'storport-reset': {
    title: 'Disken blev nulstillet', area: 'disk',
    explain: 'Lagerdriveren måtte nulstille forbindelsen til disken, fordi den ikke svarede.',
    todo: ['Opdatér NVMe-/SATA-driver og diskens firmware.', 'Gentager det sig: slå strømstyring for PCIe og disk fra.'],
  },
  'wu-failed': {
    title: 'Windows-opdatering mislykkedes', area: 'opdatering',
    explain: 'En opdatering kunne ikke installeres. Beskeden viser opdateringen og fejlkoden.',
    todo: ['Prøv igen fra Opdateringer – ofte lykkes det næste gang.', 'Gentager det sig: søg på fejlkoden (fx 0x800f0922), frigør plads, og kør Windows Update-fejlfinding.'],
  },
  'dcom-noise': {
    title: 'DCOM-tilladelse', area: 'støj', noise: true,
    explain: 'Et Windows-program bad om en tilladelse, det ikke har. Det sker på næsten alle pc\'er og er harmløst ifølge Microsoft.',
    todo: ['Kan ignoreres – skjul den på alle maskiner.'],
  },
  oom: {
    title: 'Hukommelsen løb tør', area: 'hukommelse',
    explain: 'Linux lukkede et program for at frigøre hukommelse (OOM-killer). Beskeden viser hvilket program.',
    todo: ['Se hvilket program der blev lukket, og om det bruger for meget hukommelse.', 'Giv mere RAM eller swap, eller sæt en hukommelsesgrænse på containeren.'],
  },
  'io-error': {
    title: 'Læse-/skrivefejl på disk', area: 'disk',
    explain: 'Kernen kunne ikke læse eller skrive på en disk.',
    todo: ['Tjek SMART-helbred under Helbred.', 'Tjek kabler og USB-forbindelse.', 'Tag backup.'],
  },
  'kernel-panic': {
    title: 'Kernefejl', area: 'nedbrud',
    explain: 'Linux-kernen ramte en alvorlig fejl (panic eller Oops). Ved panic genstarter eller fryser maskinen.',
    todo: ['Se linjerne lige før i loggen.', 'Opdatér kerne og firmware.', 'Gentager det sig: tjek hukommelsen med memtest.'],
  },
  'fs-error': {
    title: 'Fejl i filsystemet', area: 'disk',
    explain: 'Filsystemet (ext4, btrfs eller xfs) har fundet beskadigede data.',
    todo: ['Kør fsck eller btrfs scrub, når drevet ikke er i brug.', 'Tjek diskens helbred, og tag backup.'],
  },
  segfault: {
    title: 'Program gik ned', area: 'program',
    explain: 'Et program lukkede på grund af en fejl i programmet selv. Resten af systemet er ikke påvirket.',
    todo: ['Gentager det sig: opdatér programmet.'],
  },
  'mac-panic': {
    title: 'Kernepanik på Mac\'en', area: 'nedbrud',
    explain: 'macOS gik ned og genstartede. Rapporten ligger i /Library/Logs/DiagnosticReports.',
    todo: [
      'Opdatér macOS og programmer med systemudvidelser.',
      'Tag USB-udstyr ud og se, om det sker igen.',
      'Gentager det sig: kør Apple Diagnostics.',
    ],
  },
};

export const known = (kind?: string | null): Known | null => (kind ? KNOWN[kind] ?? null : null);

/** Kort navn på kilden: "Microsoft-Windows-Kernel-Power" → "Kernel-Power". */
export const shortSource = (s: string) => s.replace(/^Microsoft-Windows-/i, '');

/** "Disk 7", "Kernel-Power 41" eller bare kilden (Linux/Mac). */
export const sourceLabel = (g: { source: string; eventId?: number | null }) => (g.eventId != null ? `${shortSource(g.source)} ${g.eventId}` : shortSource(g.source));

/** Overskriften: den kendte titel, ellers kilden. */
export const groupTitle = (g: { source: string; eventId?: number | null; kind?: string | null }) => known(g.kind)?.title ?? sourceLabel(g);

export const countText = (n: number) => (n === 1 ? '1 gang' : `${n} gange`);

const pad = (n: number) => String(n).padStart(2, '0');
/** "i dag 08:15", "i går 22:01" eller "12.11. 10:02" (lokal tid). */
export function when(ts: string, now = new Date()): string {
  const d = new Date(ts);
  const t = `${pad(d.getHours())}:${pad(d.getMinutes())}`;
  const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((day(now) - day(d)) / 86_400_000);
  if (diff === 0) return `i dag ${t}`;
  if (diff === 1) return `i går ${t}`;
  return `${pad(d.getDate())}.${pad(d.getMonth() + 1)}. ${t}`;
}

/** "første 12.11. 10:02 · seneste i dag 08:15" (kun seneste, når det er én hændelse). */
export const rangeText = (g: { count: number; first: string; last: string }, now = new Date()) =>
  g.count > 1 && g.first !== g.last ? `første ${when(g.first, now)} · seneste ${when(g.last, now)}` : when(g.last, now);

/** Antal pr. område til tallene øverst på siden Hændelser (antal hændelser og berørte maskiner). */
export function summarize(groups: FleetGroup[]): { area: Area; count: number; machines: number }[] {
  const by = new Map<Area, { count: number; machines: Set<number> }>();
  for (const g of groups) {
    if (g.ignored) continue;
    const area = known(g.kind)?.area ?? (g.level === 'critical' ? 'nedbrud' : null);
    if (!area || area === 'støj') continue;
    const x = by.get(area) ?? { count: 0, machines: new Set<number>() };
    x.count += g.count; x.machines.add(g.machineId); by.set(area, x);
  }
  const order: Area[] = ['nedbrud', 'hardware', 'disk', 'strøm', 'hukommelse', 'opdatering', 'program'];
  return order.filter((a) => by.has(a)).map((a) => ({ area: a, count: by.get(a)!.count, machines: by.get(a)!.machines.size }));
}

/** Filtrér grupperne på siden Hændelser efter område (tom = alle). */
export const byArea = <G extends { kind?: string | null; level: SysLevel }>(groups: G[], area: Area | '') =>
  area === '' ? groups : groups.filter((g) => (known(g.kind)?.area ?? (g.level === 'critical' ? 'nedbrud' : null)) === area);

/** Forespørgslen til maskinens fejllog. */
export function logQuery(f: { days: string; level: string; source: string | null; q: string; hidden: boolean }): string {
  const p = new URLSearchParams({ days: f.days });
  if (f.level) p.set('level', f.level);
  if (f.source) p.set('source', f.source);
  if (f.q.trim()) p.set('q', f.q.trim());
  if (f.hidden) p.set('hidden', 'true');
  return p.toString();
}

// ---------------------------------------------------------------- live
export const LIVE_MAX = 2000;
export const HEARTBEAT_MS = 30_000;
const FRAME = { data: 0, exit: 2, status: 3 } as const;
const dec = new TextDecoder();

export type LiveFrame = { type: 'event'; ev: LiveEvent } | { type: 'status'; text: string } | { type: 'exit'; code: number } | null;

/** Rammerne fra serveren: data = én hændelse som JSON, status = tekst, exit = slutkode. */
export function parseLiveFrame(b: Uint8Array): LiveFrame {
  if (b.length === 0) return null;
  if (b[0] === FRAME.data) {
    try { const ev = JSON.parse(dec.decode(b.subarray(1))) as LiveEvent; return ev && typeof ev.message === 'string' ? { type: 'event', ev } : null; } catch { return null; }
  }
  if (b[0] === FRAME.status) return { type: 'status', text: dec.decode(b.subarray(1)) };
  if (b[0] === FRAME.exit && b.length === 5) return { type: 'exit', code: new DataView(b.buffer, b.byteOffset, 5).getInt32(1) };
  return null;
}

/** Livstegn fra siden (en tom dataramme). Serveren stopper live-loggen, hvis de udebliver i to minutter. */
export const heartbeatFrame = () => new Uint8Array([FRAME.data]);

/** Tilføj linjer og behold højst LIVE_MAX (de ældste ryger). */
export const appendLive = (lines: LiveEvent[], add: LiveEvent[]) => {
  const all = lines.concat(add);
  return all.length > LIVE_MAX ? all.slice(all.length - LIVE_MAX) : all;
};

/** Én linje som tekst (til "Kopiér"). */
export const liveText = (e: LiveEvent) => {
  const d = new Date(e.time);
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())} ${LEVELS[e.level]?.label ?? e.level} ${sourceLabel(e)}: ${e.message}`;
};

export const liveMatches = (e: LiveEvent, q: string) => {
  const s = q.trim().toLowerCase();
  return !s || e.message.toLowerCase().includes(s) || e.source.toLowerCase().includes(s) || String(e.eventId ?? '') === s;
};
