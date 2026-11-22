// Login, der lykkes hos serveren, men hvor browseren ikke gemmer/sender sessionen (ws_session) bagefter.

/** Kører siden inde i en iframe? (Et fremmed øverste vindue kan kaste ved adgang – så er vi også indlejret.) */
export function isFramed(w: { self: unknown; readonly top: unknown }): boolean {
  try { return w.top !== w.self; } catch { return true; }
}

/** Forklaringen, når login svarede OK, men /api/auth/me stadig siger "ikke logget ind". */
export function sessionLostText(framed: boolean): string {
  return framed
    ? 'Login lykkedes, men browseren gemmer ikke login, når Fleetdeck vises inde i en anden side. Åbn Fleetdeck i et nyt vindue og log ind dér.'
    : 'Login lykkedes, men browseren gemte ikke login-cookien. Tjek at cookies er tilladt for denne adresse, og prøv igen.';
}
