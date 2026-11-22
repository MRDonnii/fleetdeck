// Test af alarmkortenes tekster (web/src/design/alertHelpText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { agoText, fixButton, fixStatus, shortWhy, sinceText, stepParts, stepsOf, whoText, type FixJobStatus } from '../src/design/alertHelpText.ts';

const now = Date.parse('2026-11-21T12:00:00Z');
const min = (n: number) => new Date(now - n * 60_000).toISOString();

test('tid: kort og på dansk', () => {
  assert.equal(sinceText(min(180), null, now), 'Siden 3 t');
  assert.equal(sinceText(min(0), null, now), 'Lige nu');
  assert.equal(sinceText(min(300), min(120), now), 'Løst for 2 t siden');
  assert.equal(agoText(min(2), now), 'for 2 min siden');
  assert.equal(agoText(min(60 * 49), now), 'for 2 d siden');
  assert.equal(agoText(null, now), '');
});

test('hvem: hjælperens automatiske kørsler hedder Fleetdeck', () => {
  assert.equal(whoText('hjælper (automatisk)'), 'Fleetdeck');
  assert.equal(whoText('john'), 'john');
});

test('knappen bruger rettelsens egen tekst', () => {
  assert.deepEqual(fixButton({ id: 'debloat_reapply', title: 'Anvend debloat igen', button: 'Ret det' }), { icon: '✓', label: 'Ret det' });
  assert.deepEqual(fixButton({ id: 'service_start', title: 'Start stoppede tjenester', button: 'Start tjenesten' }), { icon: '▶', label: 'Start tjenesten' });
  assert.deepEqual(fixButton({ id: 'x', title: 'X' }), { icon: '✓', label: 'Ret det' });
  assert.equal(fixButton(null), null);
});

test('trin: tekst i anførselstegn bliver fed', () => {
  assert.deepEqual(stepParts('Tryk "Ret det" – Fleetdeck anvender punkterne igen.'),
    [{ text: 'Tryk ', bold: false }, { text: 'Ret det', bold: true }, { text: ' – Fleetdeck anvender punkterne igen.', bold: false }]);
  assert.deepEqual(stepParts('Intet at gøre.'), [{ text: 'Intet at gøre.', bold: false }]);
  assert.deepEqual(stepsOf({ steps: ['a', ' ', 'b'] }), ['a', 'b']);
  assert.deepEqual(stepsOf({}), []);
});

test('status for rettelsen', () => {
  const job = (status: FixJobStatus, finished: string | null = min(2)) => ({ id: 7, status, finished, createdBy: 'hjælper (automatisk)' });
  assert.equal(fixStatus({ fixJob: null, resolved: null, lastSeen: min(0) }, now), null);
  assert.equal(fixStatus({ fixJob: job('running', null), resolved: null, lastSeen: min(0) }, now)!.tone, 'running');
  assert.equal(fixStatus({ fixJob: job('pending', null), resolved: null, lastSeen: min(0) }, now)!.tone, 'running');
  assert.deepEqual(fixStatus({ fixJob: job('success'), resolved: min(1), lastSeen: min(1) }, now), { tone: 'ok', text: '✓ Rettet af Fleetdeck for 2 min siden' });
  // kørt, men fejlen er set igen længe efter: hjalp ikke
  const still = fixStatus({ fixJob: job('success', min(60)), resolved: null, lastSeen: min(1) }, now)!;
  assert.equal(still.tone, 'failed');
  assert.match(still.text, /fejlen er her stadig/);
  assert.equal(fixStatus({ fixJob: job('success', min(2)), resolved: null, lastSeen: min(1) }, now)!.tone, 'ok');
  const failed = fixStatus({ fixJob: job('failed'), resolved: null, lastSeen: min(0) }, now)!;
  assert.equal(failed.tone, 'failed');
  assert.match(failed.text, /Åbn jobbet/);
  assert.doesNotMatch(fixStatus({ fixJob: job('failed', null), resolved: null, lastSeen: min(0) }, now)!.text, / {2}/);
});

test('forsiden: første sætning af forklaringen', () => {
  assert.equal(shortWhy({ why: 'Noget er kommet tilbage. Pc\'en virker fint.', detail: 'x' }), 'Noget er kommet tilbage.');
  assert.equal(shortWhy({ why: null, detail: '12 GB fri' }), '12 GB fri');
});
