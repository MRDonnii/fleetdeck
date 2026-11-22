// "Anmod om adgang": tekster og regler til administratorens liste (ren logik, så den kan testes med node: web/tests/accessrequests.test.ts).
import type { AccessBlockRow, AccessRequestRow, AccessState } from '../types';

export const OPEN: AccessState[] = ['pending', 'approved', 'issued'];
export const isOpen = (r: Pick<AccessRequestRow, 'state'>) => OPEN.includes(r.state);

/** Tallet på Maskiner i menuen: anmodninger, der venter på administratoren (godkendte venter kun på maskinen). */
export const waitingCount = (rows: AccessRequestRow[] | undefined) => rows?.filter((r) => r.state === 'pending').length ?? 0;

export const STATE: Record<AccessState, { label: string; color: string }> = {
  pending: { label: 'Venter på dig', color: 'orange' },
  approved: { label: 'Godkendt – venter på maskinen', color: 'blue' },
  issued: { label: 'Godkendt – tilmelder sig', color: 'blue' },
  enrolled: { label: 'Tilmeldt', color: 'green' },
  rejected: { label: 'Afvist', color: 'red' },
  expired: { label: 'Udløbet', color: 'gray' },
  key_sent: { label: 'Nøgle sendt', color: 'teal' },
  superseded: { label: 'Erstattet af ny anmodning', color: 'gray' },
};

export const PLATFORM: Record<string, string> = { windows: 'Windows', linux: 'Linux', mac: 'Mac' };

/** Samme regel som serveren (A-Z, 0-9, - og _, højst 31 tegn, starter med bogstav eller tal). */
export const validName = (n: string) => /^[A-Z0-9][A-Z0-9_-]{0,30}$/.test(n.trim().toUpperCase());

/** Hvorfor "Godkend" ikke kan trykkes endnu (null = klar). */
export function approveBlocker(f: { profile: string | null; name: string; confirmed: boolean }): string | null {
  if (f.profile == null || f.profile === '') return 'Vælg en profil (eller "Ingen profil").';
  if (!validName(f.name)) return 'Navnet må kun have A-Z, 0-9, - og _ (højst 31 tegn).';
  if (!f.confirmed) return 'Bekræft koden med brugeren først.';
  return null;
}

/** "om 23 t." / "om 12 min." – hvornår anmodningen udløber. */
export function expiresIn(iso: string, now = new Date()): string {
  const s = (new Date(iso).getTime() - now.getTime()) / 1000;
  if (s <= 0) return 'udløbet';
  if (s < 3600) return `om ${Math.max(1, Math.floor(s / 60))} min.`;
  if (s < 86400) return `om ${Math.floor(s / 3600)} t.`;
  return `om ${Math.floor(s / 86400)} d.`;
}

/** Linjen under navnet: platform, styresystem og model. */
export const describe = (r: Pick<AccessRequestRow, 'platform' | 'os' | 'model'>) =>
  [PLATFORM[r.platform] ?? r.platform, r.os, r.model].filter(Boolean).join(' · ');

/** Åbne først (ventende øverst), derefter de nyeste. */
export function sortRows(rows: AccessRequestRow[]): AccessRequestRow[] {
  const rank = (r: AccessRequestRow) => (r.state === 'pending' ? 0 : isOpen(r) ? 1 : 2);
  return [...rows].sort((a, b) => rank(a) - rank(b) || b.id - a.id);
}

/** Maskinen spørger hvert halve minut: har den ikke spurgt i så mange minutter, svarer den ikke (slukket, anden anmodning …). */
export const SILENT_MINUTES = 3;

/** "Spurgte for 1 min. siden" / "Maskinen svarer ikke (sidst 12 min. siden)" – så en død anmodning kan kendes fra en levende. */
export function lastSeen(r: Pick<AccessRequestRow, 'lastPoll' | 'created'>, now = new Date()): { text: string; silent: boolean } {
  const at = new Date(r.lastPoll ?? r.created).getTime();
  const min = Math.max(0, (now.getTime() - at) / 60000);
  const span = min < 1 ? 'lige nu' : min < 60 ? `${Math.floor(min)} min. siden` : min < 1440 ? `${Math.floor(min / 60)} t. siden` : `${Math.floor(min / 1440)} d. siden`;
  if (min >= SILENT_MINUTES) return { text: r.lastPoll ? `Maskinen svarer ikke (spurgte sidst ${span})` : `Maskinen har ikke spurgt siden anmodningen (${span})`, silent: true };
  return { text: `Maskinen spurgte ${span}`, silent: false };
}

