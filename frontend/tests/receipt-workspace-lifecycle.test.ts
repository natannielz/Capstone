import * as displayFormat from '../src/lib/format';
import {test} from 'node:test';
import assert from 'node:assert/strict';
import * as React from 'react';
import {createRequire} from 'node:module';
import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {createUnitTokoClient} from './helpers/api-client';
import * as receipts from '../src/modules/fulfillment/receipt-client';
import * as identity from '../src/modules/identity/identity-workflow';
import * as workflow from '../src/modules/administration/workflow';
import * as operations from '../src/modules/administration/operation-journal';
import * as navigation from '../src/modules/administration/operation-recovery-navigation';

const actor = '17', token = 'a'.repeat(64), at = '2026-10-08T02:00:00Z';
const review: receipts.ReceiptReview = {challengeId: '21', shipmentId: '22', documentId: '23', documentVersion: '1', documentHash: 'b'.repeat(64),
  recipientName: 'Private recipient fixture', serverTime: at, expiresAt: '2026-10-08T02:15:00Z', consentVersion: 'delivery-receipt-v1',
  consentText: 'Saya telah memeriksa kiriman.', lines: [{skuId: '31', skuCode: 'SKU-PRIVATE', productName: 'Private shipped product', definitionId: '32',
    packageLabel: 'Dus', baseUnitCode: 'piece', baseUnitsPerPackage: 12, packages: 1, lineRevision: '0'}]};
