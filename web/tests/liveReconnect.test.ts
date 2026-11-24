// Test af live-forbindelsens genforsøg (web/src/liveReconnect.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { keepLive, liveRetryDelay, LIVE_MAX_DELAY } from '../src/liveReconnect.ts';

const fakeTimers = () => {
  let next = 1; const due = new Map<number, { at: number; f: () => void }>(); let now = 0;
  return {
    set: (f: () => void, ms: number) => { const id = next++; due.set(id, { at: now + ms, f }); return id; },
    clear: (id: number | undefined) => { if (id !== undefined) due.delete(id); },
    pending: () => [...due.values()].map((d) => d.at - now),
    advance: async (ms: number) => {
      now += ms;
      for (const [id, d] of [...due]) if (d.at <= now) { due.delete(id); d.f(); }
      await new Promise((r) => setImmediate(r));
    },
  };
};
const flush = () => new Promise((r) => setImmediate(r));

test('ventetiden vokser og stopper ved 60 sekunder – men giver aldrig op', () => {
  assert.equal(liveRetryDelay(0), 0);
  assert.equal(liveRetryDelay(3), 10000);
  assert.equal(liveRetryDelay(5), LIVE_MAX_DELAY);
  assert.equal(liveRetryDelay(500), 60000);
});

test('første forbindelsesfejl prøves igen, og efter mange fejl bliver den ved', async () => {
  const t = fakeTimers();
  let fails = 8, starts = 0, up = 0;
  const live = keepLive(async () => { starts++; if (fails-- > 0) throw new Error('nede'); }, () => true, () => { up++; }, t);
  live.kick(); await flush();
  assert.equal(starts, 1);
  assert.deepEqual(t.pending(), [2000]);           // første fejl ignoreres ikke
  for (let i = 0; i < 7; i++) await t.advance(LIVE_MAX_DELAY);
  assert.equal(starts, 8);
  assert.deepEqual(t.pending(), [LIVE_MAX_DELAY]);  // stadig nede: højst 60 s mellem forsøg
  await t.advance(LIVE_MAX_DELAY);
  assert.equal(starts, 9); assert.equal(up, 1);
  assert.deepEqual(t.pending(), []);
  live.dispose();
});

test('lukket forbindelse startes igen, og online/synlig prøver med det samme', async () => {
  const t = fakeTimers();
  let down = false, starts = 0;
  const live = keepLive(async () => { starts++; if (down) throw new Error('nede'); }, () => true, () => {}, t);
  live.kick(); await flush();
  down = true; live.closed();
  assert.deepEqual(t.pending(), [2000]);
  await t.advance(2000);
  assert.equal(starts, 2);
  down = false; live.kick(); await flush();          // 'online' eller fanen bliver synlig igen
  assert.equal(starts, 3);
  assert.deepEqual(t.pending(), []);
  live.dispose(); live.closed();
  assert.deepEqual(t.pending(), []);                  // efter oprydning: ingen nye forsøg
});
