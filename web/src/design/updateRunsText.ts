// Opdateringsmotorens visning (rene funktioner, så de kan testes med npm test).
import type { MaintenanceRun, MaintState, UpdateRunReport } from '../types';

/** Opdateringsmotorens tilstand (MaintenanceRun.State) som mærkat. */
export const MAINT_STATE: Record<MaintState, { label: string; color: string }> = {
  waiting: { label: 'Venter på maskinen', color: 'gray' },
  running: { label: 'I gang', color: 'blue' },
  rebooting: { label: 'Genstarter', color: 'blue' },
  reboot_wait: { label: 'Venter på genstart', color: 'yellow' },
  hung: { label: 'Genstart hænger', color: 'red' },
  done: { label: 'Færdig – alt lykkedes', color: 'green' },
  partial: { label: 'Delvist', color: 'orange' },
  failed: { label: 'Fejlede', color: 'red' },
  offline: { label: 'Kom ikke online', color: 'gray' },
  skipped: { label: 'Ikke tændt', color: 'gray' },
  blocked: { label: 'Spærret', color: 'gray' },
  superseded: { label: 'Erstattet', color: 'gray' },
};

export const KIND: Record<string, string> = { maintenance: 'Vedligeholdelse', schedule: 'Planlagt opdatering', manual: 'Opdatér alt' };

export type Row = { text: string; note?: string };
export type Group = { key: 'failed' | 'waiting' | 'installed' | 'skipped'; title: string; color: string; rows: Row[] };

/** Resultatet i den rækkefølge, ejeren skal se det: først det, der fejlede, så det, der venter, så det installerede. */
export function reportGroups(rep: UpdateRunReport, active: boolean): Group[] {
  const groups: Group[] = [
    { key: 'failed', title: 'Fejlede', color: 'red', rows: rep.failed.map((f) => ({ text: f.title, note: f.code })) },
    { key: 'waiting', title: 'Venter', color: 'orange', rows: [
      ...rep.waiting.map((w) => ({ text: w.title, note: w.reason })),
      ...rep.remaining.map((t) => ({ text: t, note: active ? 'næste runde' : 'mangler stadig' })),
      ...(rep.rebootPending && !active ? [{ text: 'Genstart', note: 'mangler' }] : []),
    ] },
    { key: 'installed', title: 'Installeret', color: 'green', rows: rep.installed.map((t) => ({ text: t })) },
    { key: 'skipped', title: 'Sprunget over', color: 'dimmed', rows: rep.skipped.map((s) => ({ text: s.title, note: s.reason })) },
  ];
  return groups.filter((g) => g.rows.length > 0);
}

/** Runderne: færdige (done), den aktuelle (current) og dem, der er tilbage (todo). Når kørslen er slut, kun dem, der faktisk kørte. */
export function roundDots(run: Pick<MaintenanceRun, 'round' | 'maxRounds' | 'finished'>): ('done' | 'current' | 'todo')[] {
  const max = run.maxRounds ?? 4;
  const cur = Math.max(1, run.round ?? 1);
  if (run.finished) return Array.from({ length: Math.min(cur, max) }, () => 'done');
  return Array.from({ length: max }, (_, i) => (i + 1 < cur ? 'done' : i + 1 === cur ? 'current' : 'todo'));
}

/** Kører intet af sig selv? Vedligeholdelse tæller kun, hvis den også opdaterer (ikke kun oprydning). */
export function autoUpdateOff(p: { maintMode: string; maintTasks: string[]; upgradeMode: string; windowsUpdateMode: string }): boolean {
  const maintUpdates = p.maintMode !== 'off' && p.maintTasks.some((t) => t === 'windows_update' || t === 'upgrade');
  return !maintUpdates && p.upgradeMode === 'off' && p.windowsUpdateMode === 'off';
}

/** Profilens tekst om genstart – den skal passe til reglerne i MaintenanceService.RebootParams. */
export function rebootRuleText(noWarning: boolean): string {
  return (noWarning
    ? 'Genstart om natten sker uden varsel (kun lige efter vinduet); kommer den senere, eller er det en indhentning, kun når ingen har brugt pc\'en de sidste 15 minutter.'
    : 'Genstart og sluk sker kun, når ingen har brugt pc\'en de sidste 15 minutter.')
    + ' "Opdatér alt (automatisk)" giver altid 5 minutters nedtælling før hver genstart. I stilletiden genstartes der aldrig – genstarten venter, til stilletiden slutter.';
}

/** Er resultatet grønt? Kun når alt lykkedes. */
export const allGood = (run: Pick<MaintenanceRun, 'state'>) => run.state === 'done';
