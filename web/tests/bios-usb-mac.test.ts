// USB-stik-guiden: hvilke pc'er kan lave stikket (web/src/design/biosText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { usbWriterHere } from '../src/design/biosText.ts';

test('USB-stik kan laves fra Linux og Mac', () => {
  assert.ok(usbWriterHere('linux'));
  assert.ok(usbWriterHere('mac'));
  assert.ok(!usbWriterHere(null));
  assert.ok(!usbWriterHere(''));
});

test('guiden sender medienavnet med til den lokale bekræftelse og spærrer ikke Mac', () => {
  const src = readFileSync(new URL('../src/design/bios.tsx', import.meta.url), 'utf8');
  assert.match(src, /local<\{ hash: string \}>\('confirm', \{[^}]*model: chosen\.model/);
  assert.doesNotMatch(src, /platform !== 'linux'/);
});
