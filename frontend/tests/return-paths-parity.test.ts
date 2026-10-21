import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { destinationHrefs, identityReturnPaths } from '../src/modules/identity/identity-workflow';

// The API (Auth0Options.AllowedReturnPaths) and this frontend (identity-workflow.ts) are built and deployed separately, so the
// API list cannot import the frontend list. This test reads the C# source and fails whenever the two lists differ (G-007).
function csharpArray(file: string, member: string): string[] {
  const source = readFileSync(new URL('../../backend/src/UnitToko.Api/' + file, import.meta.url), 'utf8');
  const start = source.indexOf(member);
  assert.ok(start >= 0, member + ' not found in ' + file);
  const open = source.indexOf('[', source.indexOf('=', start)), close = source.indexOf(']', open);
  return [...source.slice(open + 1, close).matchAll(/"([^"]*)"/g)].map(match => match[1]);
}
const sorted = (values: readonly string[]) => [...values].sort();

test('the API and the frontend allow exactly the same sign-in return paths (G-007)', () => {
  const api = csharpArray('Identity/Auth0Options.cs', 'AllowedReturnPaths');
  assert.equal(new Set(api).size, api.length, 'the API list has duplicates');
  assert.deepEqual(sorted(api), sorted(identityReturnPaths()));
});

test('every navigation destination is a valid return path (F-011)', () => {
  const allowed = new Set(identityReturnPaths());
  assert.deepEqual(destinationHrefs().filter(href => !allowed.has(href)), []);
  for (const href of ['/inventory/replenishment', '/administration/accounts']) assert.ok(allowed.has(href), href);
});

test('the legacy catalog route is a redirect only, never a return path (G-023)', () => {
  assert.equal(identityReturnPaths().includes('/catalog/browse'), false);
  assert.equal(csharpArray('ReleaseProfile.cs', 'PilotReturnPaths').includes('/catalog/browse'), false);
});

test('the first release only narrows the reviewed list; it never adds a path the API list lacks', () => {
  const api = new Set(csharpArray('Identity/Auth0Options.cs', 'AllowedReturnPaths'));
  assert.deepEqual(csharpArray('ReleaseProfile.cs', 'PilotReturnPaths').filter(path => !api.has(path)), []);
});
