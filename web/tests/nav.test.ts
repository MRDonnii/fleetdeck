// Test af menuen delt efter opgave (web/src/nav.ts). Kør: npm test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { NAV_GROUPS, TABBAR, activeGroup, alertOf, badgeText, countOf, flatNav, groupCount, isActive, navFor, type Counts, type NavPage } from '../src/nav.ts';

const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
const staff = app.slice(app.indexOf('function StaffApp'));
const pages = NAV_GROUPS.flatMap((g) => g.items).filter((e): e is NavPage => e !== 'devices');
const devices: NavPage[] = [{ to: '/ipads', label: 'iPads', icon: 'x' }, { to: '/appletv', label: 'Apple TV', icon: 'x' }];
const zero: Counts = { machines: 0, upgrades: 0, alerts: 0, jobs: 0, servers: 0, checksDown: 0, messages: 0, access: 0 };

test('ingen side er forsvundet: alle sider fra før grupperingen er stadig i menuen', () => {
  const before = ['/', '/maskiner', '/opdateringer', '/helbred', '/apps', '/pakker', '/profiler', '/printere', '/beskeder', '/overvaagning', '/terminal',
    '/jobs', '/servere', '/udrulning', '/esp', '/scripts', '/winsetup', '/debloat', '/rapporter', '/log', '/indstillinger'];
  const all = flatNav(navFor(NAV_GROUPS, { isAdmin: true, devices })).map((p) => p.to);
  for (const to of before) assert.ok(all.includes(to), `${to} mangler i menuen`);
  assert.ok(all.includes('/ipads') && all.includes('/appletv'), 'enhederne sættes ind');
});

test('hver side i App.tsx har et menupunkt, og hvert menupunkt har en side (gamle links virker)', () => {
  const routes = [...staff.matchAll(/<Route path="([^"]+)"/g)].map((m) => m[1]).filter((p) => p !== '*');
  assert.ok(routes.length > 20);
  const nav = flatNav(navFor(NAV_GROUPS, { isAdmin: true, devices }));
  for (const r of routes) {
    const base = r.replace(/\/:.*$/, '') || '/';
    assert.ok(nav.some((p) => isActive(p.to, base)), `siden ${r} har intet menupunkt`);
  }
  for (const p of pages) assert.ok(routes.some((r) => (r.replace(/\/:.*$/, '') || '/') === p.to), `menupunktet ${p.to} har ingen side`);
});

test('hver side står kun ét sted, og hver gruppe har overskrift, kort navn og mindst én side', () => {
  const tos = pages.map((p) => p.to);
  assert.equal(new Set(tos).size, tos.length);
  assert.equal(NAV_GROUPS.filter((g) => g.items.includes('devices')).length, 1);
  assert.equal(new Set(NAV_GROUPS.map((g) => g.key)).size, NAV_GROUPS.length);
  for (const g of NAV_GROUPS) { assert.ok(g.label && g.short && g.items.length, g.key); assert.ok(g.short.length <= 12, `${g.short} er for langt til toppen`); }
  assert.ok(NAV_GROUPS.length >= 4 && NAV_GROUPS.length <= 7, 'få grupper');
});

test('alle ikonnavne findes i App.tsx', () => {
  const icons = app.slice(app.indexOf('const ICONS'), app.indexOf('};', app.indexOf('const ICONS')));
  for (const p of pages) assert.match(icons, new RegExp(`\\b${p.icon}:`), `ikonet ${p.icon} mangler`);
});

test('læsere ser ikke administrator-sider; tomme grupper forsvinder', () => {
  const viewer = navFor(NAV_GROUPS, { isAdmin: false, devices: [] });
  const tos = flatNav(viewer).map((p) => p.to);
  for (const t of ['/beskeder', '/terminal', '/debloat', '/rapporter', '/log']) assert.ok(!tos.includes(t), t);
  assert.ok(viewer.every((g) => g.items.length > 0));
  const onlyAdmin = navFor([{ key: 'x', label: 'X', short: 'X', items: [{ to: '/log', label: 'L', icon: 'audit', admin: true }] }], { isAdmin: false });
  assert.equal(onlyAdmin.length, 0);
});

