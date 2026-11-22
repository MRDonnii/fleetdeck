// Adgang for programmer: tekster, rettigheder og tider for API-nøgler (rene funktioner, testet i tests/apikeys.test.ts).

export type Scope = 'read' | 'control' | 'admin';

export interface ApiKeyRow {
  id: number; name: string; scope: Scope; shown: string; hint: string;
  created: string; createdBy: string; lastUsed: string | null; lastIp: string | null; uses: number;
  lanOnly: boolean; expires: string | null; migrated: boolean;
}
export interface ApiEndpoint { method: string; path: string; scope: Scope; description: string }
export interface ApiKeysInfo {
  prefix: string; keys: ApiKeyRow[]; endpoints: ApiEndpoint[];
  lastRejected: { at: string; ip: string; reason: string; name: string | null; hint: string | null } | null;
  limits: { readPerMinute: number; controlPerMinute: number };
}

export interface ScopeInfo { id: Scope; label: string; badge: string; color: string; text: string }

/** De tre rettigheder. Hver omfatter de lavere. */
export const SCOPES: ScopeInfo[] = [
  { id: 'read', label: 'Kun læse', badge: 'Kun læse', color: 'gray', text: 'Se status: maskiner, jobs, udrulninger og advarsler. Kan ikke ændre noget.' },
  { id: 'control', label: 'Læse og styre', badge: 'Læse og styre', color: 'blue', text: 'Også styre den daglige drift, når Fleetdeck får kald til det (fx starte jobs).' },
  { id: 'admin', label: 'Administrator', badge: 'Administrator', color: 'orange', text: 'Alt, også brugere og indstillinger. Brug den kun, hvis programmet virkelig skal.' },
];

const LEVEL: Record<string, number> = { read: 1, control: 2, admin: 3 };

export function scopeInfo(scope: string): ScopeInfo { return SCOPES.find((s) => s.id === scope) ?? SCOPES[0]; }

/** Endpoints, en nøgle med rettigheden må kalde (også dem, der kræver en lavere rettighed). */
export function endpointsFor(scope: Scope, all: ApiEndpoint[]): ApiEndpoint[] {
  return all.filter((e) => (LEVEL[e.scope] ?? 99) <= (LEVEL[scope] ?? 0));
}

/** Endpoints, der kræver netop denne rettighed (til forklaringen ved hvert valg). */
export function ownEndpoints(scope: Scope, all: ApiEndpoint[]): ApiEndpoint[] { return all.filter((e) => e.scope === scope); }

export const EXPIRY: { value: string; label: string }[] = [
  { value: '0', label: 'Aldrig' }, { value: '30', label: '30 dage' }, { value: '90', label: '90 dage' }, { value: '365', label: '365 dage' },
];

const DAY = 86_400_000;
const MONTHS = ['jan.', 'feb.', 'mar.', 'apr.', 'maj', 'jun.', 'jul.', 'aug.', 'sep.', 'okt.', 'nov.', 'dec.'];
const pad = (n: number) => String(n).padStart(2, '0');
const hm = (d: Date) => `${pad(d.getHours())}:${pad(d.getMinutes())}`;
const sameDay = (a: Date, b: Date) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

/** "3. jun. 2025" (eller "i dag 09:41"). */
export function dateText(ts: string | null | undefined, now = new Date()): string {
  if (!ts) return '–';
  const d = new Date(ts);
  if (sameDay(d, now)) return `i dag ${hm(d)}`;
  return `${d.getDate()}. ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
}

/** Hvornår nøglen sidst blev brugt: "for 12 sek.", "for 4 min.", "i dag 08:14", "i går 23:10", "for 94 dage siden". */
export function lastUsedText(ts: string | null | undefined, now = new Date()): string {
  if (!ts) return 'Aldrig brugt endnu';
  const d = new Date(ts);
  const s = Math.max(0, Math.round((now.getTime() - d.getTime()) / 1000));
  if (s < 60) return `for ${s} sek.`;
  if (s < 3600) return `for ${Math.floor(s / 60)} min.`;
  if (sameDay(d, now)) return `i dag ${hm(d)}`;
  if (sameDay(d, new Date(now.getTime() - DAY))) return `i går ${hm(d)}`;
  const days = Math.floor(s / 86400);
  return days < 2 ? `for ${days || 1} dag siden` : `for ${days} dage siden`;
}

/** Orange mærke: ikke brugt (eller lavet og aldrig brugt) i 90 dage. */
export function unused90(k: Pick<ApiKeyRow, 'lastUsed' | 'created'>, now = new Date()): boolean {
  const t = new Date(k.lastUsed ?? k.created).getTime();
  return now.getTime() - t >= 90 * DAY;
}

/** Grøn prik når brugt inden for et døgn, orange når ubrugt i 90 dage, ellers grå. */
export function usageTone(k: Pick<ApiKeyRow, 'lastUsed' | 'created'>, now = new Date()): 'green' | 'orange' | 'gray' {
  if (unused90(k, now)) return 'orange';
  if (k.lastUsed && now.getTime() - new Date(k.lastUsed).getTime() < DAY) return 'green';
  return 'gray';
}

export function expiresText(expires: string | null, now = new Date()): string | null {
  if (!expires) return null;
  const left = new Date(expires).getTime() - now.getTime();
  if (left <= 0) return 'udløbet';
  const days = Math.ceil(left / DAY);
  return days === 1 ? 'udløber i morgen' : `udløber om ${days} dage`;
}

/** Linjen over nøglen i "vises kun én gang": "Kun læse · kun fra hjemmenettet · udløber aldrig". */
export function keySummary(scope: Scope, lanOnly: boolean, expiresDays: number): string {
  return [scopeInfo(scope).label, lanOnly ? 'kun fra hjemmenettet' : 'også udefra', expiresDays > 0 ? `udløber om ${expiresDays} dage` : 'udløber aldrig'].join(' · ');
}

/** Eksempel på headeren med en forkortet nøgle: "Authorization: Bearer fd_Ab3x…Hc3T". */
export function headerExample(key: string): string {
  const short = key.length > 16 ? `${key.slice(0, 7)}…${key.slice(-4)}` : key;
  return `Authorization: Bearer ${short}`;
}
