// Test af Netværk-afsnittet på maskinens side (web/src/design/networkText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  briefText, bytesText, detailRows, errorsText, hiddenText, netStatus, rateText, signalBars, signalTone, sinceText, speedLine, speedText, speedWarn,
  summaryText, visibleAdapters, type NetAdapter, type NetworkInfo,
} from '../src/design/networkText.ts';

const eth = (o: Partial<NetAdapter> = {}): NetAdapter => ({
  name: 'Ethernet', description: 'Intel(R) Ethernet Connection I219-V', kind: 'ethernet', virtual: false, status: 'connected', primary: true,
  speedMbps: 1000, maxSpeedMbps: 1000, fullDuplex: true, mac: '00-15-5D-AA-BB-01', mtu: 1500, ipv4: ['192.0.2.10'], ipv6: [], ipv6Enabled: true,
  gateways: ['192.0.2.1'], dns: ['192.0.2.1'], dhcp: true, ...o,
});
const wifi = (pct: number, o: Partial<NetAdapter> = {}): NetAdapter => ({
  name: 'Wi-Fi', kind: 'wifi', virtual: false, status: 'connected', primary: false, ipv4: ['192.0.2.20'], ipv6: [], gateways: [], dns: [],
  wifi: { ssid: 'TestNet', signalPct: pct, dbm: -64, band: '5 GHz', channel: 36, frequencyMhz: 5180, rxMbps: 866.7, txMbps: 780, radio: '802.11ac', security: 'WPA2-Personal' }, ...o,
});

test('hastighed: Mbit/s og Gbit/s med dansk komma', () => {
  assert.equal(speedText(100), '100 Mbit/s');
  assert.equal(speedText(1000), '1 Gbit/s');
  assert.equal(speedText(2500), '2,5 Gbit/s');
  assert.equal(speedText(null), null);
  assert.equal(speedText(0), null);
});

test('advarselsfarve: kablet 10/100 Mbit/s eller half duplex', () => {
  assert.equal(speedWarn(eth()), false);
  assert.equal(speedWarn(eth({ speedMbps: 100 })), true);
  assert.equal(speedWarn(eth({ speedMbps: 10 })), true);
  assert.equal(speedWarn(eth({ fullDuplex: false })), true);
  assert.equal(speedWarn(eth({ speedMbps: 100, status: 'unplugged' })), false);
  assert.equal(speedWarn(wifi(30)), false);
  assert.equal(speedLine(eth({ speedMbps: 100, fullDuplex: false })), '100 Mbit/s (half duplex)');
  assert.equal(speedLine(wifi(70)), '867 Mbit/s');   // Wi-Fi uden adapterhastighed: link-hastigheden
});

test('status: farver, og den primære uden forbindelse er rød', () => {
  assert.deepEqual(netStatus(eth()), { text: 'Tilsluttet', tone: 'green' });
  assert.deepEqual(netStatus(eth({ status: 'unplugged', primary: false })), { text: 'Kabel ude', tone: 'gray' });
  assert.equal(netStatus(eth({ status: 'unplugged' })).tone, 'red');
  assert.equal(netStatus(eth({ status: 'limited' })).text, 'Ingen IP-adresse');
  assert.equal(netStatus(eth({ status: 'disabled', primary: false })).text, 'Slået fra');
  assert.equal(netStatus(wifi(50, { status: 'disconnected' })).text, 'Ingen forbindelse');
});

test('Wi-Fi-signal: farve og streger', () => {
  assert.equal(signalTone(72), 'green');
  assert.equal(signalTone(45), 'yellow');
  assert.equal(signalTone(35), 'red');
  assert.equal(signalTone(null), 'gray');
  assert.deepEqual([90, 60, 35, 5, 0, null].map(signalBars), [4, 3, 2, 1, 0, 0]);
});

test('trafik: bytes og bit/s', () => {
  assert.equal(bytesText(512), '512 B');
  assert.equal(bytesText(1_500_000), '1,5 MB');
  assert.equal(bytesText(2_345_000_000), '2,3 GB');
  assert.equal(rateText(0), '0 bit/s');
  assert.equal(rateText(1550), '12,4 kbit/s');
  assert.equal(rateText(1_550_000), '12,4 Mbit/s');
  assert.equal(rateText(null), null);
});

