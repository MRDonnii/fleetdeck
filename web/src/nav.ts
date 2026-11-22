// Menuen delt efter opgave. Én liste (NAV_GROUPS) styrer sidemenuen (grupper med overskrifter), menuen i toppen
// (én rullemenu pr. gruppe), telefonens Mere-ark, Kontrolrums indlejrede fanebjælke og søgningen (Ctrl K).
// Ren data og logik uden React, så den kan testes med node (web/tests/nav.test.ts).

export type CountKey = 'machines' | 'upgrades' | 'alerts' | 'jobs' | 'servers' | 'checksDown' | 'messages' | 'access';
export type Counts = Record<CountKey, number>;

/** En side i menuen. icon er et navn, som App.tsx slår op i sine ikoner (enhederne har deres egne). */
export interface NavPage<I = string> {
  to: string; label: string; icon: I;
  /** tallet ved siden af (vises kun, når det er over 0 – undtagen Maskiner, der altid viser antallet i sidemenuen) */
  count?: CountKey;
  /** rødt tal = noget kræver handling */
  red?: boolean;
  /** et ekstra rødt tal (fx anmodninger om adgang ved Maskiner), vises kun når det er over 0 */
  alert?: CountKey;
  /** kun for administratorer */
  admin?: boolean;
  /** ekstra søgeord til Ctrl K */
  keywords?: string[];
}
/** 'devices' = iPads, Apple TV, iPhones og Android (de typer, der er i brug) sættes ind her. */
export type NavEntry = NavPage | 'devices';
export interface NavGroup<E = NavEntry> {
  key: string; label: string; /** kort navn til menuen i toppen */ short: string;
  /** ikon til menuen i toppen på smalle skærme (kun ikoner) */ icon: string; items: E[];
}

export const NAV_GROUPS: NavGroup[] = [
  { key: 'oversigt', label: 'Oversigt', short: 'Oversigt', icon: 'dashboard', items: [
    { to: '/', label: 'Oversigt', icon: 'dashboard', keywords: ['forside', 'flåde'] },
    { to: '/helbred', label: 'Helbred', icon: 'health', count: 'alerts', red: true, keywords: ['alarmer', 'advarsler'] },
    { to: '/haendelser', label: 'Hændelser', icon: 'events', admin: true, keywords: ['fejllog', 'logs', 'blå skærm', 'hændelseslog', 'event log', 'journal', 'nedbrud'] },
    { to: '/overvaagning', label: 'Overvågning', icon: 'monitor', count: 'checksDown', red: true, keywords: ['tjek', 'ping', 'oppetid'] },
  ] },
  { key: 'maskiner', label: 'Maskiner og brugere', short: 'Maskiner', icon: 'machines', items: [
    { to: '/maskiner', label: 'Maskiner', icon: 'machines', count: 'machines', alert: 'access', keywords: ['computere', 'pc', 'anmod om adgang', 'anmodninger'] },
    'devices',
    { to: '/beskeder', label: 'Beskeder', icon: 'messages', count: 'messages', admin: true, keywords: ['helpdesk', 'support', 'chat'] },
    { to: '/terminal', label: 'Terminal', icon: 'terminal', admin: true, keywords: ['ssh', 'kommandolinje'] },
  ] },
  { key: 'programmer', label: 'Programmer og opdateringer', short: 'Programmer', icon: 'packages', items: [
    { to: '/opdateringer', label: 'Opdateringer', icon: 'updates', count: 'upgrades', keywords: ['windows update', 'winget'] },
    { to: '/pakker', label: 'Programmer', icon: 'packages', keywords: ['pakker', 'installér'] },
    { to: '/apps', label: 'Apps', icon: 'apps', keywords: ['programmer i brug'] },
    { to: '/debloat', label: 'Debloat & privatliv', icon: 'debloat', admin: true, keywords: ['oprydning', 'telemetri'] },
  ] },
  { key: 'netvaerk', label: 'Netværk og enheder', short: 'Netværk', icon: 'network', items: [
    { to: '/printere', label: 'Printere', icon: 'printers', keywords: ['print'] },
    { to: '/servere', label: 'Servere & Docker', icon: 'servers', count: 'servers', keywords: ['unraid', 'containere', 'vm', 'netværksdrev', 'filserver'] },
    { to: '/esp', label: 'ESP-flash', icon: 'esp', keywords: ['esphome', 'firmware', 'usb'] },
  ] },
  { key: 'udrulning', label: 'Udrulning og automatik', short: 'Udrulning', icon: 'rocket', items: [
    { to: '/udrulning', label: 'Udrulning', icon: 'deploy', keywords: ['pxe', 'netboot', 'installation'] },
    { to: '/winsetup', label: 'Windows-opsætning', icon: 'winsetup', keywords: ['autounattend', 'usb-stik'] },
    { to: '/profiler', label: 'Profiler', icon: 'profiles', keywords: ['netværksdrev', 'politik'] },
    { to: '/scripts', label: 'Scripts', icon: 'scripts', keywords: ['powershell', 'bash'] },
    { to: '/jobs', label: 'Jobs', icon: 'jobs', count: 'jobs', keywords: ['kørsler', 'kø'] },
  ] },
  { key: 'system', label: 'System', short: 'System', icon: 'settings', items: [
    { to: '/indstillinger', label: 'Indstillinger', icon: 'settings', keywords: ['brugere', 'backup', 'konto', 'api-nøgler', 'adgang for programmer'] },
    { to: '/rapporter', label: 'Rapporter', icon: 'reports', admin: true },
    { to: '/log', label: 'Revisionslog', icon: 'audit', admin: true, keywords: ['historik', 'hvem gjorde hvad'] },
  ] },
];

