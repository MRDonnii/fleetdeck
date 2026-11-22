// Test af "Anmod om adgang" i administratorens liste (web/src/design/accessRequestsText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { approveBlocker, BLOCK_DURATIONS, blockAffects, blockLine, blockSummary, describe, expiresIn, isOpen, lastSeen, sortRows, STATE, validName, waitingCount } from '../src/design/accessRequestsText.ts';
import type { AccessRequestRow } from '../src/types.ts';

const row = (id: number, state: AccessRequestRow['state'], extra: Partial<AccessRequestRow> = {}): AccessRequestRow => ({
  id, code: '7K4-Q2M', name: 'STUE-PC', platform: 'windows', os: 'Windows 11 Pro', model: 'OptiPlex 7090', requestedBy: 'anna', message: null,
  ip: '192.168.1.50', created: '2026-11-20T12:00:00Z', expires: '2026-11-21T12:00:00Z', lastPoll: null, state, profileId: null, approvedName: null,
  decidedBy: null, decidedAt: null, rejectReason: null, machineId: null, machineName: null, pinVerified: true, conflict: null, unencrypted: false, macs: [], ipKey: '192.168.1.50', ipShared: null, ...extra,
});

test('tallet i menuen tæller kun dem, der venter på administratoren', () => {
  assert.equal(waitingCount(undefined), 0);
  assert.equal(waitingCount([row(1, 'pending'), row(2, 'approved'), row(3, 'issued'), row(4, 'rejected'), row(5, 'pending')]), 2);
});

test('åbne anmodninger øverst (ventende først), derefter de nyeste', () => {
  const sorted = sortRows([row(1, 'enrolled'), row(2, 'approved'), row(3, 'pending'), row(4, 'expired'), row(5, 'pending')]).map((r) => r.id);
  assert.deepEqual(sorted, [5, 3, 2, 4, 1]);
  assert.ok(isOpen(row(1, 'issued')) && !isOpen(row(1, 'key_sent')));
});

test('godkend kræver profil, gyldigt navn og bekræftet kode', () => {
  assert.match(approveBlocker({ profile: null, name: 'PC', confirmed: true })!, /profil/);
  assert.match(approveBlocker({ profile: '0', name: 'ugyldigt navn!', confirmed: true })!, /A-Z/);
  assert.match(approveBlocker({ profile: '3', name: 'PC', confirmed: false })!, /koden/);
  assert.equal(approveBlocker({ profile: '0', name: 'stue-pc', confirmed: true }), null, '"Ingen profil" er et gyldigt valg');
  assert.ok(validName('KONTOR_2') && !validName('-PC') && !validName('A'.repeat(32)));
});

test('tekster: tilstande, beskrivelse og udløb', () => {
  for (const s of ['pending', 'approved', 'issued', 'enrolled', 'rejected', 'expired', 'key_sent', 'superseded'] as const) assert.ok(STATE[s].label);
  assert.equal(describe(row(1, 'pending')), 'Windows · Windows 11 Pro · OptiPlex 7090');
  assert.equal(describe(row(1, 'pending', { platform: 'mac', os: null, model: null })), 'Mac');
  const now = new Date('2026-11-20T12:00:00Z');
  assert.equal(expiresIn('2026-11-21T11:00:00Z', now), 'om 23 t.');
  assert.equal(expiresIn('2026-11-20T12:10:00Z', now), 'om 10 min.');
  assert.equal(expiresIn('2026-11-20T11:00:00Z', now), 'udløbet');
});

