// Test af diskhelbred, batteri, tidslinje og ændringslog på Helbred-fanen (web/src/design/healthHistoryText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  batteryCapacity, batteryHealth, batteryNow, byDay, diskKind, diskLife, diskNotes, diskSize, diskState, diskTemp, markerColor, pos, powerOn, span, ticks,
  uptimeTone, versionText, type ChangeRow,
} from '../src/design/healthHistoryText.ts';

test('disk: vurdering, type og størrelse – også for data fra ældre agenter', () => {
  assert.deepEqual(diskState({ name: 'A', state: 'failing' }), { text: 'Fejl', tone: 'red' });
  assert.equal(diskState({ name: 'A', health: 'Healthy' }).tone, 'green');
  assert.equal(diskState({ name: 'A', health: 'Warning' }).tone, 'yellow');
  assert.equal(diskState({ name: 'A' }).text, 'Ukendt');
  assert.equal(diskKind({ name: 'A', kind: 'NVMe' }), 'NVMe');
  assert.equal(diskKind({ name: 'A', media: 'SSD', bus: 'NVMe' }), 'NVMe');
  assert.equal(diskKind({ name: 'A', media: '?' }), 'Disk');
  assert.equal(diskSize(500), '500 GB');
  assert.equal(diskSize(4000), '4 TB');
  assert.equal(diskSize(1500), '1,5 TB');
  assert.equal(diskSize(0), '');
});

test('disk: levetid, temperatur og driftstid', () => {
  assert.deepEqual(diskLife({ name: 'A', lifeLeft: 93 }), { pct: 93, text: '93 % tilbage', tone: 'green' });
  assert.equal(diskLife({ name: 'A', wear: 85, kind: 'SSD' })!.tone, 'orange');
  assert.equal(diskLife({ name: 'A', wear: 95, kind: 'NVMe' })!.tone, 'red');
  assert.equal(diskLife({ name: 'A', wear: 10, kind: 'HDD' }), null);
  assert.equal(diskTemp({ name: 'A', temp: 72 }, 70)!.tone, 'red');
  assert.deepEqual(diskTemp({ name: 'A', temp: 34, tempMax: 50 }), { text: '34 °C', tone: 'green', max: 'maks. 50 °C' });
  assert.equal(diskTemp({ name: 'A' }), null);
  assert.equal(powerOn(21034), '21.034 t (2,4 år)');
  assert.equal(powerOn(30), '30 t');
  assert.equal(powerOn(null), '–');
});

test('disk: det man skal lægge mærke til', () => {
  const now = new Date('2026-11-20T12:00:00Z');
  const notes = diskNotes({ name: 'A', health: 'Healthy', pending: 2, reallocated: 12, reallocatedFirst: 4, reallocatedRoseAt: '2026-11-19T12:00:00Z', mediaErrors: 1, mediaErrorsRoseAt: '2026-10-01T00:00:00Z', criticalWarning: 1 }, now);
  assert.deepEqual(notes.map((n) => n.text), ['NVMe-advarsel: få reserveblokke tilbage', '2 ulæselige sektorer venter', '12 reallokerede sektorer (var 4) – stiger', '1 mediefejl']);
  assert.deepEqual(notes.map((n) => n.tone), ['yellow', 'yellow', 'yellow', 'gray']);
  assert.equal(diskNotes({ name: 'A', criticalWarning: 8 })[0].tone, 'red');
  assert.deepEqual(diskNotes({ name: 'A', health: 'Healthy', reallocated: 0 }), []);
  assert.equal(diskNotes({ name: 'A', standby: true })[0].text, 'Disken sov – tallene er fra sidste gang');
});

test('batteri: helbred, kapacitet og status', () => {
  assert.deepEqual(batteryHealth({ name: 'B', healthPct: 54 }), { text: '54 %', tone: 'red' });
  assert.equal(batteryHealth({ name: 'B', healthPct: 70 }).tone, 'yellow');
  assert.equal(batteryHealth({ name: 'B', healthPct: 88 }).tone, 'green');
  assert.equal(batteryHealth({ name: 'B', healthPct: 88, condition: 'Service Recommended' }).tone, 'red');
  assert.equal(batteryHealth({ name: 'B' }).text, 'Ukendt');
  assert.equal(batteryCapacity({ name: 'B', design: 57000, full: 45030, unit: 'mWh' }), '45.030 af 57.000 mWh');
  assert.equal(batteryCapacity({ name: 'B' }), '–');
  assert.equal(batteryNow({ name: 'B', chargePct: 77, status: 'discharging' }), '77 % · På batteri');
  assert.equal(batteryNow({ name: 'B' }), '–');
});

