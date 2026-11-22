// Test af debloat-teksterne i web/src/design/debloatText.ts (kør: npm test).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { stateLabel, stateText } from '../src/design/debloatText.ts';

const ext = { subject: 'Filtypenavne', stateOn: 'vises', stateOff: 'skjult' };
const ad = { subject: 'Reklame-id', stateOn: 'fra', stateOff: 'til' };

test('tilstanden står med punktets egne ord, ikke "slået fra" for alt', () => {
  assert.equal(stateText(ext, 'applied'), 'Filtypenavne: vises');
  assert.equal(stateText(ext, 'not_applied'), 'Filtypenavne: skjult');
  assert.equal(stateText(ad, 'applied'), 'Reklame-id: fra');
});

test('afvigelse siger, hvad pc\'en er vendt tilbage til', () => {
  assert.equal(stateText(ext, 'drift'), 'Filtypenavne: skjult igen');
  assert.equal(stateText({ subject: 'Clipchamp', stateOn: 'fjernet', stateOff: 'installeret' }, 'drift'), 'Clipchamp: installeret igen');
});

test('faste tilstande', () => {
  assert.equal(stateLabel(ad, 'na'), 'gælder ikke denne pc');
  assert.equal(stateLabel(ad, 'partial'), 'delvist');
  assert.equal(stateLabel(ad, 'error'), 'fejl');
});
