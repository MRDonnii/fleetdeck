// TPM-afsnittet på maskinens side: tekster og farver (rene funktioner, testet i tests/tpm.test.ts).

export interface TpmInfo {
  present: boolean; version?: string | null; manufacturerId?: string | null; manufacturer?: string | null; firmware?: string | null;
  kind?: 'discrete' | 'firmware' | 'virtual' | null; enabled?: boolean | null; activated?: boolean | null; owned?: boolean | null; ready?: boolean | null;
  attention?: string[] | null; apple?: string | null; source?: string | null; error?: string | null;
}
export interface TpmEncryption { kind: string; volumes: { mount: string; system: boolean; encrypted?: boolean | null; usesTpm?: boolean | null }[] }
export type Tone = 'green' | 'red' | 'yellow' | 'gray';
export interface Shown { text: string; tone: Tone }

export const TPM_KIND: Record<string, string> = { discrete: 'Separat chip', firmware: 'Firmware-TPM (i processoren)', virtual: 'Virtuel' };

/** Til stede: Ja / Ingen TPM / Ikke relevant (Mac) / Kunne ikke læses. */
export function tpmPresent(t: TpmInfo, platform: string): Shown {
  if (t.error) return { text: 'Kunne ikke læses', tone: 'gray' };
  if (t.present) return { text: 'Ja', tone: 'green' };
  if (platform === 'mac') return { text: t.apple ? `Ikke relevant – ${t.apple}` : 'Ingen TPM', tone: 'gray' };
  return { text: 'Ingen TPM', tone: platform === 'windows' ? 'red' : 'gray' };
}

/** Producent med id, fx "Intel (INTC)". */
export function tpmManufacturer(t: TpmInfo): string | null {
  if (!t.manufacturerId) return t.manufacturer ?? null;
  return t.manufacturer && t.manufacturer !== t.manufacturerId ? `${t.manufacturer} (${t.manufacturerId})` : t.manufacturerId;
}

/** Klar: Klar / Slået fra / Ikke klar. Null = ukendt (fx Linux med TPM 2.0). */
export function tpmReady(t: TpmInfo): Shown | null {
  if (!t.present) return null;
  if (t.enabled === false || t.activated === false) return { text: 'Slået fra', tone: 'red' };
  if (t.ready === false) return { text: 'Ikke klar', tone: 'red' };
  if (t.ready === true) return { text: t.attention?.length ? 'Klar, men se bemærkning' : 'Klar', tone: t.attention?.length ? 'yellow' : 'green' };
  return null;
}

/** Bruges af BitLocker (systemdrevet). Null = ikke Windows eller BitLocker ikke tjekket. */
export function tpmBitLocker(enc: TpmEncryption | null | undefined, platform: string): Shown | null {
  if (platform !== 'windows' || !enc || enc.kind !== 'bitlocker') return null;
  const sys = enc.volumes.find((v) => v.system) ?? enc.volumes[0];
  if (!sys || sys.encrypted !== true) return { text: 'Nej – BitLocker er ikke slået til', tone: 'gray' };
  if (sys.usesTpm === true) return { text: `Ja (${sys.mount})`, tone: 'green' };
  if (sys.usesTpm === false) return { text: `Nej – ${sys.mount} låses op uden TPM`, tone: 'yellow' };
  return { text: 'Ukendt', tone: 'gray' };
}
