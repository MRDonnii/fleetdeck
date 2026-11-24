// Fremdrift for et kørende job: live fra serveren eller fra job-listen – den nyeste vinder.
// Serveren sender ingen tidsstempler med, så "hvornår" er det tidspunkt, browseren fik værdien (live: beskeden kom; job-listen: værdien ændrede sig).

export type ProgressValue = { progress: number | null; step: string; stepSince?: string | null };
export type Stamped = ProgressValue & { at: number };

/** Inden for samme trin går fremdriften kun op: den højeste vinder. Ellers vinder den nyeste. */
export function pickProgress(live: Stamped | undefined, api: Stamped): ProgressValue {
  if (!live) return api;
  if (live.step === api.step && live.progress !== null && api.progress !== null)
    return api.progress > live.progress ? api : live;
  return api.at > live.at ? api : live;
}
