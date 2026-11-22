// Test af login-linjen til netværksdrev (kør: npm test).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FILESERVER_LINK, fileServerLogin } from '../src/design/fileserver-login.ts';

test('bruger og adgangskode sat: drevene logger ind som brugeren', () => {
  assert.deepEqual(fileServerLogin({ share_user: ' pc-drev ', hasSharePass: true }), { state: 'ok', user: 'pc-drev' });
});

test('bruger uden adgangskode og intet login giver advarsler', () => {
  assert.equal(fileServerLogin({ share_user: 'pc-drev', hasSharePass: false }).state, 'nopass');
  assert.equal(fileServerLogin({ share_user: '', hasSharePass: true }).state, 'none');
  assert.equal(fileServerLogin(undefined).state, 'none');
});

test('linket peger på fanen i Indstillinger', () => {
  assert.equal(FILESERVER_LINK, '/indstillinger?fane=drev');
});
