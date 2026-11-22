// Login, der lykkes hos serveren, men ikke holder i browseren (kør: npm test).
// Fleetdeck vist inde i en anden side (fx Kontrolrums iframe): "Åbn via denne pc" åbnede http://localhost:8587 I iframen. Dér er
// siden tredjepart, så Chrome gemmer ikke ws_session (SameSite=Lax) – login svarer 200, /api/auth/me 401, og login-siden bliver stående.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { localLink } from '../src/design/biosText.ts';
import { isFramed, sessionLostText } from '../src/design/loginSession.ts';

test('"Åbn via denne pc" åbner altid et nyt vindue (aldrig inde i en iframe)', () => {
  assert.deepEqual(localLink('/maskiner/4/opdateringer?bios=usb'),
    { href: 'http://localhost:8587/maskiner/4/opdateringer?bios=usb', target: '_blank', rel: 'noopener' });
  assert.equal(localLink('/esp', 9000).href, 'http://localhost:9000/esp');
});

test('indlejret: vinduet er ikke det øverste, også når det øverste ikke må læses', () => {
  const w = {} as Window;
  assert.equal(isFramed({ self: w, top: w }), false);
  assert.equal(isFramed({ self: w, top: {} as Window }), true);
  assert.equal(isFramed({ self: w, top: null }), true);
  const blocked = { self: w, get top(): Window { throw new Error('SecurityError'); } };
  assert.equal(isFramed(blocked), true);
});

test('login uden session giver en forklaring i stedet for ingenting', () => {
  assert.match(sessionLostText(true), /nyt vindue/);
  assert.match(sessionLostText(false), /cookie/);
  assert.notEqual(sessionLostText(true), sessionLostText(false));
});