/** Telefonens fanelinje i bunden: de fire mest brugte sider (resten ligger i Mere-arket). */
export const TABBAR = ['/', '/maskiner', '/opdateringer', '/helbred'];

/** Grupperne for en bruger: administrator-sider fjernes for læsere, enhederne sættes ind, tomme grupper droppes. */
export function navFor<I, G extends NavGroup<NavPage<I> | 'devices'>>(groups: G[], opts: { isAdmin: boolean; devices?: NavPage<I>[] }): (Omit<G, 'items'> & { items: NavPage<I>[] })[] {
  return groups
    .map((g) => ({ ...g, items: g.items.flatMap((e) => (e === 'devices' ? (opts.devices ?? []) : [e])).filter((p) => !p.admin || opts.isAdmin) }))
    .filter((g) => g.items.length > 0);
}

export const flatNav = <I>(groups: NavGroup<NavPage<I>>[]): NavPage<I>[] => groups.flatMap((g) => g.items);

/** Er siden "to" den aktive for stien? "/" kun på forsiden; ellers stien selv eller en underside (/maskiner/12). */
export const isActive = (to: string, path: string) => (to === '/' ? path === '/' : path === to || path.startsWith(`${to}/`));

export const activeGroup = <I>(groups: NavGroup<NavPage<I>>[], path: string) => groups.find((g) => g.items.some((p) => isActive(p.to, path)));

/** Tallet ved en side: undefined når der ikke er noget at vise (0). Maskiner viser altid antallet. */
export function countOf(p: NavPage<unknown>, c: Counts): number | undefined {
  if (!p.count) return undefined;
  const n = c[p.count] ?? 0;
  return p.count === 'machines' || n > 0 ? n : undefined;
}

/** Det ekstra røde tal ved en side (fx "2 anmodninger om adgang" ved Maskiner): undefined når der ikke er noget. */
export function alertOf(p: NavPage<unknown>, c: Counts): number | undefined {
  if (!p.alert) return undefined;
  const n = c[p.alert] ?? 0;
  return n > 0 ? n : undefined;
}

/** Tal, der bare er et antal (ikke noget at gøre ved): tælles ikke med i gruppens tal i toppen. */
const TOTALS: CountKey[] = ['machines', 'servers'];

/** Tallet på en gruppe i toppen: summen af sidernes tal (uden antal maskiner/servere), rødt hvis en rød side bidrager. */
export function groupCount(g: NavGroup<NavPage<unknown>>, c: Counts): { n: number; red: boolean } | undefined {
  let n = 0; let red = false;
  for (const p of g.items) {
    const a = alertOf(p, c);
    if (a) { n += a; red = true; }
    if (!p.count || TOTALS.includes(p.count)) continue;
    const v = c[p.count] ?? 0;
    if (v > 0) { n += v; if (p.red) red = true; }
  }
  return n > 0 ? { n, red } : undefined;
}

/** Tælleren i badges: over 99 vises som "99+". */
export const badgeText = (n: number) => (n > 99 ? '99+' : String(n));

