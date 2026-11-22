import type { HealthState, Intent, JobStatus, PackageType, RunAs, Severity } from './types';

export function ago(ts: string | null | undefined): string {
  if (!ts) return 'aldrig';
  const s = (Date.now() - new Date(ts).getTime()) / 1000;
  if (s < 60) return 'lige nu';
  if (s < 3600) return `${Math.floor(s / 60)} min. siden`;
  if (s < 86400) return `${Math.floor(s / 3600)} t. siden`;
  return `${Math.floor(s / 86400)} d. siden`;
}
export function fmt(ts: string | null | undefined): string {
  if (!ts) return '–';
  return new Date(ts).toLocaleString('da-DK', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}
export function duration(a: string | null, b: string | null): string {
  if (!a || !b) return '';
  const s = Math.round((new Date(b).getTime() - new Date(a).getTime()) / 1000);
  return s < 60 ? `${s} s` : s < 3600 ? `${Math.floor(s / 60)} m ${s % 60} s` : `${Math.floor(s / 3600)} t ${Math.floor((s % 3600) / 60)} m`;
}
export const num = (v: number | null | undefined, d = 0) => (v === null || v === undefined ? '–' : v.toFixed(d));

type Lbl = { label: string; color: string };
export const JOB: Record<JobStatus, Lbl> = {
  pending: { label: 'I kø', color: 'blue' }, running: { label: 'Kører', color: 'cyan' }, success: { label: 'OK', color: 'green' },
  failed: { label: 'Fejlede', color: 'red' }, cancelled: { label: 'Annulleret', color: 'gray' },
  waiting_user: { label: 'Venter på brugeren', color: 'yellow' }, expired: { label: 'Udløbet', color: 'gray' },
  restarting: { label: 'Genstarter…', color: 'blue' }, hung: { label: 'Hænger', color: 'red' },
  partial: { label: 'Delvist', color: 'orange' },
};
export const HEALTH: Record<HealthState, Lbl> = {
  ok: { label: 'Sund', color: 'green' }, warning: { label: 'Advarsel', color: 'yellow' }, critical: { label: 'Kritisk', color: 'red' }, unknown: { label: 'Ukendt', color: 'gray' },
};
export const SEV: Record<Severity, Lbl> = { critical: { label: 'Kritisk', color: 'red' }, warning: { label: 'Advarsel', color: 'yellow' }, info: { label: 'Info', color: 'cyan' } };
export const COMP: Record<string, Lbl> = {
  ok: { label: 'OK', color: 'green' }, missing: { label: 'Mangler', color: 'yellow' }, failed: { label: 'Fejlet', color: 'red' },
  pending: { label: 'I gang', color: 'blue' }, unknown: { label: 'Ukendt', color: 'gray' }, present: { label: 'Skal fjernes', color: 'yellow' },
  installed: { label: 'Installeret', color: 'green' }, available: { label: 'Ikke installeret', color: 'gray' },
};
export const INTENT: Record<Intent, string> = { required: 'Påkrævet', available: 'Tilgængelig', uninstall: 'Afinstallér' };
export const RUNAS: Record<RunAs, string> = { system: 'System', user: 'Bruger', user_admin: 'Bruger (admin)' };
export const SOURCE: Record<string, string> = { manual: 'Manuel', compliance: 'Overholdelse', schedule: 'Planlagt', provision: 'Nyinstallation', helper: 'Hjælper', maintenance: 'Vedligeholdelse', debloat: 'Debloat' };
export const PKGTYPE: Record<PackageType, Lbl> = {
  winget: { label: 'winget', color: 'blue' }, msstore: { label: 'Store', color: 'cyan' }, file: { label: 'Fil', color: 'gray' },
  steam: { label: 'Steam', color: 'indigo' }, uri: { label: 'Launcher', color: 'violet' },
  apt: { label: 'Linux-pakke', color: 'orange' }, pkg: { label: 'Linux-pakke', color: 'orange' }, snap: { label: 'snap (Linux)', color: 'orange' },
  flatpak: { label: 'Flatpak (Linux)', color: 'orange' }, brew: { label: 'Homebrew (Mac)', color: 'gray' }, mas: { label: 'App Store (Mac)', color: 'gray' },
};
export const DAYS = ['Mandag', 'Tirsdag', 'Onsdag', 'Torsdag', 'Fredag', 'Lørdag', 'Søndag'];
export const STEPS: Record<string, string> = {
  Capture: 'Gem baggrund og opsætning', CaptureDrivers: 'Gem drivere', Backup: 'Backup af filer og indstillinger',
  Restore: 'Gendan filer og indstillinger', Appearance: 'Læg baggrund og opsætning på', Drivers: 'Installér drivere',
};
export const options = (m: Record<string, string | Lbl>) =>
  Object.entries(m).map(([value, l]) => ({ value, label: typeof l === 'string' ? l : l.label }));

/** "kl. 12:31" i dag, "i går kl. 12:31", ellers "15. nov. kl. 12:31". */
export function clockAt(ts: string | null | undefined): string {
  if (!ts) return '–';
  const d = new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(ts) ? ts : ts + 'Z');
  const t = d.toLocaleTimeString('da-DK', { hour: '2-digit', minute: '2-digit' });
  const today = new Date(); const y = new Date(); y.setDate(today.getDate() - 1);
  if (d.toDateString() === today.toDateString()) return `kl. ${t}`;
  if (d.toDateString() === y.toDateString()) return `i går kl. ${t}`;
  return `${d.toLocaleDateString('da-DK', { day: 'numeric', month: 'short' })} kl. ${t}`;
}

/** "Online siden kl. 08:12" / "Offline siden i går kl. 22:40 (14 t. siden)". */
export function presence(m: { online: boolean; lastSeen: string | null; onlineSince?: string | null }): string {
  return m.online ? `Online siden ${clockAt(m.onlineSince ?? m.lastSeen)}` : `Offline siden ${clockAt(m.lastSeen)} (${ago(m.lastSeen)})`;
}
