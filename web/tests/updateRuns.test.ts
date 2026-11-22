// Test af opdateringsmotorens visning (web/src/design/updateRunsText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { allGood, autoUpdateOff, MAINT_STATE, rebootRuleText, reportGroups, roundDots } from '../src/design/updateRunsText.ts';

const rep = {
  installed: ['KB5132061', 'Intel Driver'], failed: [{ title: 'HP Firmware', code: '0x80070643' }],
  waiting: [{ title: 'Dock Firmware', reason: 'kræver netstrøm (og mindst 50 % batteri)' }], skipped: [{ title: 'Microsoft Edge', reason: 'opdaterer sig selv' }],
  remaining: ['Realtek Audio'], rebootPending: true,
};

test('det, der fejlede og venter, står før det installerede', () => {
  assert.deepEqual(reportGroups(rep, false).map((g) => g.key), ['failed', 'waiting', 'installed', 'skipped']);
});

test('mens kørslen er i gang, venter resten på næste runde; bagefter mangler det', () => {
  const during = reportGroups(rep, true).find((g) => g.key === 'waiting')!;
  assert.ok(during.rows.some((r) => r.text === 'Realtek Audio' && r.note === 'næste runde'));
  assert.ok(!during.rows.some((r) => r.text === 'Genstart'));
  const after = reportGroups(rep, false).find((g) => g.key === 'waiting')!;
  assert.ok(after.rows.some((r) => r.text === 'Realtek Audio' && r.note === 'mangler stadig'));
  assert.ok(after.rows.some((r) => r.text === 'Genstart'));
});

test('tomme grupper vises ikke', () => {
  assert.deepEqual(reportGroups({ installed: ['7-Zip'], failed: [], waiting: [], skipped: [], remaining: [], rebootPending: false }, false).map((g) => g.key), ['installed']);
});

test('runderne: færdige, aktuel og tilbage', () => {
  assert.deepEqual(roundDots({ round: 2, maxRounds: 4, finished: null }), ['done', 'current', 'todo', 'todo']);
  // slut: kun de runder, der faktisk kørte (ikke grå prikker, der ser ufærdige ud)
  assert.deepEqual(roundDots({ round: 2, maxRounds: 4, finished: '2026-11-20T03:10:00Z' }), ['done', 'done']);
});

test('kun "færdig" er grøn – delvis er orange', () => {
  assert.equal(allGood({ state: 'done' }), true);
  assert.equal(allGood({ state: 'partial' }), false);
  assert.equal(MAINT_STATE.partial.color, 'orange');
  assert.equal(MAINT_STATE.done.color, 'green');
});

test('vedligeholdelse kun med oprydning tæller ikke som automatisk opdatering', () => {
  const base = { upgradeMode: 'off', windowsUpdateMode: 'off' };
  assert.equal(autoUpdateOff({ ...base, maintMode: 'weekly', maintTasks: ['disk_cleanup'] }), true);
  assert.equal(autoUpdateOff({ ...base, maintMode: 'weekly', maintTasks: ['windows_update'] }), false);
  assert.equal(autoUpdateOff({ ...base, maintMode: 'off', maintTasks: ['windows_update'] }), true);
});

test('teksten om genstart passer til profilen', () => {
  assert.match(rebootRuleText(false), /15 minutter/);
  assert.match(rebootRuleText(true), /uden varsel/);
  assert.match(rebootRuleText(true), /5 minutters nedtælling/);
});
