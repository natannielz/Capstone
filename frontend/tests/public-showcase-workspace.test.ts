import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AccessFailure } from '../src/modules/administration/workflow';
import { parsePublicShowcase } from '../src/modules/catalog/public-showcase-client';
import { showcaseChoice, showcaseMessage } from '../src/modules/catalog/PublicShowcaseWorkspace';
import { identityDestinations } from '../src/modules/identity/identity-workflow';
import { workspaceNavigation } from '../src/modules/identity/workspace-navigation';

const row = parsePublicShowcase({ assortmentId: '7', owner: { kind: 'fulfillmentShop', organizationId: '3', shopId: '5', warehouseId: null, channelId: null },
  listedProducts: '24', isPublic: false, revision: '2', pending: null });
const waiting = { ...row, pending: { proposalId: '901', makePublic: true, ownProposal: false, proposedAt: '2026-10-21T02:00:00+00:00', reason: 'Barang umum' } };

test('makers propose, a different checker decides, and nobody approves their own proposal', () => {
  assert.equal(showcaseChoice(row, ['catalog.public.propose']), 'propose');
  assert.equal(showcaseChoice(row, ['catalog.public.approve']), 'none');
  assert.equal(showcaseChoice(waiting, ['catalog.public.approve']), 'decide');
  assert.equal(showcaseChoice({ ...waiting, pending: { ...waiting.pending, ownProposal: true } }, ['catalog.public.propose', 'catalog.public.approve']), 'own-proposal');
  assert.equal(showcaseChoice(waiting, ['catalog.public.propose']), 'waiting');
});

test('each refusal says what to do next', () => {
  assert.match(showcaseMessage(new AccessFailure(403, 'step_up_required')), /Verifikasi dua langkah/);
  assert.match(showcaseMessage(new AccessFailure(409, 'catalog_public_showcase_changed')), /Muat ulang/);
  assert.match(showcaseMessage(new AccessFailure(403, 'approval_independence_required')), /orang yang berbeda/);
  assert.match(showcaseMessage(new TypeError('fetch failed')), /tidak akan tercatat dua kali/);
});

test('the page is listed under catalog work only for staff who hold either showcase permission', () => {
  const entry = (actions: string[]) => identityDestinations(actions).find(item => item.href === '/catalog/public');
  assert.equal(entry(['catalog.read']), undefined);
  assert.equal(entry(['catalog.public.propose'])?.label, 'Katalog publik');
  assert.equal(entry(['catalog.public.approve'])?.label, 'Katalog publik');
  const group = workspaceNavigation(['catalog.public.approve'], '/catalog/public', { pilot: false }).find(item => item.items.some(link => link.href === '/catalog/public'));
  assert.ok(group); assert.notEqual(group.id, 'current');
});
