import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { businessRefusal } from '../src/modules/administration/workflow';

test('maker/checker refusals are business answers, not lost access', () => {
  // Every independence code the API raises (grep backend/src for "independent_": the rest is a policy rule name, not a code).
  for (const code of ['independent_person_required', 'independent_actor_required', 'independent_recipient_required'])
    assert.equal(businessRefusal(code), true, code);
  // Changed or missing authority is lost access, not a business answer: it must lock the editor again (round 2).
  for (const code of ['access_denied', 'scope_denied', 'permission_denied', 'step_up_required', 'person_authority_changed', 'person_authority_unavailable',
    'person_evidence_invalid', 'independent_exact_bank_debit', 'independent_reviewer_required']) assert.equal(businessRefusal(code), false, code);
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
  for (const code of ['person_authority_changed', 'person_authority_unavailable', 'person_evidence_invalid'])
    assert.doesNotMatch(accessMessage(new AccessFailure(403, code)), /orang lain/, code);
});

test('no page guesses a maker/checker refusal from a substring; person_authority_changed is not "a different reviewer" (round 3)', () => {
  const offenders: string[] = [];
  const walk = (dir: URL) => { for (const name of readdirSync(dir, { withFileTypes: true })) {
    if (name.isDirectory()) walk(new URL(name.name + '/', dir));
    else if (/\.tsx?$/.test(name.name) && /code\.includes\(['"](person|independen)/.test(readFileSync(new URL(name.name, dir), 'utf8'))) offenders.push(name.name);
  } };
  walk(new URL('../src/modules/', import.meta.url));
  assert.deepEqual(offenders, []);
});
