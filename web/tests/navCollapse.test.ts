// Test af den sammenklappelige sidemenu (web/src/design/navCollapse.ts). Kør: npm test
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  NAV_COLLAPSED_KEY, NAV_WIDTH, NAV_WIDTH_EMBED, RAIL_WIDTH, canCollapse, isMacPlatform, isToggleShortcut, isTypingTarget, navWidth, parseCollapsed,
  railBadge, railLabel, railText, readCollapsed, serializeCollapsed, shortcutLabel, shouldToggle, toggleLabel, writeCollapsed,
} from '../src/design/navCollapse.ts';
import type { Counts } from '../src/nav.ts';

const zero: Counts = { machines: 0, upgrades: 0, alerts: 0, jobs: 0, servers: 0, checksDown: 0, messages: 0, access: 0 };
const key = (k: string, mods: Partial<{ ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean; repeat: boolean }> = {}) =>
  ({ key: k, ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...mods });

test('nøglen i localStorage følger den fælles regel <app>.nav.collapsed', () => {
  assert.equal(NAV_COLLAPSED_KEY, 'fleetdeck.nav.collapsed');
});

test('gemt værdi: kun "1"/"true" er sammenklappet, alt andet (også intet og skrald) er udfoldet', () => {
  assert.equal(parseCollapsed('1'), true);
  assert.equal(parseCollapsed('true'), true);
  for (const v of ['0', 'false', '', 'ja', '{}', null, undefined]) assert.equal(parseCollapsed(v), false, String(v));
  assert.equal(parseCollapsed(serializeCollapsed(true)), true);
  assert.equal(parseCollapsed(serializeCollapsed(false)), false);
});

test('læs/skriv tåler, at localStorage mangler eller kaster (privat vindue, iframe)', () => {
  const store = new Map<string, string>();
  const ok = { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) };
  assert.equal(readCollapsed(ok), false);
  writeCollapsed(ok, true);
  assert.equal(store.get('fleetdeck.nav.collapsed'), '1');
  assert.equal(readCollapsed(ok), true);
  const broken = { getItem: () => { throw new Error('SecurityError'); }, setItem: () => { throw new Error('QuotaExceeded'); } };
  assert.equal(readCollapsed(broken), false);
  assert.doesNotThrow(() => writeCollapsed(broken, true));
  assert.equal(readCollapsed(undefined), false);
});

test('genvej: Ctrl+B og ⌘B – ikke uden modifier, ikke med Alt/Shift, ikke ved gentagelse', () => {
  assert.ok(isToggleShortcut(key('b', { ctrlKey: true })));
  assert.ok(isToggleShortcut(key('B', { metaKey: true })));
  assert.ok(!isToggleShortcut(key('b')));
  assert.ok(!isToggleShortcut(key('k', { ctrlKey: true })));
  assert.ok(!isToggleShortcut(key('b', { ctrlKey: true, shiftKey: true })));
  assert.ok(!isToggleShortcut(key('b', { ctrlKey: true, altKey: true })));
  assert.ok(!isToggleShortcut(key('b', { ctrlKey: true, repeat: true })));
});

test('genvejen virker ikke, når man skriver i et tekstfelt', () => {
  const ctrlB = key('b', { ctrlKey: true });
  for (const t of [{ tagName: 'INPUT' }, { tagName: 'input', type: 'search' }, { tagName: 'TEXTAREA' }, { tagName: 'SELECT' }, { tagName: 'DIV', isContentEditable: true },
    { tagName: 'DIV', getAttribute: (n: string) => (n === 'role' ? 'textbox' : null) }]) {
    assert.ok(isTypingTarget(t), JSON.stringify(t));
    assert.ok(!shouldToggle(ctrlB, t));
  }
  for (const t of [null, undefined, { tagName: 'BODY' }, { tagName: 'A' }, { tagName: 'BUTTON' }, { tagName: 'INPUT', type: 'checkbox' }]) {
    assert.ok(!isTypingTarget(t), JSON.stringify(t));
    assert.ok(shouldToggle(ctrlB, t));
  }
});

test('kun sidemenu på bred skærm kan klappes sammen – topmenu, mobil og skjult menu (embed) er uændrede', () => {
  assert.ok(canCollapse({ navPos: 'side', wide: true, navbarShown: true }));
  assert.ok(!canCollapse({ navPos: 'top', wide: true, navbarShown: true }));
  assert.ok(!canCollapse({ navPos: 'side', wide: false, navbarShown: true }));
  assert.ok(!canCollapse({ navPos: 'side', wide: true, navbarShown: false }));
});

test('bredder: skinnen er ca. 64 px, ellers som før', () => {
  assert.equal(RAIL_WIDTH, 64);
  assert.equal(navWidth({ rail: false, embed: false }), NAV_WIDTH);
  assert.equal(navWidth({ rail: false, embed: true }), NAV_WIDTH_EMBED);
  assert.equal(navWidth({ rail: true, embed: false }), 64);
  assert.equal(navWidth({ rail: true, embed: true }), 64);
});

test('knaptekst og genvejstekst (dansk, ⌘ på Mac)', () => {
  assert.equal(toggleLabel(false), 'Klap menuen sammen');
  assert.equal(toggleLabel(true), 'Fold menuen ud');
  assert.ok(isMacPlatform('MacIntel') && isMacPlatform('iPad') && !isMacPlatform('Win32') && !isMacPlatform(undefined));
  assert.equal(shortcutLabel('MacIntel'), '⌘B');
  assert.equal(shortcutLabel('Linux x86_64'), 'Ctrl B');
});

test('mærker på skinnen: anmodninger og røde tal som lille tal, andre tal som prik, rene antal ingenting', () => {
  const c: Counts = { ...zero, machines: 12, servers: 2, access: 2, alerts: 14, upgrades: 5, messages: 1 };
  assert.deepEqual(railBadge({ count: 'machines', alert: 'access' }, c), { kind: 'number', text: '2', n: 2, alert: true });
  assert.equal(railBadge({ count: 'machines', alert: 'access' }, { ...c, access: 0 }), undefined, 'antal maskiner vises ikke');
  assert.equal(railBadge({ count: 'servers' }, c), undefined);
  assert.deepEqual(railBadge({ count: 'alerts', red: true }, c), { kind: 'number', text: '9+', n: 14 });
  assert.deepEqual(railBadge({ count: 'upgrades' }, c), { kind: 'dot', n: 5 });
  assert.equal(railBadge({ count: 'jobs' }, c), undefined, '0 giver intet mærke');
  assert.equal(railBadge({}, c), undefined);
  assert.equal(railText(3), '3');
  assert.equal(railText(10), '9+');
});

test('tooltip/aria-label på skinnen nævner tallet', () => {
  assert.equal(railLabel('Forside', undefined), 'Forside');
  assert.equal(railLabel('Opdateringer', { kind: 'dot', n: 5 }), 'Opdateringer (5)');
  assert.equal(railLabel('Maskiner', { kind: 'number', text: '2', n: 2, alert: true }), 'Maskiner · 2 venter på godkendelse');
});

test('App.tsx bruger logikken: nøgle, genvej, knap med aria-expanded og tooltips til højre', () => {
  const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
  assert.match(app, /readCollapsed\(/);
  assert.match(app, /shouldToggle\(/);
  assert.match(app, /aria-expanded=\{!collapsed\}/);
  assert.match(app, /position="right"/);
  const css = readFileSync(new URL('../src/design/fleetdeck.css', import.meta.url), 'utf8');
  assert.match(css, /prefers-reduced-motion: reduce\)[\s\S]*fd-navbar/, 'ingen animation ved prefers-reduced-motion');
});
