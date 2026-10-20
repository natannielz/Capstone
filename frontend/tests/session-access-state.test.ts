import {test} from 'node:test';
import assert from 'node:assert/strict';
import {AccessFailure} from '../src/modules/administration/workflow';
import {RequestNotSentError} from '../src/api/client';
import {sessionAccessKind, sessionLoginHref} from '../src/components/workspace-kit/session-access-state';

test('a page without a usable session asks to sign in again, a missing role is named as such, anything else offers a retry', () => {
  assert.equal(sessionAccessKind(null), 'signin');
  assert.equal(sessionAccessKind(new AccessFailure(401, 'unauthenticated')), 'signin');
  assert.equal(sessionAccessKind(new AccessFailure(409, 'session_expired')), 'signin');
  assert.equal(sessionAccessKind(new AccessFailure(409, 'actor_changed')), 'signin');
  assert.equal(sessionAccessKind(new AccessFailure(403, 'forbidden')), 'denied');
  assert.equal(sessionAccessKind(new RequestNotSentError(403, 'access_denied')), 'denied');
  assert.equal(sessionAccessKind(new AccessFailure(503, 'service_unavailable')), 'unavailable');
  assert.equal(sessionAccessKind(new TypeError('Failed to fetch')), 'unavailable');
});

test('signing in again returns to the same page', () => {
  assert.equal(sessionLoginHref('/billing/invoices'), '/identity/login?returnPath=%2Fbilling%2Finvoices');
});
