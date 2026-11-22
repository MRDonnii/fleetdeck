// Test af den rene ESP-logik i web/src/esp (kør: npm test). Node fjerner selv typerne.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ImprovParser, packet, rpc, strings, wifiRpc } from '../src/esp/improv.ts';
import { catalogNotice, filters, generatedLabel, platformOf, releases, tested, type EspFirmware } from '../src/esp/catalog.ts';

test('Improv-pakke har header, længde, kontrolsum og linjeskift', () => {
  const p = rpc(0x02);
  assert.deepEqual([...p.slice(0, 7)], [0x49, 0x4d, 0x50, 0x52, 0x4f, 0x56, 0x01]);
  assert.equal(p[7], 0x03);                // rpc
  assert.equal(p[8], 2);                   // [kommando, længde]
  assert.equal(p[p.length - 1], 0x0a);
  const sum = [...p.slice(0, p.length - 2)].reduce((a, b) => (a + b) & 0xff, 0);
  assert.equal(p[p.length - 2], sum);
});

test('Wi-Fi-kommandoen koder navn og kode i UTF-8 og afviser for lange', () => {
  const p = wifiRpc('Hjem', 'kodeæ');
  // data = [kommando 1, længde, 4, H j e m, 6, k o d e æ(2 byte)]
  assert.deepEqual([...p.slice(9, 11)], [0x01, 1 + 4 + 1 + 6]);
  assert.equal(p[11], 4);
  assert.equal(p[16], 6);
  assert.throws(() => wifiRpc('x'.repeat(33), ''));
  assert.throws(() => wifiRpc('net', 'x'.repeat(65)));
});

test('Parseren finder pakker midt i loggen og over flere bidder', () => {
  const state = packet(0x01, [0x02]);
  const result = packet(0x04, [0x01, 0x0f, 14, ...new TextEncoder().encode('http://192.0.2')]);
  const log = new TextEncoder().encode('[I][boot] starter\n');
  const all = [...log, ...state, ...log, ...result];
  const p = new ImprovParser();
  const a = p.push(all.slice(0, 20));
  const b = p.push(all.slice(20));
  const got = [...a, ...b];
  assert.equal(got.length, 2);
  assert.equal(got[0].type, 0x01);
  assert.deepEqual(got[0].data, [0x02]);
  assert.deepEqual(strings(got[1].data.slice(2)), ['http://192.0.2']);
});

test('Parseren springer en pakke med forkert kontrolsum over', () => {
  const bad = packet(0x01, [0x02]);
  bad[bad.length - 2] ^= 0xff;
  assert.equal(new ImprovParser().push(bad).length, 0);
});

const fw = (id: string, chip: string, kind: string, status = 'testet'): EspFirmware =>
  ({ id, name: id, chip, kind, status, version: '1.0', manifest: `${id}/manifest.json`, sha256: '0123456789ab' });

test('Filtre tæller pr. chip og type og nulstiller et filter, der ikke findes', () => {
  const list = [fw('a', 'ESP32-H2', 'zigbee'), fw('b', 'ESP32-H2', 'zigbee', 'Ikke testet'), fw('c', 'ESP32-C3', 'wifi')];
  const all = filters(list, 'any', 'any');
  assert.deepEqual(all.chips.map((c) => `${c.label}:${c.count}`), ['Alle:3', 'ESP32-H2:2', 'ESP32-C3:1']);
  assert.equal(all.items.length, 3);
  const h2 = filters(list, 'ESP32-H2', 'wifi');   // Wi-Fi findes ikke på H2 → alle typer
  assert.equal(h2.kind, 'any');
  assert.equal(h2.items.length, 2);
  assert.equal(filters(list, 'ESP32-S3', 'any').chip, 'any');
  assert.equal(tested(list[0]), true);
  assert.equal(tested(list[1]), false);
});

test('Udgivelser: nyt og gammelt katalogformat', () => {
  assert.deepEqual(releases({ ...fw('a', 'x', 'wifi'), changelog: ['rettelse'] }), [{ version: '1.0', items: ['rettelse'] }]);
  assert.equal(releases({ ...fw('a', 'x', 'wifi'), changelog: [{ version: '1.1', items: [] }] })[0].version, '1.1');
  assert.deepEqual(releases(fw('a', 'x', 'wifi')), []);
});

test('Dato og platform', () => {
  assert.equal(generatedLabel('2026-10-30T16:54:42'), '30. okt 2026 16.54');
  assert.equal(generatedLabel('2026-05-01'), '1. maj 2026');
  assert.equal(generatedLabel(''), '');
  assert.equal(generatedLabel('2026-13-01'), '');
  assert.equal(platformOf('Mozilla/5.0 (Windows NT 10.0; Win64; x64)'), 'windows');
  assert.equal(platformOf('Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)'), 'mac');
  assert.equal(platformOf('Mozilla/5.0 (X11; Linux x86_64)'), 'linux');
});

test('ESP-siden: usikker side uden agent giver de to valg i stedet for en død USB-knap', async () => {
  const { espEnv, probeAgent, agentProbeUrl } = await import('../src/esp/local.ts');
  const base = { canUsb: false, mobile: false, secure: false };
  assert.equal(espEnv({ ...base, canUsb: true, secure: true, agent: 'no' }), 'usb');
  assert.equal(espEnv({ ...base, mobile: true, agent: 'no' }), 'mobile');
  assert.equal(espEnv({ ...base, secure: true, agent: 'no' }), 'browser');
  assert.equal(espEnv({ ...base, agent: 'checking' }), 'local');
  assert.equal(espEnv({ ...base, agent: 'yes' }), 'local');
  assert.equal(espEnv({ ...base, agent: 'no' }), 'choose');
  assert.equal(agentProbeUrl(8587), 'http://localhost:8587/_fd/ping');
  // ethvert svar (også 403) = agenten kører; afvist forbindelse = ingen agent
  const ok = (async () => new Response(null, { status: 403 })) as unknown as typeof fetch;
  const refused = (async () => { throw new TypeError('Failed to fetch'); }) as unknown as typeof fetch;
  assert.equal(await probeAgent(8587, 100, ok), 'yes');
  assert.equal(await probeAgent(8587, 100, refused), 'no');
  const hang = ((_u: string, o: RequestInit) => new Promise((_, rej) => o.signal!.addEventListener('abort', () => rej(new Error('abort'))))) as unknown as typeof fetch;
  assert.equal(await probeAgent(8587, 20, hang), 'no');
});

test('Uden katalog forklarer siden, hvordan man sætter et op (Fleetdeck har ingen firmware med)', () => {
  assert.equal(catalogNotice(false, 0), 'setup');
  assert.equal(catalogNotice(false, 3), 'stale');   // hentet tidligere, opdateres ikke
  assert.equal(catalogNotice(true, 0), 'empty');
  assert.equal(catalogNotice(true, 2), null);
});
