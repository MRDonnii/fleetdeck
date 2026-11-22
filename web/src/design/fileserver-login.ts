// Login til filserveren: én bruger (share_user/share_pass), som netværksdrev uden eget login og WinSetup-sharen bruger.
// Nøglerne er de samme som før, så eksisterende installationer virker uændret – kun stedet i Indstillinger er nyt.

/** Fanen i Indstillinger, hvor brugeren skrives. */
export const FILESERVER_TAB = 'drev';
export const FILESERVER_LINK = `/indstillinger?fane=${FILESERVER_TAB}`;

export type FileServerLogin = { state: 'ok' | 'nopass' | 'none'; user: string };

/** Hvad drevene logger ind med, ud fra indstillingerne (share_user + om der er gemt en adgangskode). */
export function fileServerLogin(s: { share_user?: string; hasSharePass?: boolean } | undefined): FileServerLogin {
  const user = (s?.share_user ?? '').trim();
  if (!user) return { state: 'none', user };
  return { state: s?.hasSharePass ? 'ok' : 'nopass', user };
}