test('telefonens fanelinje: fire sider, som alle (også læsere) har', () => {
  assert.equal(TABBAR.length, 4);
  const tos = flatNav(navFor(NAV_GROUPS, { isAdmin: false })).map((p) => p.to);
  for (const t of TABBAR) assert.ok(tos.includes(t), t);
});

test('aktiv side og gruppe', () => {
  assert.ok(isActive('/', '/'));
  assert.ok(!isActive('/', '/maskiner'));
  assert.ok(isActive('/maskiner', '/maskiner/12/programmer'));
  assert.ok(!isActive('/apps', '/appletv'), '/apps må ikke lyse ved Apple TV');
  assert.ok(isActive('/beskeder', '/beskeder/4'));
  const g = navFor(NAV_GROUPS, { isAdmin: true, devices });
  assert.equal(activeGroup(g, '/appletv/3')?.key, 'maskiner');
  assert.equal(activeGroup(g, '/esp')?.key, 'netvaerk');
  assert.equal(activeGroup(g, '/indstillinger')?.key, 'system');
  assert.equal(activeGroup(g, '/ukendt'), undefined);
});

test('tal: siderne viser kun tal over 0 (Maskiner altid), grupperne summerer uden antal maskiner/servere', () => {
  const c: Counts = { ...zero, machines: 6, servers: 1, alerts: 2, checksDown: 1, upgrades: 9, messages: 3 };
  const by = (to: string) => pages.find((p) => p.to === to)!;
  assert.equal(countOf(by('/maskiner'), zero), 0);
  assert.equal(countOf(by('/opdateringer'), zero), undefined);
  assert.equal(countOf(by('/helbred'), c), 2);
  assert.equal(countOf(by('/pakker'), c), undefined);
  const g = Object.fromEntries(navFor(NAV_GROUPS, { isAdmin: true, devices }).map((x) => [x.key, x]));
  assert.deepEqual(groupCount(g.oversigt, c), { n: 3, red: true });
  assert.deepEqual(groupCount(g.maskiner, c), { n: 3, red: false });
  assert.deepEqual(groupCount(g.programmer, c), { n: 9, red: false });
  assert.equal(groupCount(g.netvaerk, c), undefined, 'antal servere er ikke noget at gøre ved');
  assert.equal(groupCount(g.system, c), undefined);
  assert.equal(badgeText(100), '99+');
  assert.equal(badgeText(7), '7');
});

test('anmodninger om adgang: rødt tal ved Maskiner og i gruppen, kun når der er nogen', () => {
  const by = (to: string) => pages.find((p) => p.to === to)!;
  assert.equal(alertOf(by('/maskiner'), zero), undefined);
  assert.equal(alertOf(by('/maskiner'), { ...zero, access: 2 }), 2);
  assert.equal(alertOf(by('/helbred'), { ...zero, access: 2 }), undefined, 'kun Maskiner');
  assert.equal(countOf(by('/maskiner'), { ...zero, machines: 6, access: 2 }), 6, 'antallet af maskiner står stadig der');
  const g = Object.fromEntries(navFor(NAV_GROUPS, { isAdmin: true, devices }).map((x) => [x.key, x]));
  assert.deepEqual(groupCount(g.maskiner, { ...zero, machines: 6, access: 2 }), { n: 2, red: true });
  assert.equal(groupCount(g.maskiner, { ...zero, machines: 6 }), undefined);
  assert.match(staff, /access: waitingCount\(accessRequests\)/);
  assert.match(app, /alertOf\(n, c\)/);
});

test('søgningen findes i alle visninger: sidemenu, menu i toppen, telefon og indlejret', () => {
  assert.match(staff, /!top && <SearchField visibleFrom="sm"/);
  assert.match(staff, /top && <SearchField variant="compact"/);
  assert.match(staff, /<SearchField variant="phone" hiddenFrom="sm"/);
  // indlejret sidemenu: søgefeltet, eller en søgeknap med tooltip, når menuen er klappet sammen til en skinne
  assert.match(staff, /embedSide && <AppShell.Section[^>]*>\s*\{rail\s*\? <Tooltip label="Søg \(Ctrl K\)"[\s\S]*?: <SearchField variant="compact"/);
  assert.match(staff, /embed && !embedSide[\s\S]*?spotlight\.open\(\)/);
});
