import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { AccessFailure } from '../src/modules/administration/workflow';
import { parsePublicSamplePage } from '../src/modules/catalog/public-sample-client';
import { ProposalReview, SampleRow, approveBlock, mixedOwners, proposalFrom, publicSampleTitle, sampleMessage, stageToggle, wouldExceed }
  from '../src/modules/catalog/PublicSampleWorkspace';
import { identityDestinations } from '../src/modules/identity/identity-workflow';
import { workspaceNavigation } from '../src/modules/identity/workspace-navigation';

const page = parsePublicSamplePage({ total: '3', sampleCount: '1', sampleLimit: '200', items: [
  { familyId: '7', familyName: 'Pensil 2B', ownerLabel: 'Toko Melati', packages: '2', isSample: false, pending: null },
  { familyId: '8', familyName: 'Kopi bubuk', ownerLabel: 'Toko Melati', packages: '1', isSample: true, pending: null },
  { familyId: '9', familyName: 'Map plastik', ownerLabel: 'Toko Kenanga', packages: '1', isSample: false, pending: null }],
  proposals: [{ proposalId: '901', ownProposal: true, ownerLabel: 'Toko Melati', reason: 'Contoh barang umum', proposedAt: '2026-10-21T02:00:00+00:00',
    adds: [{ familyId: '7', familyName: 'Pensil 2B' }], removes: [{ familyId: '8', familyName: 'Kopi bubuk' }] }] });
const [pencil, coffee, folder] = page.items;

test('toggles are collected into one proposal of additions and removals; toggling back cancels the change', () => {
  let staged = stageToggle(new Map(), pencil, true);
  staged = stageToggle(staged, coffee, false);
  assert.deepEqual(proposalFrom(staged), { adds: ['7'], removes: ['8'] });
  staged = stageToggle(staged, pencil, false);
  assert.deepEqual(proposalFrom(staged), { adds: [], removes: ['8'] });
  assert.equal(mixedOwners([pencil, coffee]), false); assert.equal(mixedOwners([pencil, folder]), true);
  assert.equal(wouldExceed(200, stageToggle(new Map(), pencil, true), 200), true);
  assert.equal(wouldExceed(200, stageToggle(stageToggle(new Map(), pencil, true), coffee, false), 200), false);
});

test('the proposer cannot approve: Setujui is disabled and says why', () => {
  const own = page.proposals[0];
  assert.equal(approveBlock(own), 'Anda yang mengajukan perubahan ini');
  assert.equal(approveBlock({ ...own, ownProposal: false }), null);
  const html = renderToStaticMarkup(createElement(ProposalReview, { proposal: own, canDecide: true, busy: false, onDecide: () => undefined }));
  assert.match(html, /<button[^>]*disabled=""[^>]*>Setujui<\/button>/);
  assert.match(html, /Anda yang mengajukan perubahan ini/);
  // The reviewer sees old against new for every product in the proposal.
  assert.match(html, /Pensil 2B[\s\S]*Belum tampil[\s\S]*Tampil untuk tamu/);
  assert.match(html, /Kopi bubuk[\s\S]*Tampil untuk tamu[\s\S]*Tidak tampil lagi/);
  const other = renderToStaticMarkup(createElement(ProposalReview, { proposal: { ...own, ownProposal: false }, canDecide: true, busy: false, onDecide: () => undefined }));
  assert.doesNotMatch(other, /disabled=""[^>]*>Setujui/);
});

test('each product row has a "Tampilkan untuk tamu" switch that shows the staged state', () => {
  const html = renderToStaticMarkup(createElement(SampleRow, { item: pencil, staged: true, canPropose: true, onToggle: () => undefined }));
  assert.match(html, /Tampilkan untuk tamu/); assert.match(html, /role="switch"/); assert.match(html, /aria-checked="true"/);
  assert.match(html, /2 kemasan/);
  const locked = renderToStaticMarkup(createElement(SampleRow, { item: { ...pencil, pending: { proposalId: '901', adding: true, ownProposal: false } }, staged: undefined, canPropose: true, onToggle: () => undefined }));
  assert.match(locked, /disabled=""/); assert.match(locked, /Menunggu tinjauan/);
  assert.equal(publicSampleTitle, 'Contoh katalog untuk tamu');
});

test('each refusal says what to do next', () => {
  assert.match(sampleMessage(new AccessFailure(403, 'step_up_required')), /Verifikasi dua langkah/);
  assert.match(sampleMessage(new AccessFailure(403, 'self_approval')), /orang yang berbeda/);
  assert.match(sampleMessage(new AccessFailure(409, 'public_sample_limit')), /200/);
  assert.match(sampleMessage(new AccessFailure(409, 'catalog_public_sample_pending')), /menunggu tinjauan/);
  assert.match(sampleMessage(new AccessFailure(409, 'catalog_public_sample_changed')), /Muat ulang/);
  assert.match(sampleMessage(new AccessFailure(400, 'catalog_public_sample_owner_mixed')), /satu pemilik/);
  assert.match(sampleMessage(new TypeError('fetch failed')), /tidak akan tercatat dua kali/);
});

test('the page is listed under catalog work only for staff who hold either sample permission', () => {
  const entry = (actions: string[]) => identityDestinations(actions).find(item => item.href === '/catalog/public');
  assert.equal(entry(['catalog.read']), undefined);
  assert.ok(entry(['catalog.public.propose'])); assert.ok(entry(['catalog.public.approve']));
  const group = workspaceNavigation(['catalog.public.approve'], '/catalog/public', { pilot: false }).find(item => item.items.some(link => link.href === '/catalog/public'));
  assert.ok(group); assert.notEqual(group.id, 'current');
});
