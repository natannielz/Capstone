import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { accessMessage, AccessFailure } from '../src/modules/administration/workflow';
import { IdentityFailure, identityMessage } from '../src/modules/identity/identity-workflow';

test('a missing session is a sign-in matter, never reported as a lost connection (G-015)', () => {
  for (const failure of [new IdentityFailure(401, 'unauthenticated'), new AccessFailure(401, 'unauthenticated')]) {
    assert.doesNotMatch(accessMessage(failure), /Koneksi terputus/);
    assert.match(accessMessage(failure), /^Masuk dengan akun organisasi Anda/);
  }
  assert.match(accessMessage(new TypeError('Failed to fetch')), /Koneksi terputus/, 'a real network failure keeps its own words');
  assert.doesNotMatch(identityMessage(new IdentityFailure(401, 'unauthenticated')), /berakhir/, 'a first-time visitor is not told a session ended (G-013)');
  assert.match(identityMessage(new IdentityFailure(401, 'session_expired')), /^Sesi Anda perlu diperbarui/);
});

const source = (file: string) => readFileSync(new URL('../src/modules/' + file, import.meta.url), 'utf8');

test('the receipt page and the access page show one shared state, without a stacked error (G-015, G-016)', () => {
  const receipt = source('fulfillment/ReceiptWorkspace.tsx');
  assert.match(receipt, /<SessionAccessState /);
  assert.match(receipt, /\{error !== null && account && !expired && <p role="alert"/, 'the error alert only shows inside the signed-in page');
  const access = source('administration/AccessAdministration.tsx');
  assert.match(access, /<SessionAccessState /);
  assert.match(access, /\{error !== null && pageState === "ready" && <div className=\{styles\.alert\}/, 'no error with trace id above the signed-out state');
});
