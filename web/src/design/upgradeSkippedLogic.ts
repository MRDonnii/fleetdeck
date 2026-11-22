// Den rene logik bag "Sprunget over i Opdatér alt" (uden React, så den kan testes med npm test).

/** Grupperne fra serveren (Services/UpgradeAllReport.cs), i den rækkefølge de vises. */
export type SkippedGroup = 'silent' | 'user' | 'fail' | 'blocked' | 'separate' | 'other' | 'ignored';
export interface SkippedItem { name: string; wingetId: string; group: SkippedGroup; reason: string; advice?: string | null; action?: 'upgrade' | 'amd' | null }
export interface UpgradeAllResult { jobId: number; status: string; finished: string | null; updated: number | null; skipped: SkippedItem[]; hideable: string[] }

export const SKIPPED_GROUPS: [SkippedGroup, string][] = [
  ['silent', 'Kan ikke opdateres stille'],
  ['user', 'Kræver brugerens session'],
  ['fail', 'Fejlede'],
  ['blocked', 'Kan ikke installeres'],
  ['separate', 'Installeres for sig'],
  ['other', 'Andet'],
  ['ignored', 'Ignoreret'],
];

/** Grupperet efter grund, i fast rækkefølge; tomme grupper udelades. */
export function groupSkipped(items: SkippedItem[]) {
  return SKIPPED_GROUPS.map(([group, label]) => ({ group, label, items: items.filter((x) => x.group === group) })).filter((x) => x.items.length > 0);
}

/** Teksten på knappen ud for en linje, der blev sprunget over (null = ingen knap). */
export function skippedButton(x: SkippedItem): string | null {
  if (!x.action) return null;
  if (x.action === 'amd') return 'Installér driver';
  if (x.wingetId.startsWith('fwupd:')) return 'Opdatér firmware';
  if (x.group === 'user' || x.group === 'fail' || x.group === 'other') return 'Prøv igen';
  return 'Installér';
}

/**
 * Kan opdateringen skjules? Windows Update skjules på pc'en (et job); på Mac, Linux og Unraid skjuler Fleetdeck selv alt
 * (firmware, macOS, pakker …). Andre Windows-programmer har "Opdatér aldrig" i stedet.
 */
export const canHide = (id: string, platform?: string) => id.startsWith('wu:') || (!!platform && platform !== 'windows');

/** Skjules den i Fleetdeck selv (med det samme) – ikke i Windows Update på pc'en? */
export const hiddenInFleetdeck = (id: string) => !id.startsWith('wu:');

/** Teksten i "Skjul …?"-dialogen. */
export function hideText(ids: string[]): string {
  const one = ids.length === 1;
  const it = one ? 'Den' : 'De';
  if (ids.every(hiddenInFleetdeck))
    return `${one ? 'Opdateringen' : 'Opdateringerne'} skjules i Fleetdeck. ${it} tilbydes ikke længere, tæller ikke med som ventende, og "Opdatér alt" springer ${one ? 'den' : 'dem'} over. Det huskes, indtil der kommer en ny version.`;
  return `${one ? 'Opdateringen' : 'Opdateringerne'} skjules i Windows Update på pc'en. ${it} tilbydes ikke længere, tæller ikke med som ventende, og "Opdatér alt" ser ${one ? 'den' : 'dem'} ikke.`;
}

/** Må linjen i "sprunget over" have en Skjul-knap? Dem, der anbefales ikke, og dem, der ikke kan installeres. */
export const skippedHideable = (x: SkippedItem, platform?: string) => (!!x.advice || x.group === 'blocked') && canHide(x.wingetId, platform);

/** Teksten på knappen "Skjul …, der anbefales ikke". */
export const hideAllLabel = (n: number) => n === 1 ? 'Skjul den, der anbefales ikke' : `Skjul de ${n}, der anbefales ikke`;
