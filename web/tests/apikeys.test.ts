// Test af Adgang for programmer (web/src/design/apikeysText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  dateText, endpointsFor, expiresText, headerExample, keySummary, lastUsedText, ownEndpoints, scopeInfo, unused90, usageTone, type ApiEndpoint,
} from '../src/design/apikeysText.ts';

const now = new Date(2026, 10, 20, 12, 0, 0);
const ago = (ms: number) => new Date(now.getTime() - ms).toISOString();
const DAY = 86_400_000;

test('sidst brugt: sekunder, minutter, i dag, i går og dage', () => {
  assert.equal(lastUsedText(null, now), 'Aldrig brugt endnu');
  assert.equal(lastUsedText(ago(12_000), now), 'for 12 sek.');
  assert.equal(lastUsedText(ago(60_000), now), 'for 1 min.');
  assert.equal(lastUsedText(new Date(2026, 10, 20, 8, 14).toISOString(), now), 'i dag 08:14');
  assert.equal(lastUsedText(new Date(2026, 10, 19, 23, 10).toISOString(), now), 'i går 23:10');
  assert.equal(lastUsedText(ago(94 * DAY), now), 'for 94 dage siden');
});

test('ubrugt i 90 dage: regnes fra sidst brugt, ellers fra oprettet', () => {
  const created = ago(200 * DAY);
  assert.equal(unused90({ created, lastUsed: ago(94 * DAY) }, now), true);
  assert.equal(unused90({ created, lastUsed: ago(89 * DAY) }, now), false);
  assert.equal(unused90({ created, lastUsed: null }, now), true);
  assert.equal(unused90({ created: ago(DAY), lastUsed: null }, now), false);
  assert.equal(usageTone({ created, lastUsed: ago(60_000) }, now), 'green');
  assert.equal(usageTone({ created, lastUsed: ago(5 * DAY) }, now), 'gray');
  assert.equal(usageTone({ created, lastUsed: ago(94 * DAY) }, now), 'orange');
});

test('rettigheder: navne, farver og hvilke endpoints de giver', () => {
  assert.equal(scopeInfo('read').label, 'Kun læse');
  assert.equal(scopeInfo('control').label, 'Læse og styre');
  assert.equal(scopeInfo('admin').color, 'orange');
  assert.equal(scopeInfo('ukendt').id, 'read');
  const eps: ApiEndpoint[] = [
    { method: 'GET', path: '/api/kontrolrum/status', scope: 'read', description: '' },
    { method: 'POST', path: '/api/x', scope: 'control', description: '' },
  ];
  assert.deepEqual(endpointsFor('read', eps).map((e) => e.path), ['/api/kontrolrum/status']);
  assert.deepEqual(endpointsFor('admin', eps).map((e) => e.path), ['/api/kontrolrum/status', '/api/x']);
  assert.deepEqual(ownEndpoints('admin', eps), []);
});

test('nøglen vises kun forkortet i eksemplet og opsummeres over nøglen', () => {
  const key = 'fd_' + 'A'.repeat(39) + 'Hc3T';
  assert.equal(headerExample(key), 'Authorization: Bearer fd_AAAA…Hc3T');
  assert.ok(!headerExample(key).includes(key));
  assert.equal(keySummary('read', true, 0), 'Kun læse · kun fra hjemmenettet · udløber aldrig');
  assert.equal(keySummary('control', false, 30), 'Læse og styre · også udefra · udløber om 30 dage');
});

test('datoer og udløb', () => {
  assert.equal(dateText(new Date(2025, 5, 3, 10).toISOString(), now), '3. jun. 2025');
  assert.equal(dateText(new Date(2026, 10, 20, 9, 41).toISOString(), now), 'i dag 09:41');
  assert.equal(dateText(null, now), '–');
  assert.equal(expiresText(null, now), null);
  assert.equal(expiresText(new Date(now.getTime() + 30 * DAY).toISOString(), now), 'udløber om 30 dage');
  assert.equal(expiresText(ago(1000), now), 'udløbet');
});
