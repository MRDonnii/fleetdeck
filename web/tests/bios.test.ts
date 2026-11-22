// Test af BIOS-teksterne og de afledte tilstande (web/src/design/biosText.ts). Kør: npm test.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  backupFresh, biosBadgeTip, biosBadges, biosStateLine, bytes, clock, diffKind, diffSummary, fleetCounts, fleetFilter, fleetQuiet, importantChange, localUrl,
  backupLabel, mergeChecks, offlineFor, onLocalhost, restoreButton, routeLabel, routeLong, USB_ONLY, shortVersion, updateTimeline, usbChoosable, usbDiskName, USB_EXTERNAL_DRIVE, versionsBehind, wizardStepFor,
} from '../src/design/biosText.ts';
import type { BiosDiffRow, BiosFleetRow, BiosStatus, BiosUpdateRun } from '../src/types.ts';

const status = (p: Partial<BiosStatus> = {}): BiosStatus => ({
  machineId: 1, vendor: 'lenovo', state: 'newer', critical: false, changes: [], checkedAt: '2026-11-20T10:00:00Z', source: 'Lenovo-katalog',
  current: 'Q9ZKT75A', latest: 'Q9ZKT79A', ...p,
});
const ch = (v: string, text = 'Rettelser', important = false) => ({ version: v, lines: [{ text, important }] });

test('linjen "Nyeste" følger tilstanden', () => {
  assert.deepEqual(biosStateLine(status({ state: 'newest' })), { text: 'Nyeste version', tone: 'green' });
  const four = status({ changes: [ch('Q9ZKT79A'), ch('Q9ZKT78A'), ch('Q9ZKT77A'), ch('Q9ZKT76A')] });
  assert.deepEqual(biosStateLine(four), { text: '4 versioner nyere', tone: 'yellow' });
  assert.equal(biosStateLine(status({ critical: true })).tone, 'red');
  assert.equal(biosStateLine(null).tone, 'gray');
  assert.match(biosStateLine(status({ state: 'virtual' })).text, /værten/);
  assert.match(biosStateLine(status({ state: 'catalog_error' }), 'Katalog nede siden 18. nov.').text, /18\. nov/);
  assert.equal(versionsBehind(four), 4);
  assert.equal(versionsBehind(status({ state: 'newest' })), 0);
});

test('mærker: bagud og vigtig (Secure Boot)', () => {
  const s = status({ changes: [ch('Q9ZKT79A'), ch('Q9ZKT78A', 'Microsoft UEFI CA 2023 og KEK 2023', true)] });
  assert.deepEqual(biosBadges(s).map((b) => b.text), ['2 versioner bagud', 'Vigtig']);
  assert.deepEqual(importantChange(s), { version: 'Q9ZKT78A', text: 'Microsoft UEFI CA 2023 og KEK 2023' });
  assert.deepEqual(biosBadges(status({ state: 'mac' })).map((b) => b.text), ['Opdateres med macOS']);
  assert.equal(shortVersion('Q9ZKT79A'), '79A');
  assert.equal(shortVersion('1.36.1'), '1.36.1');
});

test('flåden: filtre, tal og søgning', () => {
  const rows: BiosFleetRow[] = [
    { machineId: 1, machine: 'reception-pc', platform: 'linux', model: 'ThinkCentre', status: status() },
    { machineId: 2, machine: 'arbejds-pc', platform: 'windows', model: 'OptiPlex', status: status({ state: 'newest' }) },
    { machineId: 3, machine: 'ny-pc', platform: 'windows', model: null, status: null },
    { machineId: 4, machine: 'server-vm', platform: 'linux', model: 'QEMU', status: status({ state: 'virtual' }) },
  ];
  assert.deepEqual(fleetCounts(rows), { behind: 1, all: 4, unchecked: 1 });
  assert.deepEqual(fleetFilter(rows, 'behind', '').map((r) => r.machineId), [1]);
  assert.deepEqual(fleetFilter(rows, 'unchecked', '').map((r) => r.machineId), [3]);
  assert.deepEqual(fleetFilter(rows, 'all', 'optiplex').map((r) => r.machineId), [2]);
  assert.equal(fleetQuiet(rows), 2);
});

