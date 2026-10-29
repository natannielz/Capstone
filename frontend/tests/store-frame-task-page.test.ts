import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

// Render the real StoreFrame and AdministrationNavigation; only Next's router hooks are stubbed (no app router outside Next).
const outfile = fileURLToPath(new URL('./store-frame-under-test.mjs', import.meta.url));
await build({ stdin: { contents: "export { StoreFrame } from './src/components/store/store-frame'; export { AdministrationNavigation } from './src/modules/administration/AdministrationNavigation';",
  resolveDir: fileURLToPath(new URL('..', import.meta.url)), loader: 'ts' }, outfile, bundle: true, platform: 'node', format: 'esm', target: 'node24', packages: 'external',
  alias: { 'next/navigation': fileURLToPath(new URL('../tests/helpers/next-navigation-stub.ts', import.meta.url)), 'next/link': 'next/link.js' },
  loader: { '.css': 'empty' }, jsx: 'automatic', logLevel: 'error' });
const { StoreFrame, AdministrationNavigation } = await import(new URL('./store-frame-under-test.mjs', import.meta.url).href) as {
  StoreFrame: typeof import('../src/components/store/store-frame').StoreFrame; AdministrationNavigation: typeof import('../src/modules/administration/AdministrationNavigation').AdministrationNavigation };

// A buyer task page (e.g. /billing/invoices for pemohon) renders its AdministrationNavigation; inside the storefront frame the
// workspace sidebar, topbar, palette and its skip target must not appear (G-037).
const pemohon = ['catalog.read', 'order.read', 'order.submit', 'invoice.read', 'payment.evidence', 'receipt.accept', 'case.read', 'shipment.read'];

test('a task page inside the storefront frame shows no workspace navigation and one skip target (G-037)', () => {
  const html = renderToStaticMarkup(createElement(StoreFrame, null, createElement('main', null, createElement(AdministrationNavigation, { current: 'invoices', actions: pemohon }), createElement('h1', null, 'Tagihan'))));
  assert.doesNotMatch(html, /workspace-sidebar/);
  assert.doesNotMatch(html, /Navigasi ruang kerja/);
  assert.doesNotMatch(html, /Buka menu ruang kerja/);
  assert.equal([...html.matchAll(/id="konten-utama"/g)].length, 1);
  assert.match(html, /<h1>Tagihan<\/h1>/);
});

test('outside any frame the workspace navigation still renders (pages keep working on their own)', () => {
  const html = renderToStaticMarkup(createElement(AdministrationNavigation, { current: 'invoices', actions: pemohon }));
  assert.match(html, /workspace-sidebar/);
});
