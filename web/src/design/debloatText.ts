// Tilstandens ord for et debloat-punkt – ren logik, så den kan testes (web/tests/debloat.test.ts).
export type StateKey = 'applied' | 'partial' | 'not_applied' | 'na' | 'error' | 'drift';
export interface StateWords { subject: string; stateOn: string; stateOff: string }

const FIXED: Record<'partial' | 'na' | 'error', string> = { partial: 'delvist', na: 'gælder ikke denne pc', error: 'fejl' };

/** Punktets egne ord: "fra", "vises", "fjernet" – og ved afvigelse det modsatte + "igen" ("skjult igen"). */
export function stateLabel(i: StateWords, st: StateKey): string {
  if (st === 'applied') return i.stateOn;
  if (st === 'not_applied') return i.stateOff;
  if (st === 'drift') return `${i.stateOff} igen`;
  return FIXED[st];
}

/** "Reklame-id: fra", "Filtypenavne: vises", "Clipchamp: installeret igen". */
export const stateText = (i: StateWords, st: StateKey) => `${i.subject}: ${stateLabel(i, st)}`;