const run = (p: Partial<BiosUpdateRun>): BiosUpdateRun => ({
  id: 1, state: 'staged', effective: 'staged', route: 'capsule', fromVersion: 'Q9ZKT75A', targetVersion: 'Q9ZKT79A', checks: [], createdAt: '2026-11-20T14:02:00Z',
  updatedAt: '2026-11-20T14:06:00Z', createdBy: 'admin', canReboot: true, canVerify: false, canCompare: false, ...p,
});

test('guiden fortsætter på det rigtige trin', () => {
  assert.equal(wizardStepFor(null), null);
  assert.equal(wizardStepFor(run({ effective: 'checked' })), null);
  assert.equal(wizardStepFor(run({ effective: 'rebooting' })), 3);
  assert.equal(wizardStepFor(run({ effective: 'updated' })), 4);
  assert.equal(wizardStepFor(run({ effective: 'no_report' })), 4);
});

test('forløbet: genstart med ur, derefter kontrol', () => {
  const now = new Date('2026-11-20T14:09:41Z').getTime();
  const t = updateTimeline(run({ effective: 'rebooting', preSnapshotId: 5, stagedAt: '2026-11-20T14:06:00Z', rebootRequestedAt: '2026-11-20T14:07:00Z' }), now, 'capsule');
  const inst = t.find((x) => x.text.startsWith("BIOS'en installerer"))!;
  assert.equal(inst.state, 'run');
  assert.match(inst.sub!, /2:41/);
  assert.equal(t[0].state, 'done');
  const done = updateTimeline(run({ effective: 'updated', canCompare: true, rebootRequestedAt: '2026-11-20T14:07:00Z', finishedAt: '2026-11-20T14:10:12Z' }), now, 'capsule');
  assert.ok(done.every((x) => x.state === 'done'));
  assert.match(done[3].sub!, /3 min 12 s/);
  assert.equal(updateTimeline(run({ effective: 'rejected' }), now, 'capsule').at(-2)!.state, 'bad');
  assert.equal(clock('2026-11-20T14:07:00Z', now), '2:41');
  assert.equal(offlineFor('2026-11-20T14:07:00Z', '2026-11-20T14:07:30Z'), '30 s');
});

test('kontroller: agentens vinder, stop først', () => {
  const m = mergeChecks([{ id: 'package', state: 'warn', text: 'ikke hentet' }, { id: 'route', state: 'ok', text: 'kapsel' }],
    [{ id: 'package', state: 'ok', text: 'SHA-256 passer' }, { id: 'power', state: 'fail', text: 'Batteri' }]);
  assert.deepEqual(m.map((c) => `${c.id}:${c.state}`), ['power:fail', 'package:ok', 'route:ok']);
});

const row = (p: Partial<BiosDiffRow>): BiosDiffRow => ({ name: 'x', state: 'changed', rule: 'common', valid: true, selectable: true, preselected: true, ...p });

test('sammenlign: typer og opsummering', () => {
  assert.equal(diffKind(row({})).label, 'Almindelig');
  assert.equal(diffKind(row({ rule: 'ask' })).label, 'Kræver eget valg');
  assert.equal(diffKind(row({ rule: 'never' })).label, 'Gendannes ikke');
  assert.equal(diffKind(row({ state: 'new' })).label, 'Ny');
  const rows = [row({ name: 'a' }), row({ name: 'b' }), row({ name: 'c', rule: 'ask', preselected: false }), row({ name: 'd', rule: 'never', selectable: false }), row({ name: 'e', state: 'new', selectable: false })];
  assert.deepEqual(diffSummary(rows, new Set(['a', 'b'])).map((x) => x.text), ['2 valgt', '1 kræver eget valg', '1 gendannes ikke', '1 ny']);
  assert.deepEqual(diffSummary(rows, new Set(['a', 'b', 'c'])).map((x) => x.text), ['3 valgt', '1 gendannes ikke', '1 ny']);
  assert.equal(restoreButton(2), 'Gendan 2 indstillinger…');
  assert.equal(restoreButton(1), 'Gendan 1 indstilling…');
});

