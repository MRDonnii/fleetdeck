// Kan denne browser flashe ESP over USB – og hvis ikke, hvad skal brugeren gøre? Ren logik, så den kan testes (npm test).

/** Svarer Fleetdeck-agenten på http://localhost:porten (ukendt, mens der spørges)? */
export type AgentProbe = 'checking' | 'yes' | 'no';

/**
 * Hvad ESP-siden skal vise øverst:
 * usb    – sikker side og Web Serial: klar til USB
 * mobile – telefon/tablet: skal gøres fra en pc
 * browser – sikker side, men browseren kan ikke tale med USB (fx Firefox/Safari)
 * local  – usikker side (http via serveren), og agenten svarer på denne pc: "Åbn ESP Flasher på denne pc"
 * choose – usikker side, og agenten svarede ikke (eller browseren spærrede for at spørge): tre ligeværdige valg – åbn gennem agenten alligevel, installér agenten, eller brug https-adressen med certifikat
 */
export type EspEnv = 'usb' | 'mobile' | 'browser' | 'local' | 'choose';

export function espEnv(o: { canUsb: boolean; mobile: boolean; secure: boolean; agent: AgentProbe }): EspEnv {
  if (o.canUsb) return 'usb';
  if (o.mobile) return 'mobile';
  if (o.secure) return 'browser';
  // mens der spørges, vises vejen gennem agenten (den almindelige); svarer den ikke, vises de to valg
  return o.agent === 'no' ? 'choose' : 'local';
}

/** Agentens egen adresse på pc'en. /_fd/… besvares af agenten selv (403/405), så serveren spørges ikke. */
export const agentProbeUrl = (port: number) => `http://localhost:${port}/_fd/ping`;

/**
 * Spørg agenten på denne pc. Et svar af enhver slags (også 403) betyder, at den kører; en afvist forbindelse eller tidsgrænse betyder,
 * at den ikke gør (eller at browseren ikke må spørge – så kan brugeren stadig vælge "åbn alligevel").
 */
export async function probeAgent(port: number, timeoutMs = 2500, f: typeof fetch = fetch): Promise<AgentProbe> {
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), timeoutMs);
  try {
    await f(agentProbeUrl(port), { mode: 'no-cors', cache: 'no-store', credentials: 'omit', signal: ac.signal });
    return 'yes';
  } catch {
    return 'no';
  } finally {
    clearTimeout(timer);
  }
}
