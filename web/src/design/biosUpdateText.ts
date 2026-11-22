// BIOS-opdatering på maskinens side: tekster og farver (rene funktioner, testet i tests/biosUpdate.test.ts).
import type { BiosUpdateCheck, BiosUpdateEffective, BiosUpdateRoute, BiosUpdateRun } from '../types';

export type Tone = 'green' | 'red' | 'yellow' | 'gray' | 'blue';
export interface Shown { text: string; tone: Tone }

/** Knappen hedder altid "Opdatér BIOS …" og findes kun her – aldrig i "Opdatér alt". */
export const BIOS_UPDATE_BUTTON = 'Opdatér BIOS…';

/**
 * Kun USB-knappen i BIOS-rækken: vejen er hentet og findes ikke. undefined = ikke hentet endnu (eller kaldet fejlede) – så ingen knap,
 * ellers blinker "Lav USB-stik…" på en maskine med en vej fra Windows.
 */
export function rowUsbOnly(u: { route?: BiosUpdateRoute | null } | undefined, usbAllowed: boolean): boolean {
  return u !== undefined && !u.route && usbAllowed;
}

/** Advarslerne, der vises før bekræftelsen (vejen bestemmer de sidste). */
export function biosUpdateWarnings(route: BiosUpdateRoute): string[] {
  const common = [
    'Strømmen må ikke gå under en BIOS-opdatering. Bærbare skal sidde i laderen.',
    'Under selve opdateringen er maskinen væk i flere minutter, og Fleetdeck kan ikke se den. Sluk den ikke, selv om skærmen er sort eller viser en statuslinje.',
    'Efter opdateringen kan maskinen stoppe ved fejlen 0162 (Setup data integrity) og vente på F1. Så skal nogen trykke F1 og derefter F10 ved tastaturet.',
    "BIOS'en kan nulstille indstillingerne. Fleetdeck tager en kopi først og viser forskellen bagefter, så du kan gendanne dem.",
  ];
  return route === 'capsule'
    ? [...common, 'Låses disken op med TPM, så hav gendannelsesnøglen klar: en BIOS-opdatering ændrer TPM-målingerne.']
    : [...common,
      'BitLocker sættes på pause i to genstarter, så Windows ikke beder om gendannelsesnøglen.',
      'Brug Genstart, ikke Luk computeren: opdateringen bliver kun færdig ved en rigtig genstart.'];
}

/** Bekræftelsen: brugeren skal skrive versionen. */
export function biosUpdateConfirm(target: string): string {
  return `Skriv ${target} for at bekræfte. Fleetdeck tager en kopi af BIOS-indstillingerne og lægger opdateringen klar. Maskinen genstarter ikke af sig selv.`;
}

export function biosUpdateConfirmOk(typed: string, target: string): boolean {
  return typed.trim().toUpperCase() === target.toUpperCase();
}

/** Linjen for kørslen. */
export function biosUpdateState(run: Pick<BiosUpdateRun, 'effective' | 'targetVersion' | 'fromVersion'>): Shown {
  const t = run.targetVersion;
  const by: Record<BiosUpdateEffective, Shown> = {
    checking: { text: 'Tjekker forudsætningerne …', tone: 'blue' },
    checked: { text: 'Klar til opdatering', tone: 'green' },
    blocked: { text: 'Kan ikke opdateres lige nu. Se forudsætningerne.', tone: 'yellow' },
    staging: { text: `Tager en kopi af indstillingerne og lægger ${t} klar …`, tone: 'blue' },
    staged: { text: `${t} ligger klar. Den installeres ved næste genstart.`, tone: 'blue' },
    rebooting: { text: 'Genstarter for at installere BIOS-opdateringen. Sluk ikke for strømmen.', tone: 'yellow' },
    awaiting_report: { text: 'Maskinen er genstartet. Venter på agentens melding …', tone: 'blue' },
    no_report: { text: 'Ingen melding efter genstarten. Maskinen kan vente på F1 i BIOS\'en (fejl 0162).', tone: 'red' },
    stale: { text: 'Agenten har ikke meldt tilbage. Kontrollér maskinen.', tone: 'red' },
    updated: { text: `BIOS'en er opdateret til ${t}.`, tone: 'green' },
    rejected: { text: 'Firmwaren afviste opdateringen. Brug USB-stikket i stedet.', tone: 'red' },
    not_applied: { text: `BIOS'en er stadig ${run.fromVersion ?? 'den gamle version'}. Opdateringen blev ikke kørt ved genstarten.`, tone: 'yellow' },
    unknown: { text: 'Versionen er ikke den forventede. Kontrollér maskinen.', tone: 'yellow' },
    failed: { text: 'BIOS-opdateringen mislykkedes. Intet er ændret.', tone: 'red' },
  };
  return by[run.effective] ?? { text: run.effective, tone: 'gray' };
}

export function biosCheckTone(c: BiosUpdateCheck): Tone {
  return c.state === 'ok' ? 'green' : c.state === 'warn' ? 'yellow' : 'red';
}

/** Forudsætninger: de der stopper først, så advarsler, så det der er i orden. */
export function biosChecksSorted(checks: BiosUpdateCheck[]): BiosUpdateCheck[] {
  const rank = { fail: 0, warn: 1, ok: 2 } as const;
  return [...checks].sort((a, b) => rank[a.state] - rank[b.state]);
}