test('tidslinje: placering, varighed, oppetid og farver', () => {
  const from = '2026-11-19T12:00:00Z'; const to = '2026-11-20T12:00:00Z';
  assert.equal(pos('2026-11-20T00:00:00Z', from, to), 50);
  assert.equal(pos('2026-11-18T00:00:00Z', from, to), 0);
  assert.equal(pos('2026-11-21T00:00:00Z', from, to), 100);
  assert.equal(span('2026-11-20T00:00:00Z', '2026-11-20T03:20:00Z'), '3 t 20 min');
  assert.equal(span('2026-11-18T00:00:00Z', '2026-11-20T04:00:00Z'), '2 d 4 t');
  assert.equal(span(from, from), '0 min');
  assert.equal(uptimeTone(99.5), 'green');
  assert.equal(uptimeTone(95), 'yellow');
  assert.equal(uptimeTone(null), 'gray');
  assert.equal(markerColor({ kind: 'event', severity: 'critical' }), 'var(--mantine-color-red-6)');
  assert.equal(markerColor({ kind: 'boot', severity: 'info' }), 'var(--mantine-color-teal-5)');
});

test('tidslinje: aksens mærker ligger inden for båndet', () => {
  const t1 = ticks('2026-11-19T12:30:00Z', '2026-11-20T12:30:00Z', 1);
  assert.ok(t1.length >= 5 && t1.length <= 7);
  assert.ok(t1.every((t) => t.pct > 0 && t.pct < 100 && /^\d\d:00$/.test(t.label)));
  assert.equal(ticks('2026-11-13T12:00:00Z', '2026-11-20T12:00:00Z', 7).length, 7);
  const t30 = ticks('2026-10-21T12:00:00Z', '2026-11-20T12:00:00Z', 30);
  assert.ok(t30.length === 6);
});

test('ændringslog: versioner og dage', () => {
  assert.equal(versionText({ action: 'updated', from: '120.0', to: '121.0' }), '120.0 → 121.0');
  assert.equal(versionText({ action: 'installed', to: '3.0.20' }), '3.0.20');
  assert.equal(versionText({ action: 'removed', from: '1.0' }), '1.0');
  const now = new Date('2026-11-20T15:00:00');
  const rows: ChangeRow[] = [
    { id: 1, time: new Date('2026-11-18T09:00:00').toISOString(), kind: 'program', action: 'installed', name: 'VLC' },
    { id: 2, time: new Date('2026-11-20T10:00:00').toISOString(), kind: 'driver', action: 'updated', name: 'Example GPU' },
    { id: 3, time: new Date('2026-11-19T10:00:00').toISOString(), kind: 'program', action: 'removed', name: 'Old Tool' },
    { id: 4, time: new Date('2026-11-20T08:00:00').toISOString(), kind: 'program', action: 'updated', name: 'Firefox' },
  ];
  const g = byDay(rows, now);
  assert.deepEqual(g.map((x) => x.label.split(' ')[0]), ['I', 'I', 'onsdag']);
  assert.deepEqual(g.map((x) => x.rows.map((r) => r.id)), [[2, 4], [3], [1]]);
  assert.equal(g[0].label, 'I dag');
  assert.equal(g[1].label, 'I går');
});

test('tidslinje: smal skærm giver færre og kortere mærker', () => {
  const t7 = ticks('2026-11-13T12:00:00Z', '2026-11-20T12:00:00Z', 7, true);
  assert.equal(t7.length, 7);
  assert.ok(t7.every((t) => /^\d{1,2}\/\d{1,2}$/.test(t.label)));
  assert.equal(ticks('2026-10-21T12:00:00Z', '2026-11-20T12:00:00Z', 30, true).length, 3);
  assert.ok(ticks('2026-11-19T12:30:00Z', '2026-11-20T12:30:00Z', 1, true).length <= 4);
});
