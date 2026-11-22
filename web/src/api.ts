import { useEffect, useSyncExternalStore } from 'react';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { QueryClient, useMutation, useQuery, useQueryClient, type QueryKey } from '@tanstack/react-query';
import { notifications } from '@mantine/notifications';

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

let onUnauthorized: () => void = () => {};
export const setUnauthorizedHandler = (f: () => void) => { onUnauthorized = f; };
/** 403: fx fordi totrinsbekræftelse er blevet påkrævet, mens siden var åben – så hentes /me igen. */
let onForbidden: () => void = () => {};
export const setForbiddenHandler = (f: () => void) => { onForbidden = f; };

/** CSRF-værn: serveren afviser cookie-godkendte POST/PUT/DELETE uden denne header (andre sider kan ikke sætte den). */
export const CSRF_HEADER = { 'X-Fleetdeck': '1' } as const;

export async function api<T = unknown>(method: string, url: string, body?: unknown): Promise<T> {
  const init: RequestInit = { method, credentials: 'same-origin', headers: { ...CSRF_HEADER } };
  if (body instanceof FormData) init.body = body;
  else if (body !== undefined) { (init.headers as Record<string, string>)['Content-Type'] = 'application/json'; init.body = JSON.stringify(body); }
  // relativt til <base href> (understi bag Kontrolrum)
  const r = await fetch(url.replace(/^\//, ''), init);
  if (r.status === 401 && !url.startsWith('/api/auth/')) { onUnauthorized(); throw new ApiError(401, 'Ikke logget ind'); }
  if (r.status === 403) onForbidden();
  const text = await r.text();
  let data: unknown = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!r.ok) {
    const d = data as { detail?: string; title?: string } | string | null;
    const msg = typeof d === 'string' ? d : d?.detail ?? d?.title ?? r.statusText;
    throw new ApiError(r.status, msg || `Fejl ${r.status}`);
  }
  return data as T;
}
export const get = <T,>(url: string) => api<T>('GET', url);
export const post = <T = unknown,>(url: string, body: unknown = {}) => api<T>('POST', url, body);
export const put = <T = unknown,>(url: string, body: unknown) => api<T>('PUT', url, body);
export const del = <T = unknown,>(url: string) => api<T>('DELETE', url);

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: (n, e) => !(e instanceof ApiError && e.status < 500) && n < 2, staleTime: 5_000, refetchOnWindowFocus: true },
  },
});

export const useApi = <T,>(key: QueryKey, url: string | null, opts: { refetchInterval?: number } = {}) =>
  useQuery<T>({ queryKey: key, queryFn: () => get<T>(url!), enabled: url !== null, ...opts });

export function notifyOk(message: string) { notifications.show({ color: 'green', message }); }
export function notifyErr(e: unknown) { notifications.show({ color: 'red', title: 'Fejl', message: e instanceof Error ? e.message : String(e) }); }

/** Mutation der viser fejl/succes og opdaterer alle forespørgsler bagefter. */
export function useAction<TArgs, TRes = unknown>(fn: (a: TArgs) => Promise<TRes>, ok?: string | ((r: TRes) => string)) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: (r) => { if (ok) notifyOk(typeof ok === 'function' ? ok(r) : ok); qc.invalidateQueries(); },
    onError: notifyErr,
  });
}

/** Fremdrift for kørende jobs, som agenterne sender løbende (uden at hente alle data igen). */
type LiveProgress = { progress: number | null; step: string; stepSince?: string | null };
const progressMap = new Map<number, LiveProgress>();
const progressSubs = new Set<() => void>();
function setProgress(id: number, p: LiveProgress) {
  progressMap.set(id, p);
  progressSubs.forEach((f) => f());
}
const subscribeProgress = (f: () => void) => { progressSubs.add(f); return () => { progressSubs.delete(f); }; };

/** Seneste fremdrift for et job: live fra serveren, ellers det der kom med job-listen. */
export function useJobProgress(id: number, progress: number | null | undefined, step: string | undefined, stepSince?: string | null): LiveProgress {
  const live = useSyncExternalStore(subscribeProgress, () => progressMap.get(id));
  return live ?? { progress: progress ?? null, step: step ?? '', stepSince };
}

/** Live-opdateringer: serveren sender "Changed", og vi henter de berørte data igen. */
export function useLiveUpdates(enabled: boolean) {
  const qc = useQueryClient();
  useEffect(() => {
    if (!enabled) return;
    const conn = new HubConnectionBuilder().withUrl('hubs/ui').withAutomaticReconnect([0, 2000, 5000, 10000, 30000]).configureLogging(LogLevel.Warning).build();
    let timer: number | undefined;
    conn.on('Progress', (m: { jobId: number; progress: number | null; step: string; stepSince?: string | null }) =>
      setProgress(m.jobId, { progress: m.progress, step: m.step, stepSince: m.stepSince }));
    conn.on('Changed', () => {
      window.clearTimeout(timer);
      timer = window.setTimeout(() => qc.invalidateQueries(), 400);   // saml mange ændringer i én opdatering
    });
    conn.start().catch(() => {});
    return () => { window.clearTimeout(timer); conn.stop(); };
  }, [enabled, qc]);
}
