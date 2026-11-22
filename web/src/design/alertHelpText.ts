// Tekster til alarmkortene (design A: "Sådan løser du det" under fejlen). Ren logik, så den kan testes med node (web/tests/alertHelp.test.ts).

export type FixJobStatus = 'pending' | 'running' | 'success' | 'failed' | 'cancelled' | 'waiting_user' | 'expired' | 'restarting' | 'hung' | 'partial';

export interface AlertHelpInput {
  severity: 'critical' | 'warning' | 'info';
  detail: string;
  why: string | null;
  lastSeen: string;
  resolved: string | null;
  steps?: string[] | null;
  fix: { id: string; title: string; button?: string | null } | null;
  fixJob: { id: number; status: FixJobStatus; finished: string | null; createdBy: string } | null;
}

/** "3 t", "12 min", "2 d" – kort tid siden ts (til "Siden 3 t" og "for 3 t siden"). */
export function shortAge(ts: string | null | undefined, now = Date.now()): string {
  if (!ts) return '';
  const s = Math.max(0, (now - new Date(ts).getTime()) / 1000);
  if (s < 60) return 'lige nu';
  if (s < 3600) return `${Math.floor(s / 60)} min`;
  if (s < 86400) return `${Math.floor(s / 3600)} t`;
  return `${Math.floor(s / 86400)} d`;
}

/** "for 3 t siden" / "lige nu". */
export function agoText(ts: string | null | undefined, now = Date.now()): string {
  const a = shortAge(ts, now);
  return !a ? '' : a === 'lige nu' ? 'lige nu' : `for ${a} siden`;
}

/** Overskriftens tid: "Siden 3 t", "Løst for 2 t siden". */
export function sinceText(firstSeen: string, resolved: string | null, now = Date.now()): string {
  if (resolved) return `Løst ${agoText(resolved, now)}`;
  const a = shortAge(firstSeen, now);
  return a === 'lige nu' ? 'Lige nu' : `Siden ${a}`;
}

/** Hvem der kørte rettelsen, som folk forstår det: hjælperens automatiske kørsler hedder "Fleetdeck". */
export function whoText(createdBy: string): string {
  if (/^hjælper/i.test(createdBy)) return 'Fleetdeck';
  return createdBy || 'ukendt';
}

/** Knappen: rettelsens egen tekst ("Start tjenesten", "Tag backup nu" …) med et tegn foran. */
export function fixButton(fix: AlertHelpInput['fix']): { icon: string; label: string } | null {
  if (!fix) return null;
  const label = fix.button || 'Ret det';
  return { icon: /^(Start|Tænd|Scan|Analysér)/.test(label) ? '▶' : '✓', label };
}

/** Et trin delt op, så tekst i anførselstegn ("Ret det", "Hold håndhævet") kan vises med fed. */
export function stepParts(step: string): { text: string; bold: boolean }[] {
  const out: { text: string; bold: boolean }[] = [];
  const re = /"([^"]+)"/g;
  let last = 0;
  for (let m = re.exec(step); m; m = re.exec(step)) {
    if (m.index > last) out.push({ text: step.slice(last, m.index), bold: false });
    out.push({ text: m[1], bold: true });
    last = m.index + m[0].length;
  }
  if (last < step.length) out.push({ text: step.slice(last), bold: false });
  return out;
}

/** Trinene: serverens, ellers ingen (ældre server uden "steps"). */
export function stepsOf(a: Pick<AlertHelpInput, 'steps'>): string[] {
  return (a.steps ?? []).filter((s) => s.trim().length > 0);
}

export type FixTone = 'running' | 'ok' | 'failed' | 'info';

/** Status for den seneste rettelse: kører / "✓ Rettet af … for X siden" / fejlet + hvad man så gør. Null = ingen rettelse kørt. */
export function fixStatus(a: Pick<AlertHelpInput, 'fixJob' | 'resolved' | 'lastSeen'>, now = Date.now()): { tone: FixTone; text: string } | null {
  const j = a.fixJob;
  if (!j) return null;
  const who = whoText(j.createdBy);
  const ago = agoText(j.finished, now);
  const when = ago ? ` ${ago}` : '';
  switch (j.status) {
    case 'pending':
      return { tone: 'running', text: `Venter på maskinen – rettelsen starter, når den melder sig (bestilt af ${who}).` };
    case 'running':
    case 'restarting':
      return { tone: 'running', text: `Rettelsen kører … (startet af ${who})` };
    case 'waiting_user':
      return { tone: 'info', text: 'Rettelsen venter på, at en bruger logger ind på maskinen.' };
    case 'success': {
      if (a.resolved) return { tone: 'ok', text: `✓ Rettet af ${who}${when}` };
      // fejlen er set igen et stykke tid efter, at rettelsen var færdig: den hjalp ikke
      const still = j.finished && new Date(a.lastSeen).getTime() - new Date(j.finished).getTime() > 10 * 60_000;
      return still
        ? { tone: 'failed', text: `Rettelsen kørte${when} (${who}), men fejlen er her stadig. Følg trinene ovenfor, eller åbn jobbet og se, hvad der skete.` }
        : { tone: 'ok', text: `✓ Rettelsen kørte${when} (${who}). Alarmen forsvinder, når maskinen melder sig næste gang.` };
    }
    case 'partial':
      return { tone: 'failed', text: `Rettelsen lykkedes kun delvist${when}. Åbn jobbet og se, hvad der mangler, og prøv igen.` };
    case 'expired':
      return { tone: 'failed', text: 'Maskinen hentede ikke rettelsen i tide. Prøv igen, når den er online.' };
    case 'cancelled':
      return { tone: 'info', text: `Rettelsen blev annulleret${when}.` };
    case 'failed':
    case 'hung':
    default:
      return { tone: 'failed', text: `✗ Rettelsen fejlede${when} (${who}). Åbn jobbet og se hvorfor – prøv så igen, eller følg trinene ovenfor.` };
  }
}

/** Kort forklaring til forsidens "Kræver opmærksomhed": første sætning af hvorfor, ellers detaljen. */
export function shortWhy(a: Pick<AlertHelpInput, 'why' | 'detail'>): string {
  const t = (a.why ?? '').trim() || a.detail;
  const m = /^(.+?[.!?])(\s|$)/.exec(t);
  return m ? m[1] : t;
}