test('listen ligger på Maskiner (kun administrator), godkend-dialogen kræver bekræftelse, og nøglen står i fragmentet', () => {
  const machines = readFileSync(new URL('../src/pages/Machines.tsx', import.meta.url), 'utf8');
  const ui = readFileSync(new URL('../src/design/accessrequests.tsx', import.meta.url), 'utf8');
  assert.match(machines, /isAdmin && <AccessRequestsSection \/>/);
  assert.match(ui, /er bekræftet med brugeren/);
  assert.match(ui, /disabled=\{!!blocker\}/);
  assert.match(ui, /Send nøgle i stedet/);
  assert.match(ui, /nøglen står efter #/);
  const download = readFileSync(new URL('../src/pages/Download.tsx', import.meta.url), 'utf8');
  assert.match(download, /Anmod om adgang/);
});

test('sidst set: en anmodning, der ikke spørger længere, markeres som død', () => {
  const now = new Date('2026-11-20T12:10:00Z');
  assert.deepEqual(lastSeen(row(1, 'pending', { lastPoll: '2026-11-20T12:09:30Z' }), now), { text: 'Maskinen spurgte lige nu', silent: false });
  assert.equal(lastSeen(row(1, 'pending', { lastPoll: '2026-11-20T12:08:00Z' }), now).silent, false);
  const dead = lastSeen(row(1, 'pending', { lastPoll: '2026-11-20T11:58:00Z' }), now);
  assert.ok(dead.silent && dead.text.includes('12 min. siden'), dead.text);
  assert.ok(lastSeen(row(1, 'pending', { lastPoll: null, created: '2026-11-20T12:00:00Z' }), now).text.includes('ikke spurgt'));
});

test('Bloker: sætningen siger præcis, hvad der blokeres og hvor længe', () => {
  const r = row(1, 'pending', { macs: ['AA:BB:CC:DD:EE:01'] });
  assert.deepEqual(blockSummary(r, { ip: true, mac: false, name: false, hours: '24' }), { text: 'Blokerer IP 192.168.1.50 i 24 timer.', blocker: null });
  assert.equal(blockSummary(r, { ip: true, mac: true, name: true, hours: '168' }).text, 'Blokerer IP 192.168.1.50, MAC AA:BB:CC:DD:EE:01 og navnet STUE-PC i 7 dage.');
  assert.equal(blockSummary(row(1, 'pending', { macs: ['A', 'B'] }), { ip: false, mac: true, name: false, hours: '1' }).text, 'Blokerer 2 MAC-adresser i 1 time.');
  assert.equal(blockSummary(r, { ip: false, mac: false, name: false, hours: '24' }).blocker, 'Vælg mindst én ting at blokere.');
  assert.equal(blockSummary(r, { ip: true, mac: false, name: false, hours: null }).blocker, 'Vælg hvor længe.');
  // ingen MAC i anmodningen: valget tæller ikke
  assert.equal(blockSummary(row(1, 'pending'), { ip: false, mac: true, name: false, hours: '24' }).blocker, 'Vælg mindst én ting at blokere.');
  assert.match(blockSummary(row(1, 'pending', { ip: 'socket' }), { ip: true, mac: false, name: false, hours: '24' }).blocker!, /egen vært/);
  // altid med udløb – højst 90 dage (som serveren)
  assert.ok(BLOCK_DURATIONS.every((d) => Number(d.value) > 0 && Number(d.value) <= 24 * 90));
});

test('Bloker: IPv6 vises som det /64-net, serveren blokerer', () => {
  const r = row(1, 'pending', { ip: '2001:db8:1:2:aa:bb:cc:dd', ipKey: '2001:db8:1:2::/64' });
  assert.equal(blockSummary(r, { ip: true, mac: false, name: false, hours: '24' }).text, 'Blokerer IP 2001:db8:1:2::/64 i 24 timer.');
});

test('Bloker: viser de andre åbne anmodninger, der afvises med det samme', () => {
  const r = row(1, 'pending', { macs: ['AA:BB:CC:DD:EE:01'] });
  const all = [r,
    row(2, 'pending', { name: 'ARBEJDS-PC' }),                                   // samme IP
    row(3, 'pending', { name: 'STUE-PC', ip: '192.168.1.9', ipKey: '192.168.1.9' }),   // samme navn
    row(4, 'pending', { name: 'X', ip: '192.168.1.8', ipKey: '192.168.1.8', macs: ['AA:BB:CC:DD:EE:01'] }),   // samme MAC
    row(5, 'rejected', { name: 'GAMMEL' })];                                     // ikke åben
  const ids = (c: Parameters<typeof blockAffects>[2]) => blockAffects(r, all, c).map((o) => o.id);
  assert.deepEqual(ids({ ip: true, mac: false, name: false, hours: '24' }), [2]);
  assert.deepEqual(ids({ ip: false, mac: false, name: true, hours: '24' }), [3]);
  assert.deepEqual(ids({ ip: false, mac: true, name: false, hours: '24' }), [4]);
  assert.deepEqual(ids({ ip: true, mac: true, name: true, hours: '24' }), [2, 3, 4]);
});

test('Bloker: delt IP er ikke valgt på forhånd, og MAC/navn er markeret som oplyst af enheden', () => {
  const src = readFileSync(new URL('../src/design/accessrequests.tsx', import.meta.url), 'utf8');
  assert.match(src, /ip: r\.ip !== 'socket' && !r\.ipShared/);
  assert.equal((src.match(/oplyst af enheden/g) ?? []).length, 2);
});

test('Bloker: linjen i listen viser udløb og afviste forsøg', () => {
  const now = new Date('2026-11-20T12:00:00Z');
  assert.equal(blockLine({ until: '2026-11-21T11:00:00Z', hits: 0, lastHit: null }, now), 'Udløber om 23 t. · ingen forsøg siden');
  assert.equal(blockLine({ until: '2026-11-27T12:00:00Z', hits: 4, lastHit: '2026-11-20T11:58:00Z' }, now), 'Udløber om 7 d. · 4 afviste forsøg, sidst 2 min. siden');
  assert.equal(blockLine({ until: '2026-11-20T11:00:00Z', hits: 1, lastHit: null }, now), 'Udløbet · 1 afvist forsøg');
});

test('Bloker-knappen findes på kortet og i listen over afsluttede, og blokeringer kan ophæves', () => {
  const src = readFileSync(new URL('../src/design/accessrequests.tsx', import.meta.url), 'utf8');
  assert.match(src, /api\/access-requests\/\$\{r\.id\}\/block/);
  assert.match(src, /del\(`\/api\/access-blocks\/\$\{b\.id\}`\)/);
  assert.equal((src.match(/>Bloker…<\/Button>/g) ?? []).length, 2);
});
