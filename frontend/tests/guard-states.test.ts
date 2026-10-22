import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { accessMessage, AccessFailure } from '../src/modules/administration/workflow';
import { IdentityFailure, identityMessage } from '../src/modules/identity/identity-workflow';

test('a missing session is a sign-in matter, never reported as a lost connection (G-015)', () => {
  for (const failure of [new IdentityFailure(401, 'unauthenticated'), new AccessFailure(401, 'unauthenticated')]) {
    assert.doesNotMatch(accessMessage(failure), /Koneksi terputus/);
    // Option A (round 3): behind the request guard a 401 means a session that ended; inline texts say so too (flow C).
    assert.match(accessMessage(failure), /^Sesi Anda perlu diperbarui\. Masuk kembali/);
  }
  assert.match(accessMessage(new TypeError('Failed to fetch')), /Koneksi terputus/, 'a real network failure keeps its own words');
  assert.match(identityMessage(new IdentityFailure(401, 'unauthenticated')), /^Sesi Anda perlu diperbarui/);
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

test('a session that changed hands is an actor change, not "no session" (round 3)', () => {
  const delegation = source('administration/TemporaryDelegation.tsx');
  assert.match(delegation, /session\.account\.userId !== current\.userId\) throw new AccessFailure\(409, 'actor_changed'\)/);
  assert.doesNotMatch(delegation, /userId !== current\.userId\) throw new IdentityFailure\(401/);
});

test('a refused page shows one denial: no page-level error alert stacked above the shared state (G-042)', async () => {
  const { readdirSync, statSync } = await import('node:fs');
  const { join } = await import('node:path');
  const { fileURLToPath } = await import('node:url');
  const root = fileURLToPath(new URL('../src/modules', import.meta.url)), offenders: string[] = [];
  const walk = (directory: string) => { for (const name of readdirSync(directory)) { const path = join(directory, name);
    if (statSync(path).isDirectory()) { walk(path); continue; } if (!name.endsWith('.tsx')) continue;
    const lines = readFileSync(path, 'utf8').split(/\r?\n/);
    lines.forEach((line, index) => { if (!line.includes('<SessionAccessState')) return;
      for (const near of lines.slice(Math.max(0, index - 3), index + 1))
        for (const alert of near.match(/\{(error|failure) !== null && [^{}]*?<(p|div)[^>]*role="alert"/g) ?? [])
          if (!/account|loaded|current|pageState|denied/.test(alert)) offenders.push(path.slice(root.length) + ':' + (index + 1));
    }); } };
  walk(root);
  assert.deepEqual([...new Set(offenders)], []);
});

test('pages decide access before showing their tools: receipt needs receipt.accept, history needs a history grant (G-038, G-040)', () => {
  const receipt = source('fulfillment/ReceiptWorkspace.tsx');
  assert.match(receipt, /if \(!current\.allowedActions\.includes\('receipt\.accept'\)\) throw new AccessFailure\(403, 'scope_denied'\)/);
  assert.doesNotMatch(receipt, /!current\.allowedActions\.includes\('shipment\.prepare'\)/, 'a preparer without receipt.accept is not a recipient');
  const history = source('administration/AccessHistory.tsx');
  assert.match(history, /canReadAccessHistory\(actor\.allowedActions\)/, 'the page decides before rendering the purpose form');
});

test('the history grant list is the navigation list (one rule)', async () => {
  const { canReadAccessHistory, identityDestinations } = await import('../src/modules/identity/identity-workflow');
  for (const action of ['access.audit.read', 'access.approve', 'membership.approve', 'access.revoke', 'technical.configure', 'organization.approve']) {
    assert.equal(canReadAccessHistory([action]), true, action);
    assert.ok(identityDestinations([action]).some(item => item.href === '/administration/history'), action);
  }
  for (const actions of [[], ['catalog.read', 'order.submit'], ['procurement.read'], ['invoice.issue']]) assert.equal(canReadAccessHistory(actions), false, actions.join());
});

test('a wrong-role visitor on the price page gets the standard denial, never maker/checker wording (G-041)', () => {
  const prices = source('catalog/CatalogPriceWorkspace.tsx');
  assert.doesNotMatch(prices, /if \(error\.status === 403\) return 'Tindakan tidak diizinkan\. Pemeriksa harus/);
  assert.match(prices, /businessRefusal\(error\.code\)\) return 'Tindakan tidak diizinkan\. Pemeriksa harus/);
});

test('every inline 401 text in a protected page follows the lost-session rule (flow C, Option A)', async () => {
  const { readdirSync, statSync } = await import('node:fs');
  const { join } = await import('node:path');
  const { fileURLToPath } = await import('node:url');
  const root = fileURLToPath(new URL('../src/modules', import.meta.url)), offenders: string[] = [];
  const walk = (directory: string) => { for (const name of readdirSync(directory)) { const path = join(directory, name);
    if (statSync(path).isDirectory()) walk(path); else if (/\.tsx?$/.test(name) && readFileSync(path, 'utf8').includes('Masuk dengan akun organisasi Anda untuk melanjutkan')) offenders.push(path.slice(root.length)); } };
  walk(root);
  assert.deepEqual(offenders, []);
});
