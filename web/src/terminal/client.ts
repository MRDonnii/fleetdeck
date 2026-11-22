import { ApiError, CSRF_HEADER } from '../api';

/** Oplåsningen ligger kun i hukommelsen: genindlæsning af siden kræver en ny kode. */
let unlockToken: string | null = null;
export const setUnlock = (t: string | null) => { unlockToken = t; };
export const hasUnlock = () => unlockToken !== null;

let onLocked: (code: 'locked' | 'reauth') => void = () => {};
export const setLockedHandler = (f: typeof onLocked) => { onLocked = f; };

/** Fejl fra terminalens API med svarets indhold (fx hvilke maskiner der skal bekræftes). */
export class TermError extends ApiError { constructor(status: number, message: string, public data: unknown) { super(status, message); } }

export async function term<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { ...CSRF_HEADER };
  if (unlockToken) headers['X-Terminal-Unlock'] = unlockToken;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const r = await fetch(`api/terminal/${path}`, { method, credentials: 'same-origin', headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await r.text();
  let data: unknown = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!r.ok) {
    const d = data as { detail?: string; title?: string; code?: string } | null;
    if (r.status === 423 && (d?.code === 'locked' || d?.code === 'reauth')) onLocked(d.code);
    throw new TermError(r.status, d?.detail ?? d?.title ?? `Fejl ${r.status}`, data);
  }
  return data as T;
}

export interface TermMachine { id: number; name: string; platform: string; online: boolean; favorite: boolean; protected: boolean; group?: string | null; unavailable?: string | null }
export interface TermHost {
  id: number; name: string; address: string; port: number; username: string; group?: string | null; favorite: boolean; protected: boolean;
  credentialId?: number | null; hostKeyFingerprint?: string | null; pendingKeyFingerprint?: string | null; lastConnected?: string | null;
}
export interface TermCredential { id: number; name: string; kind: 'key' | 'password'; publicKey?: string | null; fingerprint?: string | null; hosts: number; lastUsed?: string | null }
export interface Targets { machines: TermMachine[]; hosts: TermHost[] }
export interface OpenSession { id: string; ticket: string; label: string; protected: boolean; platform?: string | null }
export interface TermSnippet { id: number; name: string; command: string; description: string; platform: string; risky: boolean; risk: boolean; variables: string[] }

export const FRAME = { data: 0, resize: 1, exit: 2, status: 3 } as const;

export function resizeFrame(cols: number, rows: number) {
  const b = new Uint8Array(5); const v = new DataView(b.buffer);
  v.setUint8(0, FRAME.resize); v.setUint16(1, cols); v.setUint16(3, rows);
  return b;
}
const enc = new TextEncoder();
export function dataFrame(text: string) {
  const t = enc.encode(text); const b = new Uint8Array(t.length + 1); b[0] = FRAME.data; b.set(t, 1);
  return b;
}
export const wsUrl = (s: OpenSession) => {
  const u = new URL(`api/terminal/ws/${s.id}`, document.baseURI);
  u.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:'; u.searchParams.set('ticket', s.ticket);
  return u.toString();
};
