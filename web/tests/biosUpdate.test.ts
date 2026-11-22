// Test af BIOS-opdateringens tekster (web/src/design/biosUpdateText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { biosChecksSorted, biosCheckTone, biosUpdateConfirmOk, biosUpdateState, biosUpdateWarnings, BIOS_UPDATE_BUTTON, rowUsbOnly } from '../src/design/biosUpdateText.ts';

test('advarsler: strøm og F1 altid, BitLocker kun på Windows', () => {
  const lin = biosUpdateWarnings('capsule');
  const win = biosUpdateWarnings('lenovo-exe');
  assert.ok(lin[0].startsWith('Strømmen må ikke gå'));
  assert.ok(lin.some(w => w.includes('0162')) && win.some(w => w.includes('0162')));
  assert.ok(win.some(w => w.includes('BitLocker')));
  assert.ok(!lin.some(w => w.includes('BitLocker')));
  assert.ok(lin.some(w => w.includes('TPM')));
});

test('bekræftelse kræver præcis versionen', () => {
  assert.equal(biosUpdateConfirmOk(' q9zkt79a ', 'Q9ZKT79A'), true);
  assert.equal(biosUpdateConfirmOk('Q9ZKT78A', 'Q9ZKT79A'), false);
  assert.equal(biosUpdateConfirmOk('', 'Q9ZKT79A'), false);
  assert.ok(BIOS_UPDATE_BUTTON.startsWith('Opdatér BIOS'));
});

test('tilstande og farver', () => {
  const r = (effective: Parameters<typeof biosUpdateState>[0]['effective']) => biosUpdateState({ effective, targetVersion: 'Q9ZKT79A', fromVersion: 'Q9ZKT75A' });
  assert.deepEqual(r('updated'), { text: "BIOS'en er opdateret til Q9ZKT79A.", tone: 'green' });
  assert.equal(r('rejected').tone, 'red');
  assert.equal(r('no_report').tone, 'red');
  assert.ok(r('no_report').text.includes('F1'));
  assert.ok(r('not_applied').text.includes('Q9ZKT75A'));
  assert.equal(r('staged').tone, 'blue');
});

test('forudsætninger: stop først', () => {
  const s = biosChecksSorted([{ id: 'a', state: 'ok', text: '' }, { id: 'b', state: 'fail', text: '' }, { id: 'c', state: 'warn', text: '' }]);
  assert.deepEqual(s.map(c => c.id), ['b', 'c', 'a']);
  assert.equal(biosCheckTone({ id: 'x', state: 'warn', text: '' }), 'yellow');
});

test('BIOS-rækken: "Lav USB-stik…" først, når vejen er hentet og ikke findes', () => {
  assert.equal(rowUsbOnly(undefined, true), false);              // henter stadig (eller kaldet fejlede): ingen knap
  assert.equal(rowUsbOnly({ route: 'lenovo-exe' }, true), false); // vej fra Windows: "Opdatér BIOS…"
  assert.equal(rowUsbOnly({ route: null }, true), true);          // ingen vej fra styresystemet: USB-guiden
  assert.equal(rowUsbOnly({ route: null }, false), false);        // USB ikke tilladt
});
