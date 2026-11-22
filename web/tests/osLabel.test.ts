// Test af styresystem-mærket og -teksten ud for maskinerne (web/src/design/osLabel.ts). Kør: npm test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { macName, osBadge, osDetail } from '../src/design/osLabel.ts';

test('alle tre platforme får et mærke – også Windows', () => {
  assert.equal(osBadge({ platform: 'windows' })?.label, 'Windows');
  assert.equal(osBadge({ platform: 'mac' })?.label, 'macOS');
  assert.equal(osBadge({ platform: 'linux' })?.label, 'Linux');
  assert.equal(osBadge({ platform: 'ios' }), null);
});

test('Windows: udgave og funktionsopdatering, uden "Microsoft"', () => {
  assert.equal(osDetail({ platform: 'windows', os: 'Microsoft Windows 11 Pro', osVersion: '26H2' }), 'Windows 11 Pro 26H2');
  assert.equal(osDetail({ platform: 'windows', os: 'Microsoft Windows 10 Home', osVersion: '22h2' }), 'Windows 10 Home 22H2');
  assert.equal(osDetail({ platform: 'windows', os: 'Microsoft Windows 11 Pro', osVersion: '10.0.26100' }), 'Windows 11 Pro');
  assert.equal(osDetail({ platform: 'windows', os: null }), null);
});

test('macOS: version og navn', () => {
  assert.equal(osDetail({ platform: 'mac', os: 'macOS 13.7.8', osVersion: '13.7.8' }), 'macOS 13.7.8 Ventura');
  assert.equal(osDetail({ platform: 'mac', os: 'macOS 15.3', osVersion: '15.3' }), 'macOS 15.3 Sequoia');
  assert.equal(osDetail({ platform: 'mac', os: 'macOS 10.15.7', osVersion: '10.15.7' }), 'macOS 10.15.7 Catalina');
  assert.equal(osDetail({ platform: 'mac', os: 'macOS 27.2.1', osVersion: '27.2.1' }), 'macOS 27.2.1', 'ukendt navn: kun versionen');
  assert.equal(osDetail({ platform: 'mac', os: null, osVersion: '14.6' }), 'macOS 14.6 Sonoma');
  assert.equal(macName('12.7'), 'Monterey');
  assert.equal(macName(''), null);
});

test('Linux: distroens eget navn, Raspberry Pi OS i stedet for Raspbian', () => {
  assert.equal(osDetail({ platform: 'linux', os: 'Ubuntu 26.04.1 LTS', osVersion: '26.04' }), 'Ubuntu 26.04.1 LTS');
  assert.equal(osDetail({ platform: 'linux', os: 'Raspbian GNU/Linux 12 (bookworm)', osVersion: '12' }), 'Raspberry Pi OS 12 (bookworm)');
  assert.equal(osDetail({ platform: 'linux', os: 'Debian GNU/Linux 12 (bookworm)' }), 'Debian 12 (bookworm)');
  assert.equal(osDetail({ platform: 'linux', os: 'Unraid 7.4.0', osVersion: '7.4.0' }), 'Unraid 7.4.0');
});

test('maskinelisten viser mærket for alle platforme og teksten, både på mobil og i tabellen', () => {
  const src = readFileSync(new URL('../src/components.tsx', import.meta.url), 'utf8');
  assert.ok(!src.includes(">Linux</Badge>") && !src.includes(">macOS</Badge>"), 'de gamle faste mærker er væk');
  assert.ok((src.match(/<OsBadge /g) ?? []).length >= 2);
  assert.ok((src.match(/osDetail\(m\)/g) ?? []).length >= 2);
});
