import {test} from 'node:test';
import assert from 'node:assert/strict';
import * as React from 'react';
import {createRequire} from 'node:module';
import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {createUnitTokoClient} from './helpers/api-client';
import * as payments from '../src/modules/billing/payment-client';
import * as identity from '../src/modules/identity/identity-workflow';
import * as workflow from '../src/modules/administration/workflow';
import * as operations from '../src/modules/administration/operation-journal';
import * as navigation from '../src/modules/administration/operation-recovery-navigation';
import * as supplements from '../src/modules/billing/payment-pdf-supplement-client';
import * as supplementRecovery from '../src/modules/billing/payment-pdf-supplement-recovery';
import * as supplementAdapter from '../src/modules/billing/payment-pdf-supplement-recovery-adapter';
import {orderAmount} from '../src/modules/ordering/order-quote-client';

const actor = '17', declarationId = '21', at = '2026-10-08T02:00:00Z';
const draft: payments.PaymentDraft = {id: declarationId, revision: '2', createdAt: at, updatedAt: at, state: 'draft', submission: null,
  input: {invoiceIds: ['31'], declaredAmount: '4000.01', transferDate: '2026-10-07', payerName: 'Private payer fixture', bankReference: 'PRIVATE-BANK-REF', note: 'Private transfer note'},
  invoices: [{invoiceId: '31', number: 'INV-PRIVATE-FIXTURE', total: '5000.00', buyerName: 'Private buyer fixture', sellerName: 'Seller fixture'}]};
const image = payments.parsePaymentEvidenceStatus({reservationId: '51', revision: '3', state: 'quarantined', expiresAt: '2026-10-09T02:00:00Z',
  reference: {id: '52', version: '1', sha256: 'a'.repeat(64)}, scanStatus: 'clean', decoderStatus: 'decoded', safeFailure: null});
const session = identity.parseIdentitySession({authenticated: true, account: {userId: actor, displayName: 'Buyer fixture', verifiedEmail: 'buyer@example.invalid'},
  serverTime: at, factorCompletedAt: at, freshFactorUntil: '2026-10-08T02:05:00Z', idleExpiresAt: '2026-10-08T02:30:00Z',
  absoluteExpiresAt: '2026-10-08T10:00:00Z', stepUpOperations: []});

class MemoryStorage {
  readonly values = new Map<string, string>(); blocked = false;
  get length() {return this.values.size;}
  key(index: number) {return [...this.values.keys()][index] ?? null;}
  getItem(key: string) {return this.values.get(key) ?? null;}
  setItem(key: string, value: string) {if (this.blocked) throw new Error('Storage refused'); this.values.set(key, value);}
  removeItem(key: string) {this.values.delete(key);}
}
type Node = {type: unknown; props: Record<string, unknown>};
const nodes = (value: unknown): Node[] => Array.isArray(value) ? value.flatMap(nodes) : value && typeof value === 'object' && 'props' in value
  ? [value as Node, ...nodes((value as Node).props.children)] : [];
const text = (value: unknown): string => Array.isArray(value) ? value.map(text).join('') : value && typeof value === 'object' && 'props' in value
  ? text((value as Node).props.children) : typeof value === 'string' || typeof value === 'number' ? String(value) : '';
type Effect = {dependencies?: readonly unknown[]; cleanup?: () => void};
type Frame = {component: (props: Record<string, unknown>) => unknown; slots: unknown[]; effects: Map<number, Effect>};
const changed = (before?: readonly unknown[], next?: readonly unknown[]) => !before || !next || before.length !== next.length || next.some((value, index) => !Object.is(value, before[index]));

