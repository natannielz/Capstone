import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readdirSync, readFileSync, statSync} from 'node:fs';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {AccessFailure} from '../src/modules/administration/workflow';
import {IdentityFailure} from '../src/modules/identity/identity-workflow';
import {RequestNotSentError} from '../src/api/client';
import {SessionAccessState, sessionAccessKind, sessionLoginHref} from '../src/components/workspace-kit/session-access-state';

test('someone without a session is asked to sign in; only a session that ended is called expired (G-013)', () => {
  assert.equal(sessionAccessKind(null), 'signin');
  assert.equal(sessionAccessKind(new AccessFailure(401, 'unauthenticated')), 'signin');
  assert.equal(sessionAccessKind(new IdentityFailure(401, 'unauthenticated')), 'signin');
  assert.equal(sessionAccessKind(new IdentityFailure(401, 'session_expired')), 'expired');
  assert.equal(sessionAccessKind(new AccessFailure(409, 'session_expired')), 'expired');
  assert.equal(sessionAccessKind(new AccessFailure(409, 'actor_changed')), 'expired');
  assert.equal(sessionAccessKind(new AccessFailure(401, 'reauthentication_required')), 'expired');
  assert.equal(sessionAccessKind(new AccessFailure(403, 'forbidden')), 'denied');
  assert.equal(sessionAccessKind(new RequestNotSentError(403, 'access_denied')), 'denied');
  assert.equal(sessionAccessKind(new AccessFailure(503, 'service_unavailable')), 'unavailable');
  assert.equal(sessionAccessKind(new TypeError('Failed to fetch')), 'unavailable');
});

test('signing in again returns to the same page', () => {
  assert.equal(sessionLoginHref('/billing/invoices'), '/identity/login?returnPath=%2Fbilling%2Finvoices');
});

const render = (props: Parameters<typeof SessionAccessState>[0]) => renderToStaticMarkup(createElement(SessionAccessState, props));
const heading = (html: string) => /<h2[^>]*>([^<]+)<\/h2>/.exec(html)?.[1];
const links = (html: string) => [...html.matchAll(/<a[^>]*href="([^"]+)"[^>]*>([^<]+)<\/a>/g)].map(match => [match[2], match[1].replace(/&amp;/g, '&')]);

test('each state has exactly one heading, one body and one primary action (G-014)', () => {
  const base = {returnPath: '/inventory/sources', onRetry: () => {}};
  const signin = render({...base, error: new IdentityFailure(401, 'unauthenticated')});
  assert.equal(heading(signin), 'Masuk untuk melanjutkan');
  assert.deepEqual(links(signin), [['Masuk', '/identity/login?returnPath=%2Finventory%2Fsources']]);
  assert.doesNotMatch(signin, /berakhir|diperbarui|kembali/i, 'a first-time visitor is not told a session expired');
  const expired = render({...base, error: new IdentityFailure(401, 'session_expired')});
  assert.equal(heading(expired), 'Sesi Anda perlu diperbarui');
  assert.deepEqual(links(expired), [['Masuk kembali', '/identity/login?returnPath=%2Finventory%2Fsources']]);
  const denied = render({...base, error: new AccessFailure(403, 'access_denied')});
  assert.equal(heading(denied), 'Halaman ini di luar tugas akun Anda');
  const unavailable = render({...base, error: new AccessFailure(503, 'service_unavailable'), detail: 'Peninjauan belum diaktifkan untuk organisasi ini.'});
  assert.equal(heading(unavailable), 'Halaman belum dapat dibuka');
  assert.match(unavailable, /Peninjauan belum diaktifkan untuk organisasi ini\./);
  // A page can name the state it already knows (an editor that saw its session end, or access withdrawn mid-work).
  assert.equal(heading(render({...base, error: null, kind: 'expired'})), 'Sesi Anda perlu diperbarui');
  assert.equal(heading(render({...base, error: null, kind: 'denied'})), 'Halaman ini di luar tugas akun Anda');
});

// Wordings retired by G-014: every page now shows the shared state instead of its own.
const retired = ['Sesi terverifikasi diperlukan', 'Masuk untuk mengelola', 'Masuk untuk menghitung', 'Masuk untuk memeriksa', 'Masuk untuk membuka kemasan',
  'Masuk untuk menyiapkan', 'Masuk untuk mengubah', 'Masuk sebagai penerima', 'Masuk kembali dengan akun', 'Sesi dan akses perlu diperiksa kembali',
  'Sesi perlu diperiksa kembali', 'Periksa sesi dan akses', 'Sesi atau akses belum tersedia', 'Sesi Anda telah berakhir', 'Sesi belum tersedia', 'Sesi telah berakhir'];

test('no page keeps its own wording for signed out, expired or missing access (G-013, G-014)', () => {
  const root = fileURLToPath(new URL('../src', import.meta.url)), offenders: string[] = [];
  const walk = (directory: string) => {
    for (const name of readdirSync(directory)) {
      const path = join(directory, name);
      if (statSync(path).isDirectory()) walk(path);
      else if (/\.(tsx?)$/.test(name)) { const source = readFileSync(path, 'utf8'); for (const text of retired) if (source.includes(text)) offenders.push(path.slice(root.length) + ': ' + text); }
    }
  };
  walk(root);
  assert.deepEqual(offenders, []);
});
