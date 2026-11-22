// Sammenklappelig sidemenu (fælles regel for alle apps, se Faelles-design/sammenklappelig-menu.md).
// Kun ren logik her – ingen React – så den kan testes med node (tests/navCollapse.test.ts).
import type { CountKey, Counts, NavPage } from '../nav';

/** Nøglen i localStorage. Huskes pr. browser, ikke på serveren. */
export const NAV_COLLAPSED_KEY = 'fleetdeck.nav.collapsed';

/** Bredden på sidemenuen: almindelig, indlejret i Kontrolrum og som smal ikon-skinne. */
export const NAV_WIDTH = 236;
export const NAV_WIDTH_EMBED = 210;
export const RAIL_WIDTH = 64;

/** Varighed på overgangen (ms). Ingen overgang ved prefers-reduced-motion (styres i CSS). */
export const NAV_TRANSITION_MS = 180;

/** Den gemte værdi: kun "1" (eller "true") betyder sammenklappet – alt andet, også intet, er udfoldet. */
export function parseCollapsed(raw: string | null | undefined): boolean {
  return raw === '1' || raw === 'true';
}

export const serializeCollapsed = (collapsed: boolean) => (collapsed ? '1' : '0');

/** Læser den gemte tilstand uden at fejle, hvis localStorage ikke er til rådighed (privat vindue, iframe uden adgang). */
export function readCollapsed(storage: Pick<Storage, 'getItem'> | undefined): boolean {
  try { return parseCollapsed(storage?.getItem(NAV_COLLAPSED_KEY)); } catch { return false; }
}

export function writeCollapsed(storage: Pick<Storage, 'setItem'> | undefined, collapsed: boolean): void {
  try { storage?.setItem(NAV_COLLAPSED_KEY, serializeCollapsed(collapsed)); } catch { /* huskes så bare ikke */ }
}

/** Kan menuen klappes sammen? Kun når den står i siden på en bred skærm (mobilens skuffe og menuen i toppen er uændrede). */
export const canCollapse = (o: { navPos: 'side' | 'top'; wide: boolean; navbarShown: boolean }) => o.navPos === 'side' && o.wide && o.navbarShown;

/** Sidemenuens bredde. */
export const navWidth = (o: { rail: boolean; embed: boolean }) => (o.rail ? RAIL_WIDTH : o.embed ? NAV_WIDTH_EMBED : NAV_WIDTH);

export interface KeyLike { key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean; repeat?: boolean }
export interface TargetLike { tagName?: string; isContentEditable?: boolean; type?: string; getAttribute?: (name: string) => string | null }

/** Input-typer, der ikke er tekstfelter: der må genvejen gerne virke. */
const NON_TEXT_INPUTS = ['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file', 'image'];

/** Står fokus i et tekstfelt (input, textarea, select, contenteditable eller en textbox – også terminalens skjulte textarea)? */
export function isTypingTarget(t: TargetLike | null | undefined): boolean {
  if (!t) return false;
  if (t.isContentEditable) return true;
  const tag = (t.tagName ?? '').toUpperCase();
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
  if (tag === 'INPUT') return !NON_TEXT_INPUTS.includes((t.type ?? 'text').toLowerCase());
  const role = t.getAttribute?.('role');
  return role === 'textbox' || role === 'combobox' || role === 'searchbox';
}

/** Ctrl+B (⌘B på Mac): kun B med Ctrl eller ⌘, uden Alt og Shift, og ikke når tasten holdes nede. */
export function isToggleShortcut(e: KeyLike): boolean {
  if (e.repeat || e.altKey || e.shiftKey) return false;
  if (!(e.ctrlKey || e.metaKey)) return false;
  return e.key === 'b' || e.key === 'B';
}

/** Skal tastetrykket skifte menuen? Genvejen plus: ikke mens man skriver. */
export const shouldToggle = (e: KeyLike, target: TargetLike | null | undefined) => isToggleShortcut(e) && !isTypingTarget(target);

/** Mac (og iPad med tastatur) bruger ⌘, alle andre Ctrl. */
export const isMacPlatform = (platform: string | undefined) => /mac|iphone|ipad|ipod/i.test(platform ?? '');

/** Teksten til genvejen i knappens tooltip. */
export const shortcutLabel = (platform: string | undefined) => (isMacPlatform(platform) ? '⌘B' : 'Ctrl B');

/** Knappens tekst (og aria-label). */
export const toggleLabel = (collapsed: boolean) => (collapsed ? 'Fold menuen ud' : 'Klap menuen sammen');

/** Tal, der bare er et antal (ikke noget at gøre ved): vises ikke på skinnen – samme regel som tallene i topmenuen. */
const TOTALS: CountKey[] = ['machines', 'servers'];

/** Mærket på et ikon i den smalle skinne: rødt tal for noget, der skal handles på, ellers en lille prik. */
export type RailBadge = { kind: 'number'; text: string; n: number; alert?: boolean } | { kind: 'dot'; n: number; alert?: undefined };

/** Lille tal på ikonet: 1-9, ellers "9+". */
export const railText = (n: number) => (n > 9 ? '9+' : String(n));

export function railBadge(p: Pick<NavPage<unknown>, 'count' | 'alert' | 'red'>, c: Counts): RailBadge | undefined {
  const alert = p.alert ? c[p.alert] ?? 0 : 0;
  if (alert > 0) return { kind: 'number', text: railText(alert), n: alert, alert: true };
  if (!p.count || TOTALS.includes(p.count)) return undefined;
  const n = c[p.count] ?? 0;
  if (n <= 0) return undefined;
  return p.red ? { kind: 'number', text: railText(n), n } : { kind: 'dot', n };
}

/** Tooltip og aria-label på et punkt i skinnen: navnet, og tallet, hvis der er et (anmodninger forklares, som i den brede menu). */
export function railLabel(label: string, b: RailBadge | undefined): string {
  if (!b) return label;
  return b.alert ? `${label} · ${b.n} venter på godkendelse` : `${label} (${b.n})`;
}

/** localStorage, hvis browseren giver adgang (en iframe i sandkasse kan kaste allerede ved opslaget). */
export function browserStorage(): Storage | undefined {
  try { return typeof localStorage === 'undefined' ? undefined : localStorage; } catch { return undefined; }
}