test('maskinlisten og overskriften', () => {
  assert.equal(briefText({ kind: 'ethernet', speedMbps: 1000, warn: false }), 'Kablet 1 Gbit/s');
  assert.equal(briefText({ kind: 'wifi', signalPct: 72, warn: false }), 'Wi-Fi 72 %');
  assert.equal(briefText({ kind: 'mobile', warn: false }), 'Mobil');
  assert.equal(briefText(null), null);
  const net: NetworkInfo = { adapters: [eth(), wifi(72)], virtualCount: 0 };
  assert.equal(summaryText(net), 'Kablet 1 Gbit/s · 192.0.2.10');
  assert.equal(summaryText({ adapters: [wifi(72, { primary: true })], virtualCount: 0 }), 'Wi-Fi 72 % · 192.0.2.20');
});

test('virtuelle skjules, men tælles', () => {
  const v: NetAdapter = { ...eth({ name: 'vEthernet (WSL)', virtual: true, primary: false }) };
  const net: NetworkInfo = { adapters: [eth(), wifi(72), v], virtualCount: 5 };
  assert.deepEqual(visibleAdapters(net, false).map((a) => a.name), ['Ethernet', 'Wi-Fi']);
  assert.equal(visibleAdapters(net, true).length, 3);
  assert.equal(hiddenText(net, false), 'Vis alle (5 virtuelle skjult)');
  assert.equal(hiddenText({ ...net, virtualCount: 1 }, false), 'Vis alle (1 virtuel skjult)');
  assert.equal(hiddenText(net, true), 'Skjul virtuelle');
  assert.equal(hiddenText({ ...net, virtualCount: 0 }, false), null);
  assert.deepEqual(visibleAdapters({ adapters: [{ ...v, primary: true }], virtualCount: 1 }, false).length, 1);   // primær VPN/VM vises altid
});

test('detaljer: alt der kan læses, intet tomt', () => {
  const now = Date.parse('2026-11-20T12:00:00Z');
  const rows = Object.fromEntries(detailRows(eth({
    rxBytes: 2_345_000_000, txBytes: 1_500_000, rxRate: 1_550_000, txRate: 1550, rxErrors: 12, txErrors: 0, newErrors: 12, rxDrops: 3, txDrops: 0,
    linkChanges24h: 2, linkChangedAt: '2026-11-20T11:48:00Z', vlanId: 20, dnsSuffix: 'lan',
  }), now));
  assert.equal(rows['Hardware'], 'Intel(R) Ethernet Connection I219-V');
  assert.equal(rows['DHCP'], 'Ja');
  assert.equal(rows['IPv6'], 'Ja');
  assert.equal(rows['Duplex'], 'Fuld');
  assert.equal(rows['VLAN'], '20');
  assert.equal(rows['MTU'], '1500');
  assert.equal(rows['DNS-suffiks'], 'lan');
  assert.equal(rows['Trafik nu'], '↓ 12,4 Mbit/s   ↑ 12,4 kbit/s');
  assert.equal(rows['Trafik i alt'], '↓ 2,3 GB   ↑ 1,5 MB');
  assert.equal(rows['Fejl'], '12 ind · 0 ud (+12 siden sidst)');
  assert.equal(rows['Tabte pakker'], '3 ind · 0 ud');
  assert.equal(Object.fromEntries(detailRows(eth({ rxDrops: 0, txDrops: 0 })))['Tabte pakker'], '0');
  assert.equal(rows['Link op/ned (24 t)'], '2 gange, senest for 12 min. siden');
  assert.equal(rows['Netværk (SSID)'], undefined);   // ikke Wi-Fi
  const w = Object.fromEntries(detailRows(wifi(72)));
  assert.equal(w['Signal'], '72 % (-64 dBm)');
  assert.equal(w['Bånd og kanal'], '5 GHz · kanal 36 · 5180 MHz');
  assert.equal(w['Sikkerhed'], 'WPA2-Personal');
  assert.equal(w['Link-hastighed'], '↓ 866,7 Mbit/s   ↑ 780 Mbit/s');
  assert.equal(w['DHCP'], undefined);
  assert.equal(Object.fromEntries(detailRows(eth({ ipv6Enabled: false })))['IPv6'], 'Slået fra');
});

test('fejl og tid siden', () => {
  assert.equal(errorsText(eth({ rxErrors: 0, txErrors: 0 })), '0');
  assert.equal(errorsText(eth()), null);
  const now = Date.parse('2026-11-20T12:00:00Z');
  assert.equal(sinceText('2026-11-20T12:00:20Z', now), 'lige nu');
  assert.equal(sinceText('2026-11-20T09:00:00Z', now), 'for 3 t. siden');
  assert.equal(sinceText('2026-11-17T12:00:00Z', now), 'for 3 dage siden');
});