function mountPayment() {
  const storage = new MemoryStorage(), writes: Request[] = [], lookups: Request[] = [];
  let recoveryUnauthorized = true;
  const root = '/api/v1/billing/payment-declarations';
  const gateway = payments.createPaymentGateway(createUnitTokoClient({baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); const path = new URL(input.url).pathname;
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actor);
    if (path === root + '/query') return Response.json({items: [draft], hasMore: false});
    if (path === `${root}/${declarationId}/query`) return Response.json(draft);
    if (path === `${root}/${declarationId}/replacement-origin/query`) return Response.json({origin: null});
    if (path === `${root}/${declarationId}/evidence/query`) return Response.json({items: [image]});
    if (path === root || path === `${root}/${declarationId}/submit`) {
      writes.push(input.clone()); assert.equal(input.headers.get('If-Match'), '"2"'); assert.ok(input.headers.get('Idempotency-Key'));
      if (path === root) return Response.json({code: 'unauthenticated'}, {status: 401});
      throw new TypeError('Lost submission response');
    }
    if (path === `${root}/${declarationId}/submission/operations/query`) {
      lookups.push(input.clone()); assert.equal(input.headers.get('Idempotency-Key'), null);
      return recoveryUnauthorized ? Response.json({code: 'unauthenticated'}, {status: 401}) : Response.json({status: 'not_confirmed', result: null});
    }
    return assert.fail('Unexpected payment request: ' + path);
  }}));
  const events = new EventTarget(), intervals = new Map<number, () => void>(); let timer = 0;
  const browser = {localStorage: storage, location: new URL('https://unit-toko.test/billing/payments'),
    addEventListener: events.addEventListener.bind(events), removeEventListener: events.removeEventListener.bind(events),
    setInterval(callback: () => void) {intervals.set(++timer, callback); return timer;}, clearInterval(id: number) {intervals.delete(id);}};
  const access = {current: async () => ({userId: actor, allowedActions: ['payment.evidence', 'invoice.read']})};
  const frames = new Map<string, Frame>(); let active!: Frame, position = 0, dirty = true, tree: unknown;
  let pending: {frame: Frame; index: number; effect: () => void | (() => void); dependencies?: readonly unknown[]}[] = [];
  // Reuse the existing EvidenceAttachmentBoundary hook/effect harness, with separate slots for the actual parent, desk and recovery panel.
  const react = {...React,
    useState(initial: unknown) {const frame = active, index = position++; if (!(index in frame.slots)) frame.slots[index] = typeof initial === 'function' ? initial() : initial;
      return [frame.slots[index], (value: unknown) => {if (![...frames.values()].includes(frame)) return;
        const next = typeof value === 'function' ? value(frame.slots[index]) : value; if (!Object.is(next, frame.slots[index])) {frame.slots[index] = next; dirty = true;}}];},
    useRef(initial: unknown) {return active.slots[position++] ??= {current: initial};},
    useCallback(callback: unknown, dependencies?: readonly unknown[]) {const index = position++, previous = active.slots[index] as {value: unknown; dependencies?: readonly unknown[]} | undefined;
      if (!previous || changed(previous.dependencies, dependencies)) active.slots[index] = {value: callback, dependencies}; return (active.slots[index] as {value: unknown}).value;},
    useEffect(effect: () => void | (() => void), dependencies?: readonly unknown[]) {pending.push({frame: active, index: position++, effect, dependencies});}};
  const runtimeRequire = createRequire(resolve('package.json')), ts = runtimeRequire('typescript') as typeof import('typescript');
  const marker = () => null;
  const imports = (name: string): unknown => {
    if (name === 'react') return react;
    if (name === 'react/jsx-runtime') return runtimeRequire(name);
    if (name === 'next/link' || name === 'next/image') return {__esModule: true, default: name === 'next/link' ? 'a' : 'img'};
    if (name.endsWith('.module.css')) return {__esModule: true, default: new Proxy({}, {get: (_target, key) => String(key)})};
    if (name === '../identity/identity-client') return {createIdentityGateway: () => ({session: async () => session})};
    if (name === '../identity/identity-workflow') return identity;
    if (name === '../administration/access-client' || name === './access-client') return {createAccessGateway: () => access};
    if (name === '../administration/workflow' || name === './workflow') return workflow;
    if (name === '../administration/operation-journal' || name === './operation-journal') return operations;
    if (name === './operation-recovery-navigation') return navigation;
    if (name === '../administration/OperationRecovery') return recovery;
    if (name === '../administration/AdministrationNavigation') return {AdministrationNavigation: marker};
    if (name === '../ordering/order-quote-client') return {orderAmount};
    if (name === './invoice-client') return {createInvoiceGateway: () => ({list: async () => ({items: [], hasMore: false})})};
    if (name === './payment-client') return {...payments, createPaymentGateway: () => gateway};
    if (name === './PaymentOcrPanel') return {PaymentOcrPanel: marker};
    if (name === './PaymentSupplementPanels') return {PaymentSupplementRequesterPanel: marker};
    if (name === './payment-pdf-supplement-client') return {...supplements, createPaymentSupplementGateway: () => ({})};
    if (name === './payment-pdf-supplement-recovery') return supplementRecovery;
    if (name === './payment-pdf-supplement-recovery-adapter') return supplementAdapter;
    throw new Error('Unexpected mounted payment import: ' + name);
  };
  function load(file: string) {
    const code = ts.transpileModule(readFileSync(resolve(file), 'utf8'), {compilerOptions: {module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
    const evaluated = {exports: {} as Record<string, unknown>};
    new Function('require', 'module', 'exports', 'window', 'navigator', 'performance', code)(imports, evaluated, evaluated.exports, browser, {onLine: true}, {now: () => 0});
    return evaluated.exports;
  }
  const recovery = load('src/modules/administration/OperationRecovery.tsx');
  const component = load('src/modules/billing/PaymentWorkspace.tsx').PaymentWorkspace as () => React.ReactElement;
  function render() {
    const seen = new Set<string>(); pending = []; dirty = false;
    function expand(value: unknown, path: string): unknown {
      if (Array.isArray(value)) return value.map((child, index) => expand(child, path + '/' + index));
      if (!React.isValidElement(value)) return value;
      const props = value.props as Record<string, unknown>;
      if (typeof value.type === 'function') {
        const id = path + ':' + (value.key ?? value.type.name), fn = value.type as Frame['component']; seen.add(id);
        let frame = frames.get(id); if (!frame) {frame = {component: fn, slots: [], effects: new Map()}; frames.set(id, frame);}
        assert.equal(frame.component, fn); active = frame; position = 0; return expand(fn(props), id);
      }
      return {type: value.type, props: {...props, children: expand(props.children, path + '/children')}};
    }
    tree = expand(React.createElement(component), 'root');
    for (const [id, frame] of frames) if (!seen.has(id)) {for (const effect of frame.effects.values()) effect.cleanup?.(); frames.delete(id);}
    for (const item of pending) {const previous = item.frame.effects.get(item.index);
      if (!previous || changed(previous.dependencies, item.dependencies)) {previous?.cleanup?.(); const cleanup = item.effect();
        item.frame.effects.set(item.index, {dependencies: item.dependencies, cleanup: typeof cleanup === 'function' ? cleanup : undefined});}}
  }
  async function settle() {for (let tick = 0; tick < 60; tick++) {if (dirty) render(); await new Promise<void>(resolve => setImmediate(resolve));} assert.equal(dirty, false, 'Mounted effects did not settle');}
  function find(predicate: (node: Node) => boolean) {const found = nodes(tree).find(predicate); assert.ok(found, 'Expected mounted payment control'); return found;}
  async function click(label: string) {const control = find(node => node.type === 'button' && text(node.props.children) === label);
    assert.notEqual(control.props.disabled, true); (control.props.onClick as () => void)(); await settle();}
  async function open() {await settle(); await click('Buka draf ' + declarationId); assert.ok(nodes(tree).some(node => node.props.value === draft.input.payerName));}
  async function save() {const reason = find(node => node.props.id === 'payment-reason');
    (reason.props.onChange as (event: unknown) => void)({target: {value: 'Private reason fixture'}}); await settle();
    (find(node => node.type === 'form').props.onSubmit as (event: unknown) => void)({preventDefault() {}}); await settle();}
  return {storage, writes, lookups, open, save, click, find, settle, get tree() {return tree;},
    allowRecoveryRead() {recoveryUnauthorized = false;},
    unmount() {for (const frame of frames.values()) for (const effect of frame.effects.values()) effect.cleanup?.(); frames.clear(); assert.equal(intervals.size, 0);}};
}

function assertSignedOut(harness: ReturnType<typeof mountPayment>) {
  const controls = nodes(harness.tree);
  assert.equal(controls.some(node => node.props['aria-label'] === 'Draf pembayaran' || node.type === 'form'), false);
  assert.doesNotMatch(text(harness.tree), /Private payer fixture|INV-PRIVATE-FIXTURE|Private buyer fixture|PRIVATE-BANK-REF/);
  assert.equal(harness.find(node => node.type === 'a' && text(node.props.children) === 'Masuk kembali').props.href,
    '/identity/login?returnPath=%2Fbilling%2Fpayments');
}

test('mounted maintained payment clears private content and offers only the safe sign-in destination after a definitive 401', async t => {
  const harness = mountPayment(); t.after(() => harness.unmount()); await harness.open(); await harness.save();
  assert.equal(harness.writes.length, 1); assertSignedOut(harness);
  assert.deepEqual(new operations.OperationJournal(() => harness.storage).list(actor), []);
});

test('mounted uncertain payment submission keeps its original recovery reference through a denied lookup and renewed session', async t => {
  const harness = mountPayment(); t.after(() => harness.unmount()); await harness.open();
  await harness.click('Pilih bukti untuk diajukan'); await harness.click('Ajukan untuk verifikasi');
  const journal = new operations.OperationJournal(() => harness.storage), [original] = journal.list(actor);
  assert.equal(original.operation, 'billing.payment.submit'); assert.equal(original.key, harness.writes[0].headers.get('Idempotency-Key'));
  assert.deepEqual(Object.keys(original).sort(), ['actorId', 'createdAt', 'key', 'operation']);
  assert.deepEqual(journal.list('99'), []); assert.doesNotMatch([...harness.storage.values.values()].join(''), /Private|4000|BANK-REF/);
  await harness.click('Periksa hasil'); assertSignedOut(harness); assert.deepEqual(journal.list(actor), [original]);
  harness.allowRecoveryRead(); await harness.click('Perbarui sesi dan akses'); await harness.click('Periksa hasil');
  assert.deepEqual(journal.list(actor), [original]); assert.equal(harness.writes.length, 1); assert.equal(harness.lookups.length, 2);
  for (const lookup of harness.lookups) assert.deepEqual(await lookup.json(), {purpose: 'operation-recovery', key: original.key});
  assert.equal(harness.find(node => node.type === 'button' && text(node.props.children) === 'Simpan draf pembayaran').props.disabled, true);
});

test('mounted maintained payment refuses a draft write before sending when recovery storage rejects the reference', async t => {
  const harness = mountPayment(); t.after(() => harness.unmount()); await harness.open(); harness.storage.blocked = true; await harness.save();
  assert.equal(harness.writes.length, 0); assert.equal(harness.storage.length, 0);
  assert.ok(nodes(harness.tree).some(node => node.props.role === 'alert'));
  assert.ok(nodes(harness.tree).some(node => node.props.value === draft.input.payerName));
});
