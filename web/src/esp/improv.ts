// Improv Wi-Fi over USB serial (https://www.improv-wifi.com/serial/): sender det gemte Wi-Fi til en nyflashet ESP,
// så ingen skal skrive koden i ESP Web Tools-vinduet. Overført fra ESP Flasher (site/improv.js).
//
// Pakke: "IMPROV" + version 1 + type + længde + data + kontrolsum (summen af alle foregående byte) + "\n".

/** Det lille udsnit af Web Serial, vi bruger (ikke i TypeScripts DOM-typer endnu). */
export interface SerialPortLike {
  open(o: { baudRate: number }): Promise<void>;
  close(): Promise<void>;
  setSignals?(s: { dataTerminalReady?: boolean; requestToSend?: boolean }): Promise<void>;
  readonly readable: ReadableStream<Uint8Array>;
  readonly writable: WritableStream<Uint8Array>;
}
export interface SerialLike { requestPort(): Promise<SerialPortLike> }
export const serialApi = (): SerialLike | undefined => (navigator as Navigator & { serial?: SerialLike }).serial;

const HEADER = [0x49, 0x4d, 0x50, 0x52, 0x4f, 0x56, 0x01];
const TYPE = { state: 0x01, error: 0x02, rpc: 0x03, result: 0x04 };
const CMD = { wifi: 0x01, state: 0x02 };
export const STATE: Record<number, string> = { 0x02: 'Enheden er klar', 0x03: 'Enheden forbinder til Wi-Fi …', 0x04: 'Enheden er på Wi-Fi' };
const ERRORS: Record<number, string> = {
  0x01: 'Ugyldig kommando', 0x02: 'Ukendt kommando',
  0x03: 'Kunne ikke komme på Wi-Fi – tjek navn og kode', 0x04: 'Ikke tilladt', 0xff: 'Ukendt fejl',
};

export function packet(type: number, data: number[]): Uint8Array {
  const out = [...HEADER, type, data.length, ...data];
  out.push(out.reduce((a, b) => (a + b) & 0xff, 0), 0x0a);
  return new Uint8Array(out);
}

export const rpc = (command: number, payload: number[] = []) => packet(TYPE.rpc, [command, payload.length, ...payload]);

/** Wi-Fi-kommandoen: [navnets længde, navn, kodens længde, kode] i UTF-8. */
export function wifiRpc(ssid: string, password: string): Uint8Array {
  const enc = new TextEncoder();
  const s = [...enc.encode(ssid)], p = [...enc.encode(password)];
  if (s.length > 32 || p.length > 64) throw new Error('Wi-Fi-navn eller -kode er for lang');
  return rpc(CMD.wifi, [s.length, ...s, p.length, ...p]);
}

export function strings(bytes: number[]): string[] {
  const out: string[] = [];
  for (let i = 0; i < bytes.length;) {
    const n = bytes[i++];
    out.push(new TextDecoder().decode(new Uint8Array(bytes.slice(i, i + n))));
    i += n;
  }
  return out;
}

/** Finder Improv-pakker i en rå bytestrøm (enheden skriver også sin almindelige log på samme port). */
export class ImprovParser {
  private buf: number[] = [];
  push(chunk: ArrayLike<number>): { type: number; data: number[] }[] {
    this.buf.push(...Array.from(chunk));
    const packets: { type: number; data: number[] }[] = [];
    for (;;) {
      const start = this.findHeader();
      if (start < 0) { this.buf = this.buf.slice(-HEADER.length); break; }
      if (start > 0) this.buf = this.buf.slice(start);
      if (this.buf.length < 9) break;
      const len = this.buf[8];
      if (this.buf.length < 10 + len) break;
      const body = this.buf.slice(0, 9 + len);
      const sum = body.reduce((a, b) => (a + b) & 0xff, 0);
      if (sum === this.buf[9 + len]) packets.push({ type: body[7], data: body.slice(9) });
      this.buf = this.buf.slice(10 + len);
    }
    return packets;
  }
  private findHeader(): number {
    for (let i = 0; i + HEADER.length <= this.buf.length; i++) if (HEADER.every((b, j) => this.buf[i + j] === b)) return i;
    return -1;
  }
}

export interface ProvisionResult { ok: boolean; message: string; urls?: string[] }

/** Sender navn/kode til ESP'en på `port` (lukket Web Serial-port). Lukker porten igen bagefter. */
export async function provisionWifi(port: SerialPortLike, ssid: string, password: string,
  onStatus: (s: string) => void = () => {}, timeoutMs = 45_000): Promise<ProvisionResult> {
  const wifi = wifiRpc(ssid, password);
  await port.open({ baudRate: 115200 });
  // Hold ikke chippen i reset/downloadtilstand (ESP32-C3/C6/H2 USB-serial følger DTR/RTS)
  try { await port.setSignals?.({ dataTerminalReady: false, requestToSend: false }); } catch { /* ikke alle adaptere */ }
  const writer = port.writable.getWriter();
  const reader = port.readable.getReader();
  const parser = new ImprovParser();
  const deadline = Date.now() + timeoutMs;
  const WAIT = Symbol('wait');
  let state: number | null = null, sentWifi = false, lastAsk = 0;
  let pending: Promise<ReadableStreamReadResult<Uint8Array>> | null = null;
  try {
    onStatus('Venter på enheden …');
    while (Date.now() < deadline) {
      if (!sentWifi && Date.now() - lastAsk > 1500) {   // spørg, til den svarer (den kan stadig være ved at starte)
        await writer.write(rpc(CMD.state));
        lastAsk = Date.now();
      }
      // én læsning ad gangen: en læsning, der taber kapløbet, venter og bruges næste gang (ingen tabte byte)
      if (!pending) pending = reader.read();
      const chunk = await Promise.race([pending, new Promise<typeof WAIT>((r) => setTimeout(() => r(WAIT), 500))]);
      if (chunk === WAIT) continue;
      pending = null;
      if (chunk.done) break;
      if (!chunk.value) continue;
      for (const pk of parser.push(chunk.value)) {
        if (pk.type === TYPE.state) {
          state = pk.data[0];
          onStatus(STATE[state] ?? 'Enheden svarer');
          if (state === 0x02 && !sentWifi) {
            onStatus(`Sender Wi-Fi "${ssid}" …`);
            await writer.write(wifi);
            sentWifi = true;
          }
        } else if (pk.type === TYPE.error && pk.data[0] !== 0x00) {
          return { ok: false, message: ERRORS[pk.data[0]] ?? 'Fejl fra enheden' };
        } else if (pk.type === TYPE.result) {
          const cmd = pk.data[0];
          const urls = strings(pk.data.slice(2));
          if (cmd === CMD.wifi || (cmd === CMD.state && state === 0x04))
            return { ok: true, urls, message: state === 0x04 && !sentWifi ? 'Enheden var allerede på Wi-Fi' : 'Enheden er på Wi-Fi' };
        }
      }
    }
    return { ok: false, message: sentWifi ? 'Enheden svarede ikke – tjek Wi-Fi-koden' : 'Enheden svarede ikke på Improv' };
  } finally {
    try { await reader.cancel(); } catch { /* ignorér */ }
    try { reader.releaseLock(); } catch { /* ignorér */ }
    try { writer.releaseLock(); } catch { /* ignorér */ }
    try { await port.close(); } catch { /* ignorér */ }
  }
}
