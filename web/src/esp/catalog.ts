// Firmwarekataloget fra ESP Flasher (esp-flasher/firmware/catalog.json) og det, siden viser om det.

export interface EspRelease { version: string; date?: string; title?: string; items?: string[] }
export interface EspFirmware {
  id: string; name: string; chip: string; kind: string; kindLabel?: string; status?: string;
  version: string; date?: string; manifest: string; sha256: string;
  summary?: string; features?: string[]; wiring?: [string, string][]; after?: string; after_wifi?: string; ota?: string;
  changelog?: (EspRelease | string)[];
}
export interface EspCatalogFile { generated?: string; firmwares: EspFirmware[] }

export const KIND_LABEL: Record<string, string> = { any: 'Alle', zigbee: 'Zigbee', wifi: 'Wi-Fi' };

export const tested = (f: EspFirmware) => (f.status ?? '').toLowerCase() === 'testet';
export const needsWifi = (f: EspFirmware) => f.kind === 'wifi';

/** Filtre med antal: chip ("any" = alle) og derefter type inden for den valgte chip. */
export function filters(list: EspFirmware[], chip: string, kind: string) {
  const chips = ['any', ...new Set(list.map((f) => f.chip))];
  const c = chips.includes(chip) ? chip : 'any';
  const inChip = list.filter((f) => c === 'any' || f.chip === c);
  const kinds = ['any', ...new Set(inChip.map((f) => f.kind))];
  const k = kinds.includes(kind) ? kind : 'any';
  return {
    chip: c, kind: k,
    chips: chips.map((x) => ({ value: x, label: x === 'any' ? 'Alle' : x, count: list.filter((f) => x === 'any' || f.chip === x).length })),
    kinds: kinds.map((x) => ({ value: x, label: KIND_LABEL[x] ?? x, count: inChip.filter((f) => x === 'any' || f.kind === x).length })),
    items: inChip.filter((f) => k === 'any' || f.kind === k),
  };
}

/** Nyeste udgivelse først; ældre katalogformat havde bare en liste af tekster. */
export function releases(f: EspFirmware): EspRelease[] {
  const log = f.changelog ?? [];
  if (log.length && typeof log[0] === 'string') return [{ version: f.version, items: log as string[] }];
  return (log as EspRelease[]).filter((r) => r && typeof r === 'object');
}

/** "2026-10-30T16:54:42" → "30. okt 2026 16.54" (dansk, uden sekunder); tom ved ukendt. */
export function generatedLabel(s: string | undefined | null): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2}))?/.exec(s ?? '');
  if (!m) return '';
  const mon = ['jan', 'feb', 'mar', 'apr', 'maj', 'jun', 'jul', 'aug', 'sep', 'okt', 'nov', 'dec'][Number(m[2]) - 1];
  if (!mon) return '';
  return `${Number(m[3])}. ${mon} ${m[1]}${m[4] ? ` ${m[4]}.${m[5]}` : ''}`;
}

/** Platformen, så vejledningen til at stole på certifikatet starter på den rigtige fane. */
export function platformOf(ua: string): 'windows' | 'mac' | 'linux' {
  if (/Windows/i.test(ua)) return 'windows';
  if (/Mac OS X|Macintosh/i.test(ua)) return 'mac';
  return 'linux';
}

/**
 * Hvad siden skal sige om kataloget. Fleetdeck har ingen firmware med, så uden et katalog forklares, hvordan man sætter et op.
 * setup: intet katalog og ingen firmware · stale: intet katalog, men firmware hentet tidligere (opdateres ikke) ·
 * empty: et katalog, men ingen firmware endnu (tomt eller ikke hentet) · null: alt er i orden.
 */
export type CatalogNotice = 'setup' | 'stale' | 'empty' | null;
export function catalogNotice(live: boolean, count: number): CatalogNotice {
  if (!live) return count > 0 ? 'stale' : 'setup';
  return count === 0 ? 'empty' : null;
}
