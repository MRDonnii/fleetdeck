// Test af valget mellem live-fremdrift og job-listen (web/src/jobProgress.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { pickProgress } from '../src/jobProgress.ts';

const live = (progress: number | null, step: string, at: number) => ({ progress, step, stepSince: null, at });
const api = (progress: number | null, step: string, at: number) => ({ progress, step, stepSince: null, at });

test('uden live-fremdrift bruges job-listen', () => {
  assert.equal(pickProgress(undefined, api(80, 'Installerer', 10)).progress, 80);
});

test('forbindelsen faldt ud: nyere data fra job-listen slår gammel live-fremdrift (40 % hænger ikke fast)', () => {
  assert.equal(pickProgress(live(40, 'Henter', 100), api(80, 'Installerer', 200)).progress, 80);
  assert.equal(pickProgress(live(40, 'Installerer', 100), api(80, 'Installerer', 200)).progress, 80);
});

test('samme trin: den højeste fremdrift vinder, også når job-listen er hentet efter (svaret var gammelt)', () => {
  assert.equal(pickProgress(live(60, 'Installerer', 100), api(40, 'Installerer', 200)).progress, 60);
  assert.equal(pickProgress(live(80, 'Installerer', 300), api(40, 'Installerer', 200)).progress, 80);
});

test('live-fremdrift, der er nyere end job-listen, bruges', () => {
  assert.equal(pickProgress(live(20, 'Genstarter', 300), api(90, 'Installerer', 200)).step, 'Genstarter');
});
