// Test af fejlloggen (web/src/design/syslogText.ts): forklaringer, titler, tider, opsummering, filtre og live-rammer. Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  appendLive, byArea, countText, groupTitle, heartbeatFrame, KNOWN, known, LIVE_MAX, liveMatches, liveText, logQuery, parseLiveFrame, rangeText, sourceLabel,
  summarize, when, type FleetGroup, type LiveEvent,
} from '../src/design/syslogText.ts';

// Typerne, serveren kan give (SysLogKinds i src/Fleetdeck.Shared/SysLog.cs)
const SERVER_KINDS = ['unexpected-shutdown', 'shutdown-6008', 'bugcheck', 'whea-fatal', 'whea-corrected', 'disk-bad-block', 'disk-paging', 'disk-retry',
  'disk-controller', 'ntfs-corrupt', 'storport-reset', 'wu-failed', 'dcom-noise', 'oom', 'io-error', 'kernel-panic', 'fs-error', 'segfault', 'mac-panic'];

test('alle kendte typer har titel, forklaring og hvad man gør', () => {
  for (const k of SERVER_KINDS) {
    const x = known(k);
    assert.ok(x, k);
    assert.ok(x.title.length > 3 && x.explain.length > 20 && x.todo.length > 0, k);
  }
  assert.deepEqual(Object.keys(KNOWN).sort(), [...SERVER_KINDS].sort());
  assert.equal(known(null), null);
  assert.equal(known('ukendt'), null);
  assert.equal(known('dcom-noise')?.noise, true);
});

test('titel: kendt hændelse eller kilde med id', () => {
  assert.equal(groupTitle({ source: 'Microsoft-Windows-Kernel-Power', eventId: 41, kind: 'unexpected-shutdown' }), 'Uventet nedlukning');
  assert.equal(groupTitle({ source: 'Microsoft-Windows-Kernel-Power', eventId: 137 }), 'Kernel-Power 137');
  assert.equal(sourceLabel({ source: 'disk', eventId: 7 }), 'disk 7');
  assert.equal(groupTitle({ source: 'nginx' }), 'nginx');
});

test('tider og antal', () => {
  const now = new Date(2026, 10, 20, 12, 0);
  assert.equal(when(new Date(2026, 10, 20, 8, 15).toISOString(), now), 'i dag 08:15');
  assert.equal(when(new Date(2026, 10, 19, 22, 1).toISOString(), now), 'i går 22:01');
  assert.equal(when(new Date(2026, 10, 12, 10, 2).toISOString(), now), '12.11. 10:02');
  const g = { count: 14, first: new Date(2026, 10, 12, 10, 2).toISOString(), last: new Date(2026, 10, 20, 8, 15).toISOString() };
  assert.equal(rangeText(g, now), 'første 12.11. 10:02 · seneste i dag 08:15');
  assert.equal(rangeText({ ...g, count: 1, first: g.last }, now), 'i dag 08:15');
  assert.equal(countText(1), '1 gang');
  assert.equal(countText(14), '14 gange');
});

const fg = (machineId: number, kind: string | null, count: number, level: FleetGroup['level'] = 'error', ignored: FleetGroup['ignored'] = null): FleetGroup => ({
  machineId, machine: `PC-${machineId}`, platform: 'windows', online: true, key: `${kind}|${machineId}`, level, log: 'System', source: 's', kind, count,
  first: '2026-11-20T08:00:00Z', last: '2026-11-20T09:00:00Z', message: 'm', ignored, recent: [],
});

test('opsummering pr. område tæller hændelser og maskiner, men ikke støj eller skjulte', () => {
  const s = summarize([fg(1, 'bugcheck', 2), fg(2, 'kernel-panic', 1), fg(1, 'disk-bad-block', 5), fg(3, 'dcom-noise', 40), fg(4, 'whea-fatal', 1, 'error', 'global'), fg(5, null, 3, 'critical')]);
  assert.deepEqual(s, [{ area: 'nedbrud', count: 6, machines: 3 }, { area: 'disk', count: 5, machines: 1 }]);
  assert.equal(byArea([fg(1, 'bugcheck', 1), fg(1, 'oom', 1)], 'hukommelse').length, 1);
  assert.equal(byArea([fg(1, 'bugcheck', 1), fg(1, 'oom', 1)], '').length, 2);
});

test('forespørgslen udelader tomme filtre', () => {
  assert.equal(logQuery({ days: '7', level: '', source: null, q: ' ', hidden: false }), 'days=7');
  assert.equal(logQuery({ days: '1', level: 'error', source: 'disk', q: 'sector', hidden: true }), 'days=1&level=error&source=disk&q=sector&hidden=true');
});

const enc = new TextEncoder();
const frame = (type: number, body: Uint8Array) => { const b = new Uint8Array(body.length + 1); b[0] = type; b.set(body, 1); return b; };

test('live-rammer: hændelse, status, slut og affald', () => {
  const ev: LiveEvent = { time: '2026-11-20T09:15:02Z', level: 'error', log: 'kernel', source: 'kernel', message: 'Buffer I/O error', kind: 'io-error' };
  assert.deepEqual(parseLiveFrame(frame(0, enc.encode(JSON.stringify(ev)))), { type: 'event', ev });
  assert.deepEqual(parseLiveFrame(frame(3, enc.encode('Forbinder …'))), { type: 'status', text: 'Forbinder …' });
  const exit = new Uint8Array([2, 0, 0, 0, 1]);
  assert.deepEqual(parseLiveFrame(exit), { type: 'exit', code: 1 });
  assert.equal(parseLiveFrame(frame(0, enc.encode('{ikke json'))), null);
  assert.equal(parseLiveFrame(new Uint8Array()), null);
  assert.deepEqual([...heartbeatFrame()], [0]);
});

test('live: højst LIVE_MAX linjer, filter og tekst til kopi', () => {
  const ev = (i: number): LiveEvent => ({ time: '2026-11-20T09:15:02Z', level: 'warning', log: 'journal', source: 'backup', message: `linje ${i}` });
  const lines = appendLive(Array.from({ length: LIVE_MAX - 1 }, (_, i) => ev(i)), [ev(-1), ev(-2), ev(-3)]);
  assert.equal(lines.length, LIVE_MAX);
  assert.equal(lines[lines.length - 1].message, 'linje -3');
  assert.ok(liveMatches(ev(5), 'LINJE 5'));
  assert.ok(liveMatches({ ...ev(1), eventId: 41 }, '41'));
  assert.ok(!liveMatches(ev(5), 'disk'));
  assert.match(liveText(ev(7)), /^\d\d:\d\d:\d\d Advarsel backup: linje 7$/);
});