const difference = 'Private difference fixture', reason = 'Private receipt note';
const session = identity.parseIdentitySession({authenticated: true, account: {userId: actor, displayName: 'Recipient fixture', verifiedEmail: 'recipient@example.invalid'},
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

function mountReceipt(confirmMode: 'lost' | '401' = 'lost') {
  const storage = new MemoryStorage(), writes: Request[] = [], lookups: Request[] = [];
  let recoveryMode: '401' | 'malformed' | 'unconfirmed' = '401';
  const root = '/api/v1/fulfillment/receipts';
  const gateway = receipts.createReceiptGateway(createUnitTokoClient({baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); assert.equal(input.method, 'POST'); assert.equal(input.headers.get('X-Expected-Actor-Id'), actor);
    assert.equal(input.headers.get('X-CSRF-Token'), 'fixture-request-token');
    const path = new URL(input.url).pathname; assert.equal(input.url.includes(token), false);
    if (path === `${root}/challenges/${review.challengeId}/query`) {assert.deepEqual(await input.json(), {token}); return Response.json(review);}
    if (path === `${root}/challenges/${review.challengeId}/confirm`) {
      writes.push(input.clone()); assert.ok(input.headers.get('Idempotency-Key'));
      if (confirmMode === '401') return Response.json({code: 'unauthenticated'}, {status: 401});
      throw new TypeError('Lost committed receipt response');
    }
    if (path === `${root}/operations/query`) {
      lookups.push(input.clone()); assert.equal(input.headers.get('Idempotency-Key'), null);
      if (recoveryMode === '401') return Response.json({code: 'unauthenticated'}, {status: 401});
      return Response.json(recoveryMode === 'malformed' ? {status: 'committed', receipt: {id: '41'}} : {status: 'not_confirmed', receipt: null});
    }
    return assert.fail('Unexpected receipt request: ' + path);
  }}));
  const events = new EventTarget(), listeners = new Map<string, Set<EventListenerOrEventListenerObject>>(), intervals = new Map<number, () => void>(); let timer = 0;
  let location = new URL(`https://unit-toko.test/fulfillment/receive?returnPath=https%3A%2F%2Foutside.invalid&token=${token}#${review.challengeId}.${token}`);
  const browser = {localStorage: storage, get location() {return location;}, history: {replaceState(_state: unknown, _unused: string, url: string) {location = new URL(url, location);}},
    addEventListener(kind: string, listener: EventListenerOrEventListenerObject) {if (!listeners.has(kind)) listeners.set(kind, new Set()); listeners.get(kind)!.add(listener); events.addEventListener(kind, listener);},
    removeEventListener(kind: string, listener: EventListenerOrEventListenerObject) {listeners.get(kind)?.delete(listener); events.removeEventListener(kind, listener);},
    setInterval(callback: () => void) {intervals.set(++timer, callback); return timer;}, clearInterval(id: number) {intervals.delete(id);}};
  const access = {current: async () => ({userId: actor, allowedActions: ['receipt.accept']}), receipt() {return assert.fail('Receipt recovery must use its owning gateway');}};
  const frames = new Map<string, Frame>(); let active!: Frame, position = 0, dirty = true, tree: unknown, focused: Node | null = null;
  let pending: {frame: Frame; index: number; effect: () => void | (() => void); dependencies?: readonly unknown[]}[] = [];
  const refs = new Set<{current: unknown}>();
  // Reuse the maintained payment hook/effect harness; host refs receive focusable nodes before effects commit.
  const react = {...React,
    useState(initial: unknown) {const frame = active, index = position++; if (!(index in frame.slots)) frame.slots[index] = typeof initial === 'function' ? initial() : initial;
      return [frame.slots[index], (value: unknown) => {if (![...frames.values()].includes(frame)) return;
        const next = typeof value === 'function' ? value(frame.slots[index]) : value; if (!Object.is(next, frame.slots[index])) {frame.slots[index] = next; dirty = true;}}];},
    useRef(initial: unknown) {return active.slots[position++] ??= {current: initial};},
    useCallback(callback: unknown, dependencies?: readonly unknown[]) {const index = position++, previous = active.slots[index] as {value: unknown; dependencies?: readonly unknown[]} | undefined;
      if (!previous || changed(previous.dependencies, dependencies)) active.slots[index] = {value: callback, dependencies}; return (active.slots[index] as {value: unknown}).value;},
    useEffect(effect: () => void | (() => void), dependencies?: readonly unknown[]) {pending.push({frame: active, index: position++, effect, dependencies});}};
  const runtimeRequire = createRequire(resolve('package.json')), ts = runtimeRequire('typescript') as typeof import('typescript'), marker = () => null;
  const imports = (name: string): unknown => {
    if (name === 'react') return react;
    if (name === 'react/jsx-runtime') return runtimeRequire(name);
    if (name === 'next/link') return {__esModule: true, default: 'a'};
    if (name.endsWith('.module.css')) return {__esModule: true, default: new Proxy({}, {get: (_target, key) => String(key)})};
    if (name === '../identity/identity-client') return {createIdentityGateway: () => ({session: async () => session})};
    if (name === '../identity/identity-workflow') return identity;
    if (name === '../administration/access-client' || name === './access-client') return {createAccessGateway: () => access};
    if (name === '../administration/workflow' || name === './workflow') return workflow;
    if (name === '../administration/operation-journal' || name === './operation-journal') return operations;
    if (name === './operation-recovery-navigation') return navigation;
    if (name === '../administration/OperationRecovery') return recovery;
    if (name === './receipt-client') return {...receipts, createReceiptGateway: () => gateway};
    if (name === './ShipmentReceiptPanel') return {ReceiptSummary: marker};
    if (name === './ReceiptProofPanel') return {ReceiptProofPanel: marker};
    if (name === './receipt-proof-client') return {createReceiptProofGateway: () => ({})};
    if (name === '@/lib/format') return displayFormat;
    throw new Error('Unexpected mounted receipt import: ' + name);
  };
  function load(file: string) {
    const code = ts.transpileModule(readFileSync(resolve(file), 'utf8'), {compilerOptions: {module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
    const evaluated = {exports: {} as Record<string, unknown>};
    new Function('require', 'module', 'exports', 'window', 'navigator', 'performance', code)(imports, evaluated, evaluated.exports, browser, {onLine: true}, {now: () => 0});
    return evaluated.exports;
  }
  const recovery = load('src/modules/administration/OperationRecovery.tsx');
  const component = load('src/modules/fulfillment/ReceiptWorkspace.tsx').ReceiptWorkspace as () => React.ReactElement;
  function render() {
    const seen = new Set<string>(); pending = []; dirty = false; for (const ref of refs) ref.current = null; refs.clear();
    function expand(value: unknown, path: string): unknown {
      if (Array.isArray(value)) return value.map((child, index) => expand(child, path + '/' + index));
      if (!React.isValidElement(value)) return value;
      const props = value.props as Record<string, unknown>;
      if (typeof value.type === 'function') {
        const id = path + ':' + (value.key ?? value.type.name), fn = value.type as Frame['component']; seen.add(id);
        let frame = frames.get(id); if (!frame) {frame = {component: fn, slots: [], effects: new Map()}; frames.set(id, frame);}
        assert.equal(frame.component, fn); active = frame; position = 0; return expand(fn(props), id);
      }
      const node = {type: value.type, props: {...props, children: expand(props.children, path + '/children')}, focus() {focused = node;}};
      if (props.ref && typeof props.ref === 'object' && 'current' in props.ref) {const ref = props.ref as {current: unknown}; refs.add(ref); ref.current = node;}
      return node;
    }
    tree = expand(React.createElement(component), 'root');
    for (const [id, frame] of frames) if (!seen.has(id)) {for (const effect of frame.effects.values()) effect.cleanup?.(); frames.delete(id);}
    for (const item of pending) {const previous = item.frame.effects.get(item.index);
      if (!previous || changed(previous.dependencies, item.dependencies)) {previous?.cleanup?.(); const cleanup = item.effect();
        item.frame.effects.set(item.index, {dependencies: item.dependencies, cleanup: typeof cleanup === 'function' ? cleanup : undefined});}}
  }
  async function settle() {for (let tick = 0; tick < 60; tick++) {if (dirty) render(); await new Promise<void>(resolve => setImmediate(resolve));} assert.equal(dirty, false, 'Mounted effects did not settle');}
  function find(predicate: (node: Node) => boolean) {const found = nodes(tree).find(predicate); assert.ok(found, 'Expected mounted receipt control'); return found;}
  function button(label: string) {return find(node => node.type === 'button' && text(node.props.children) === label);}
  function field(label: string) {return nodes(find(node => node.type === 'label' && text(node.props.children) === label).props.children).find(node => node.type === 'input' || node.type === 'textarea')!;}
  async function edit(label: string, value: string) {const control = field(label); assert.ok(control); (control.props.onChange as (event: unknown) => void)({target: {value}}); await settle();}
  async function click(label: string) {const control = button(label); assert.notEqual(control.props.disabled, true); (control.props.onClick as () => void)(); await settle();}
  async function consent() {(find(node => node.type === 'input' && node.props.type === 'checkbox').props.onChange as (event: unknown) => void)({target: {checked: true}}); await settle();}
  async function preview() {(find(node => node.type === 'form').props.onSubmit as (event: unknown) => void)({preventDefault() {}}); await settle();}
  async function open() {await settle(); assert.equal(browser.location.pathname + browser.location.search + browser.location.hash, '/fulfillment/receive'); await click('Periksa kiriman');}
  async function fill() {for (const [label, packages, loose] of [['Diterima', '0', '6'], ['Ditolak', '0', '4'], ['Belum diterima', '0', '2']]) {
    await edit(label + ' — kemasan utuh', packages); await edit(label + ' — satuan lepas', loose);
  } await edit('Alasan perbedaan', difference); await edit('Catatan penerimaan', reason);}
  return {storage, writes, lookups, open, fill, edit, preview, consent, click, button, field, find, settle, get tree() {return tree;}, get focused() {return focused;},
    recoveryReply(mode: typeof recoveryMode) {recoveryMode = mode;},
    unmount() {for (const frame of frames.values()) for (const effect of frame.effects.values()) effect.cleanup?.(); frames.clear();
      for (const ref of refs) ref.current = null; refs.clear(); assert.equal(intervals.size, 0); assert.equal([...listeners.values()].reduce((sum, value) => sum + value.size, 0), 0);}};
}

function assertSignedOut(harness: ReturnType<typeof mountReceipt>) {
  assert.equal(nodes(harness.tree).some(node => node.props['aria-label'] === 'Konfirmasi penerimaan' || node.type === 'form'), false);
  assert.doesNotMatch(text(harness.tree), /Private|SKU-PRIVATE/);
  const href = harness.find(node => node.type === 'a' && text(node.props.children) === 'Masuk sebagai penerima').props.href;
  assert.equal(href, '/identity/login?returnPath=%2Ffulfillment%2Freceive'); assert.equal(String(href).includes(token), false);
}

test('mounted recipient review returns focus to quantities, preserves edits and requires renewed consent without sending before confirmation', async t => {
  const harness = mountReceipt(); t.after(() => harness.unmount()); await harness.open(); await harness.fill(); await harness.preview();
  assert.equal(harness.focused, harness.find(node => node.type === 'h2' && text(node.props.children) === 'Tinjau jumlah penerimaan'));
  assert.match(text(harness.tree), /Diterima: 0 Dus \+ 6 piece/); assert.match(text(harness.tree), /Ditolak: 0 Dus \+ 4 piece/);
  assert.match(text(harness.tree), /Belum diterima: 0 Dus \+ 2 piece/); assert.match(text(harness.tree), new RegExp(difference));
  assert.match(text(harness.tree), new RegExp('Catatan: ' + reason)); assert.equal(harness.button('Konfirmasi penerimaan').props.disabled, true);
  await harness.consent(); await harness.click('Kembali ke jumlah');
  assert.equal(harness.focused, harness.field('Diterima — kemasan utuh'));
  assert.equal(harness.field('Diterima — satuan lepas').props.value, '6'); assert.equal(harness.field('Alasan perbedaan').props.value, difference);
  assert.equal(harness.field('Catatan penerimaan').props.value, reason);
  await harness.edit('Diterima — satuan lepas', '5'); await harness.edit('Belum diterima — satuan lepas', '3');
  await harness.edit('Alasan perbedaan', 'Private revised difference'); await harness.preview();
  assert.match(text(harness.tree), /Diterima: 0 Dus \+ 5 piece/); assert.match(text(harness.tree), /Belum diterima: 0 Dus \+ 3 piece/);
  assert.match(text(harness.tree), /Private revised difference/); assert.equal(harness.button('Konfirmasi penerimaan').props.disabled, true);
  assert.equal(harness.find(node => node.type === 'input' && node.props.type === 'checkbox').props.checked, false);
  assert.equal(harness.writes.length, 0);
  await harness.consent(); harness.storage.blocked = true; await harness.click('Konfirmasi penerimaan');
  assert.equal(harness.writes.length, 0); assert.equal(harness.storage.length, 0); assert.ok(nodes(harness.tree).some(node => node.props.role === 'alert'));
});

test('mounted recipient clears private content on confirmation or recovery 401 and keeps an uncertain original reference without another send', async t => {
  for (const mode of ['401', 'lost'] as const) {
    const harness = mountReceipt(mode); t.after(() => harness.unmount()); await harness.open(); await harness.fill(); await harness.preview(); await harness.consent();
    const staleConfirm = harness.button('Konfirmasi penerimaan').props.onClick as () => void;
    await harness.click('Konfirmasi penerimaan'); assert.equal(harness.writes.length, 1);
    assert.deepEqual(await harness.writes[0].json(), {token, consent: true, consentVersion: review.consentVersion, reason, lines: [{skuId: '31',
      accepted: {packages: 0, looseUnits: 6}, rejected: {packages: 0, looseUnits: 4}, missing: {packages: 0, looseUnits: 2}, exceptionReason: difference, lineRevision: '0'}]});
    const journal = new operations.OperationJournal(() => harness.storage);
    if (mode === '401') {assertSignedOut(harness); assert.deepEqual(journal.list(actor), []); continue;}
    const [original] = journal.list(actor), stored = [...harness.storage.values.entries()];
    assert.equal(original.operation, 'fulfillment.receipt.confirm'); assert.equal(original.key, harness.writes[0].headers.get('Idempotency-Key'));
    assert.deepEqual(Object.keys(original).sort(), ['actorId', 'createdAt', 'key', 'operation']); assert.deepEqual(journal.list('99'), []);
    assert.doesNotMatch(stored.map(([, value]) => value).join(''), /Private|SKU-PRIVATE/); assert.equal(JSON.stringify(stored).includes(token), false);
    assert.equal(harness.button('Konfirmasi penerimaan').props.disabled, true);
    await harness.click('Periksa hasil'); assertSignedOut(harness); assert.deepEqual(journal.list(actor), [original]);
    staleConfirm(); await harness.settle(); assert.equal(harness.writes.length, 1);
    harness.recoveryReply('malformed'); await harness.click('Perbarui sesi dan akses'); await harness.click('Periksa hasil');
    assert.deepEqual(journal.list(actor), [original]); assert.equal(harness.writes.length, 1);
    assert.equal(nodes(harness.tree).some(node => node.type === 'form' || node.type === 'button' && text(node.props.children) === 'Periksa kiriman'), false);
    harness.recoveryReply('unconfirmed'); await harness.click('Periksa hasil');
    assert.deepEqual(journal.list(actor), [original]); assert.deepEqual([...harness.storage.values.entries()], stored);
    assert.equal(harness.writes.length, 1); assert.equal(harness.lookups.length, 3);
    for (const lookup of harness.lookups) assert.deepEqual(await lookup.json(), {purpose: 'operation-recovery', key: original.key});
    assert.doesNotMatch(text(harness.tree), /Private|SKU-PRIVATE/);
  }
});
