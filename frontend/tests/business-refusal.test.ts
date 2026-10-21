import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { businessRefusal } from '../src/modules/administration/workflow';

test('maker/checker refusals are business answers, not lost access', () => {
  for (const code of ['independent_person_required', 'independent_actor_required', 'independent_reviewer_required'])
    assert.equal(businessRefusal(code), true, code);
  for (const code of ['access_denied', 'scope_denied', 'permission_denied', 'step_up_required']) assert.equal(businessRefusal(code), false, code);
});

test('every inventory editor keeps business refusals inline instead of concealing the page', () => {
  const dir = new URL('../src/modules/inventory/', import.meta.url);
  for (const file of readdirSync(dir).filter(name => /-(editor|execution)-state\.ts$/.test(name))) {
    const source = readFileSync(new URL(file, dir), 'utf8');
    const conceals = source.split(/\r?\n/).filter(line => /status === 403/.test(line) && !/step_up_required'\) return|fresh: false/.test(line));
    for (const line of conceals) assert.match(line, /businessRefusal\(error\.code\)|includes\('independent'\)/, file + ': ' + line.trim());
  }
});

test('the shared message names the maker/checker rule instead of a missing permission', async () => {
  const { accessMessage, AccessFailure } = await import('../src/modules/administration/workflow');
  assert.match(accessMessage(new AccessFailure(403, 'independent_person_required')), /orang lain yang berwenang/);
  assert.match(accessMessage(new AccessFailure(403, 'access_denied')), /Akses untuk tindakan ini tidak tersedia/);
});
