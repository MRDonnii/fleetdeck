// Live-forbindelsen (SignalR) skal komme igen af sig selv: også efter første fejl, og uden at give op efter et antal forsøg.

/** Længste pause mellem to forsøg. */
export const LIVE_MAX_DELAY = 60_000;
const STEPS = [0, 2000, 5000, 10000, 30000];

/** Ventetid før forsøg nr. <attempt> (0 = med det samme), højst 60 sekunder – og aldrig "giv op". */
export const liveRetryDelay = (attempt: number) => Math.min(LIVE_MAX_DELAY, STEPS[attempt] ?? LIVE_MAX_DELAY);

type Timers = { set: (f: () => void, ms: number) => number; clear: (id: number | undefined) => void };
const browserTimers: Timers = { set: (f, ms) => window.setTimeout(f, ms), clear: (id) => window.clearTimeout(id) };

/**
 * Hold forbindelsen oppe. <code>kick()</code> prøver med det samme (første start, 'online', fanen synlig igen),
 * <code>closed()</code> kaldes når forbindelsen lukker, og så prøves der igen med voksende pause (højst 60 s).
 * <code>onUp</code> kaldes hver gang forbindelsen er oppe igen.
 */
export function keepLive(start: () => Promise<void>, isDown: () => boolean, onUp: () => void, timers: Timers = browserTimers) {
  let attempt = 0, timer: number | undefined, busy = false, stopped = false;
  const schedule = () => {
    if (stopped) return;
    timers.clear(timer);
    timer = timers.set(tryNow, liveRetryDelay(++attempt));
  };
  const tryNow = () => {
    timers.clear(timer); timer = undefined;
    if (stopped || busy || !isDown()) return;
    busy = true;
    start().then(
      () => { busy = false; attempt = 0; if (!stopped) onUp(); },
      () => { busy = false; schedule(); },
    );
  };
  return {
    kick: () => { attempt = 0; tryNow(); },
    closed: schedule,
    dispose: () => { stopped = true; timers.clear(timer); },
  };
}
