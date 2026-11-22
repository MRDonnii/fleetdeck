// Test af TPM-afsnittet på maskinens side (web/src/design/tpmText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { tpmBitLocker, tpmManufacturer, tpmPresent, tpmReady, type TpmEncryption, type TpmInfo } from '../src/design/tpmText.ts';

const good: TpmInfo = { present: true, version: '2.0', manufacturerId: 'INTC', manufacturer: 'Intel', kind: 'firmware', enabled: true, activated: true, ready: true };
const bl = (usesTpm: boolean | null, encrypted = true): TpmEncryption => ({ kind: 'bitlocker', volumes: [{ mount: 'C:', system: true, encrypted, usesTpm }] });

test('til stede: Windows uden TPM er rød, Mac er ikke relevant', () => {
  assert.deepEqual(tpmPresent(good, 'windows'), { text: 'Ja', tone: 'green' });
  assert.equal(tpmPresent({ present: false }, 'windows').tone, 'red');
  assert.equal(tpmPresent({ present: false }, 'linux').tone, 'gray');
  assert.equal(tpmPresent({ present: false, apple: 'Apple Silicon (Secure Enclave)' }, 'mac').text, 'Ikke relevant – Apple Silicon (Secure Enclave)');
  assert.equal(tpmPresent({ present: false }, 'mac').text, 'Ingen TPM');
  assert.equal(tpmPresent({ present: false, error: 'x' }, 'windows').text, 'Kunne ikke læses');
});

test('producent med id', () => {
  assert.equal(tpmManufacturer(good), 'Intel (INTC)');
  assert.equal(tpmManufacturer({ present: true, manufacturerId: 'XYZ', manufacturer: 'XYZ' }), 'XYZ');
  assert.equal(tpmManufacturer({ present: true }), null);
});

test('klar, slået fra og ukendt', () => {
  assert.equal(tpmReady(good)?.text, 'Klar');
  assert.equal(tpmReady({ ...good, enabled: false })?.text, 'Slået fra');
  assert.equal(tpmReady({ ...good, ready: false })?.text, 'Ikke klar');
  assert.equal(tpmReady({ ...good, attention: ['Kræver genstart'] })?.tone, 'yellow');
  assert.equal(tpmReady({ present: true, version: '2.0' }), null);
});

test('bruges af BitLocker', () => {
  assert.deepEqual(tpmBitLocker(bl(true), 'windows'), { text: 'Ja (C:)', tone: 'green' });
  assert.equal(tpmBitLocker(bl(false), 'windows')?.tone, 'yellow');
  assert.equal(tpmBitLocker(bl(null, false), 'windows')?.text, 'Nej – BitLocker er ikke slået til');
  assert.equal(tpmBitLocker(bl(null), 'windows')?.text, 'Ukendt');
  assert.equal(tpmBitLocker(bl(true), 'linux'), null);
  assert.equal(tpmBitLocker({ kind: 'luks', volumes: [] }, 'windows'), null);
  assert.equal(tpmBitLocker(null, 'windows'), null);
});