/** "Bloker…": hvor længe (timer). En blokering udløber altid. */
export const BLOCK_DURATIONS: { value: string; label: string }[] = [
  { value: '1', label: '1 time' }, { value: '24', label: '24 timer' }, { value: '168', label: '7 dage' },
  { value: '720', label: '30 dage' }, { value: '2160', label: '90 dage' },
];

export interface BlockChoice { ip: boolean; mac: boolean; name: boolean; hours: string | null }

/** Hvad der blokeres, som en sætning ("IP 192.168.1.50 og navnet STUE-PC i 24 timer"), eller hvorfor der ikke kan trykkes. */
export function blockSummary(r: Pick<AccessRequestRow, 'ip' | 'name' | 'macs'> & Partial<Pick<AccessRequestRow, 'ipKey'>>, c: BlockChoice): { text: string; blocker: string | null } {
  const parts = [
    c.ip ? `IP ${r.ipKey || r.ip}` : null,
    c.mac && r.macs.length ? (r.macs.length === 1 ? `MAC ${r.macs[0]}` : `${r.macs.length} MAC-adresser`) : null,
    c.name ? `navnet ${r.name}` : null,
  ].filter(Boolean) as string[];
  const dur = BLOCK_DURATIONS.find((d) => d.value === c.hours)?.label;
  const what = parts.length > 1 ? `${parts.slice(0, -1).join(', ')} og ${parts[parts.length - 1]}` : parts[0] ?? '';
  const blocker = !parts.length ? 'Vælg mindst én ting at blokere.' : !dur ? 'Vælg hvor længe.' : r.ip === 'socket' && c.ip ? 'Serverens egen vært kan ikke blokeres på IP.' : null;
  return { text: parts.length && dur ? `Blokerer ${what} i ${dur}.` : '', blocker };
}

/**
 * Andre åbne anmodninger, som blokeringen afviser med det samme (samme regler som serveren: IP pr. /64, samme navn, fælles MAC). Vises før
 * man bekræfter – MAC og navn oplyses af enheden selv, så en blokering kan ramme en rigtig maskine, som en anden enhed giver sig ud for.
 */
export function blockAffects(r: AccessRequestRow, all: AccessRequestRow[], c: BlockChoice): AccessRequestRow[] {
  const key = r.ipKey || r.ip;
  return all.filter((o) => o.id !== r.id && isOpen(o) && (
    (c.ip && (o.ipKey || o.ip) === key)
    || (c.name && o.name === r.name)
    || (c.mac && o.macs.some((m) => r.macs.includes(m)))));
}

/** "2 min. siden" / "lige nu". */
function sinceText(iso: string, now: Date): string {
  const min = Math.max(0, (now.getTime() - new Date(iso).getTime()) / 60000);
  return min < 1 ? 'lige nu' : min < 60 ? `${Math.floor(min)} min. siden` : min < 1440 ? `${Math.floor(min / 60)} t. siden` : `${Math.floor(min / 1440)} d. siden`;
}

/** "Udløber om 23 t. · 4 afviste forsøg, sidst 2 min. siden". */
export function blockLine(b: Pick<AccessBlockRow, 'until' | 'hits' | 'lastHit'>, now = new Date()): string {
  const left = expiresIn(b.until, now);
  const hits = b.hits === 0 ? 'ingen forsøg siden' : `${b.hits} afvist${b.hits === 1 ? '' : 'e'} forsøg${b.lastHit ? `, sidst ${sinceText(b.lastHit, now)}` : ''}`;
  return `${left === 'udløbet' ? 'Udløbet' : `Udløber ${left}`} · ${hits}`;
}
