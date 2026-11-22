// Test af "Sprunget over i Opdatér alt" (web/src/design/upgradeSkippedLogic.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { canHide, groupSkipped, hiddenInFleetdeck, hideAllLabel, hideText, skippedButton, skippedHideable, type SkippedItem } from '../src/design/upgradeSkippedLogic.ts';

const it = (wingetId: string, group: SkippedItem['group'], action: SkippedItem['action'] = 'upgrade'): SkippedItem => ({ name: wingetId, wingetId, group, reason: '', action });

test('grupperne kommer i fast rækkefølge, og tomme udelades', () => {
  const g = groupSkipped([it('a', 'ignored', null), it('b', 'user'), it('wu:1', 'silent'), it('c', 'user')]);
  assert.deepEqual(g.map((x) => x.group), ['silent', 'user', 'ignored']);
  assert.equal(g[1].items.length, 2);
  assert.equal(g[0].label, 'Kan ikke opdateres stille');
});

test('knappen passer til grunden', () => {
  assert.equal(skippedButton(it('gpu:amd', 'silent', 'amd')), 'Installér driver');
  assert.equal(skippedButton(it('wu:1', 'silent')), 'Installér');
  assert.equal(skippedButton(it('fwupd:x', 'silent')), 'Opdatér firmware');
  assert.equal(skippedButton(it('App.Id', 'user')), 'Prøv igen');
  assert.equal(skippedButton(it('App.Id', 'fail')), 'Prøv igen');
  assert.equal(skippedButton(it('App.Id', 'ignored', null)), null);
});

test('på Windows kan kun Windows Update skjules; på andre maskiner skjuler Fleetdeck alt', () => {
  assert.equal(canHide('wu:abc'), true);
  assert.equal(canHide('wu:abc', 'windows'), true);
  assert.equal(canHide('gpu:amd', 'windows'), false);
  assert.equal(canHide('Mozilla.Firefox', 'windows'), false);
  assert.equal(canHide('fwupd:b7a1d3d9', 'linux'), true);
  assert.equal(canHide('firefox', 'linux'), true);
  assert.equal(canHide('macos:Sonoma 14.7', 'mac'), true);
  assert.equal(hideAllLabel(1), 'Skjul den, der anbefales ikke');
  assert.equal(hideAllLabel(3), 'Skjul de 3, der anbefales ikke');
});

test('kan ikke installeres: egen gruppe efter Fejlede, ingen knap, men kan skjules på Linux', () => {
  const kek: SkippedItem = { name: 'KEK CA', wingetId: 'fwupd:b7a1d3d9', group: 'blocked', reason: 'Firmwaren afviser opdateringen (Secure Boot-nøgle).', action: null };
  const g = groupSkipped([it('a', 'other'), kek, it('b', 'fail')]);
  assert.deepEqual(g.map((x) => x.group), ['fail', 'blocked', 'other']);
  assert.equal(g[1].label, 'Kan ikke installeres');
  assert.equal(skippedButton(kek), null);
  assert.equal(skippedHideable(kek, 'linux'), true);
  assert.equal(skippedHideable(it('firefox', 'other'), 'linux'), false);   // hverken "anbefales ikke" eller blokeret
});

test('skjul-teksten siger, hvor den skjules', () => {
  assert.equal(hiddenInFleetdeck('fwupd:x'), true);
  assert.equal(hiddenInFleetdeck('wu:x'), false);
  assert.match(hideText(['fwupd:x']), /skjules i Fleetdeck/);
  assert.match(hideText(['fwupd:x']), /ny version/);
  assert.match(hideText(['wu:1', 'wu:2']), /^Opdateringerne skjules i Windows Update/);
});