test('USB-stik: kun fra localhost, navne og store diske', () => {
  assert.ok(onLocalhost('localhost') && onLocalhost('127.0.0.1'));
  assert.ok(!onLocalhost('fleetdeck.example.org'));
  assert.equal(localUrl('/maskiner/4/opdateringer'), 'http://localhost:8587/maskiner/4/opdateringer');
  assert.equal(usbDiskName({ vendor: 'Generic', model: 'Flash Disk', size: 16_000_000_000 }), 'Generic Flash Disk · 16 GB');
  assert.equal(bytes(7_600_000), '7,6 MB');
  const d = { vendor: '', model: '', size: 1, serial: 's', selectable: false, large: true, why: 'Disken er større end 256 GB.' };
  assert.ok(usbChoosable(d, 256_000_000_000));
  assert.ok(!usbChoosable({ ...d, large: false }, 256_000_000_000));
  // en ekstern harddisk/SSD (Mac) kræver den samme ekstra bekræftelse
  assert.ok(usbChoosable({ ...d, why: USB_EXTERNAL_DRIVE }, 256_000_000_000));
  // en stor disk, der sad i før guiden (fx en backupdisk), kan aldrig vælges
  assert.ok(!usbChoosable({ ...d, why: 'Stikket sad i, før guiden begyndte. Tag det ud og sæt det i igen.' }, 256_000_000_000));
  assert.equal(localUrl('/maskiner/4/opdateringer?bios=usb', 9000), 'http://localhost:9000/maskiner/4/opdateringer?bios=usb');
  const t = new Date(2026, 10, 20, 14, 2).getTime();
  assert.equal(backupLabel(null, t), 'Ingen');
  assert.equal(backupLabel(new Date(2026, 10, 20, 9, 5).toISOString(), t), 'I dag 09:05');
  assert.equal(backupLabel(new Date(2026, 10, 3, 9, 5).toISOString(), t), '3. nov.');
  assert.equal(routeLabel('capsule'), 'UEFI-kapsel ved genstart');
  assert.equal(routeLabel(null, 'dell'), 'Dells program');
  // Lenovos rå ROM på Linux: ingen kapsel-vej, kun USB-stikket – og aldrig "Secure Boot skal ikke slås fra"
  assert.equal(routeLabel(null, 'lenovo', true), 'USB-stik');
  assert.equal(routeLong(null), USB_ONLY);
  assert.ok(!routeLong(null, 'Lenovos pakke til denne model er en rå ROM …').includes('Secure Boot skal ikke'));
  assert.ok(routeLong('capsule').includes('rigtig UEFI-kapsel'));
  const now = new Date('2026-11-20T14:00:00Z').getTime();
  assert.ok(backupFresh('2026-11-20T02:00:00Z', now));
  assert.ok(!backupFresh('2026-11-18T14:00:00Z', now));
  assert.ok(!backupFresh(null, now));
});

test('flådetabellen: mærkerne afkortes ikke, og tooltippen forklarer dem (skitse bios-7)', () => {
  const s = status({ current: 'M1UKT75A', latest: 'M1UKT79A', changes: [ch('M1UKT79A'), ch('M1UKT78A', 'Microsoft UEFI CA 2023 og KEK 2023', true), ch('M1UKT77A'), ch('M1UKT76A')] });
  const [behind, imp] = biosBadges(s);
  assert.equal(behind.text, '4 versioner bagud');
  assert.equal(biosBadgeTip(s, behind), 'M1UKT75A → M1UKT79A · nyere versioner: 79A, 78A, 77A, 76A');
  assert.equal(biosBadgeTip(s, imp), 'Vigtig: 78A – Microsoft UEFI CA 2023 og KEK 2023');
  assert.equal(biosBadgeTip(status({ critical: true, changes: [] }), { text: 'Vigtig', important: true }), 'Leverandøren markerer opdateringen som kritisk.');
  assert.equal(biosBadgeTip(status({ state: 'virtual' }), biosBadges(status({ state: 'virtual' }))[0]), 'Virtuel maskine. BIOS styres af værten.');
  assert.match(biosBadgeTip(null, { text: 'Ikke tjekket' }), /Ikke tjekket/);
  // Mantines Badge-label har overflow: hidden (så den kan presses til "3 versione…"): i flådetabellen er den synlig og med tooltip
  const src = readFileSync(new URL('../src/design/bios.tsx', import.meta.url), 'utf8');
  const fleet = src.slice(src.indexOf('export const FleetBadges'), src.indexOf('export const BiosBadge'));
  assert.match(fleet, /label: \{ overflow: 'visible'/);
  assert.match(fleet, /<Tooltip key=\{x\.text\} label=\{biosBadgeTip\(s, x\)\}/);
  assert.equal((src.match(/<FleetBadges s=\{s\} \/>/g) ?? []).length, 2);
});
