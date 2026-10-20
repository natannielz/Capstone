import * as displayFormat from '../src/lib/format';
import * as documentNumbers from '../src/lib/document-numbers';
import {test} from 'node:test';
import assert from 'node:assert/strict';
import * as React from 'react';
import {createRequire} from 'node:module';
import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {createUnitTokoClient} from './helpers/api-client';
import {actorId, sku, definition} from './catalog-fixtures';
import * as dispatch from '../src/modules/fulfillment/dispatch-client';
import * as identity from '../src/modules/identity/identity-workflow';
import * as workflow from '../src/modules/administration/workflow';
import * as operations from '../src/modules/administration/operation-journal';
import * as navigation from '../src/modules/administration/operation-recovery-navigation';
import * as scans from '../src/modules/inventory/scan-client';
import * as lineIdentity from '../src/modules/ordering/order-line-identity';

const source = {orderId: '9007199254741001', suborderId: '9007199254741002', revision: '3', review: 'a'.repeat(64), buyerName: 'Cabang fixture',
  delivery: {recipientName: 'Penerima fixture', phone: '081234567890', address: 'Alamat fixture', city: 'Jakarta', postalCode: '12810', instructions: null},
  lines: [{skuId: sku.id, publicId: sku.publicId, skuCode: sku.code, productName: 'Pena biru', definition, orderedPackages: 2, remainingPackages: 2}], canPrepare: true, orderedAt: definition.effectiveAt};
const prepared = {preparationId: '9007199254741010', preparationRevision: '1', source, lines: [{skuId: sku.id, packages: 1}],
  lots: [{reservationId: '9007199254741011', skuId: sku.id, definitionId: definition.id, lotId: '202', remainingPackages: 2, expiresOn: null}]};
const review = {preparation: prepared, courierId: '9100', courierName: 'Kurir fixture', selected: [{reservationId: prepared.lots[0].reservationId, packages: 1}], review: 'b'.repeat(64)};
const result = {shipmentId: '9007199254741020', revision: '1', orderId: source.orderId, suborderId: source.suborderId, recordedAt: definition.effectiveAt,
  numberEntity: '100', numberChannel: '103', numberYear: '2026', number: '9007199254741021'};
const session = identity.parseIdentitySession({authenticated: true, account: {userId: actorId, displayName: 'Dispatcher fixture', verifiedEmail: 'dispatcher@example.invalid'},
  serverTime: '2026-10-08T02:00:00Z', factorCompletedAt: '2026-10-08T02:00:00Z', freshFactorUntil: '2026-10-08T02:05:00Z',
  idleExpiresAt: '2026-10-08T02:30:00Z', absoluteExpiresAt: '2026-10-08T10:00:00Z', stepUpOperations: []});
const pendingMessage = 'Permintaan keberangkatan sedang diproses.';

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

function mountDispatch() {
  const storage = new MemoryStorage(), writes: Request[] = [], lookups: Request[] = []; let lists = 0;
  let finish: ((response: Response) => void) | undefined, lose: ((failure: Error) => void) | undefined;
  const root = '/api/v1/fulfillment/dispatches';
  const gateway = dispatch.createDispatchGateway(createUnitTokoClient({baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); assert.equal(input.method, 'POST'); assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId);
    assert.equal(input.headers.get('X-CSRF-Token'), 'fixture-request-token');
    const path = new URL(input.url).pathname;
    if (path === `${root}/query`) {lists++; return Response.json({items: [{...prepared, canDispatch: true}], hasMore: false});}
    if (path === `${root}/${source.suborderId}/query`) return Response.json(prepared);
    if (path === `${root}/${source.suborderId}/couriers/query`) return Response.json({items: [{userId: review.courierId, displayName: review.courierName}], hasMore: false});
    if (path === `${root}/${source.suborderId}/preview/query`) return Response.json(review);
    if (path === `${root}/${source.suborderId}`) {
      const reference = new operations.OperationJournal(() => storage).list(actorId);
      assert.equal(reference.length, 1); assert.equal(reference[0].key, input.headers.get('Idempotency-Key'));
      writes.push(input.clone());
      return new Promise<Response>((resolve, reject) => {finish = resolve; lose = reject;});
    }
    if (path === `${root}/operations/query`) {
      lookups.push(input.clone()); assert.equal(input.headers.get('Idempotency-Key'), null);
      return Response.json({status: 'committed', result});
    }
    return assert.fail('Unexpected dispatch request: ' + path);
  }}));
  const events = new EventTarget(), listeners = new Map<string, Set<EventListenerOrEventListenerObject>>(), intervals = new Map<number, () => void>(); let timer = 0;
  // Same-tab storage deliberately emits no storage event: the mounted owner must refresh explicitly.
  const browser = {localStorage: storage,
    addEventListener(kind: string, listener: EventListenerOrEventListenerObject) {if (!listeners.has(kind)) listeners.set(kind, new Set()); listeners.get(kind)!.add(listener); events.addEventListener(kind, listener);},
    removeEventListener(kind: string, listener: EventListenerOrEventListenerObject) {listeners.get(kind)?.delete(listener); events.removeEventListener(kind, listener);},
    setInterval(callback: () => void) {intervals.set(++timer, callback); return timer;}, clearInterval(id: number) {intervals.delete(id);}};
  const access = {current: async () => ({userId: actorId, allowedActions: ['shipment.dispatch', 'shipment.assign']}),
    receipt() {return assert.fail('Dispatch lookup must use its owning gateway');}, resolve() {return assert.fail('Dispatch may not close an in-flight reference');}};
  const frames = new Map<string, Frame>(); let active!: Frame, position = 0, dirty = true, tree: unknown;
  let effects: {frame: Frame; index: number; effect: () => void | (() => void); dependencies?: readonly unknown[]}[] = [];
  // Reuse the maintained receipt/payment hook-effect lifecycle harness and mount actual recovery JSX too.
  const react = {...React,
    useState(initial: unknown) {const frame = active, index = position++; if (!(index in frame.slots)) frame.slots[index] = typeof initial === 'function' ? initial() : initial;
      return [frame.slots[index], (value: unknown) => {if (![...frames.values()].includes(frame)) return;
        const next = typeof value === 'function' ? value(frame.slots[index]) : value; if (!Object.is(next, frame.slots[index])) {frame.slots[index] = next; dirty = true;}}];},
    useRef(initial: unknown) {return active.slots[position++] ??= {current: initial};},
    useCallback(callback: unknown, dependencies?: readonly unknown[]) {const index = position++, previous = active.slots[index] as {value: unknown; dependencies?: readonly unknown[]} | undefined;
      if (!previous || changed(previous.dependencies, dependencies)) active.slots[index] = {value: callback, dependencies}; return (active.slots[index] as {value: unknown}).value;},
    useEffect(effect: () => void | (() => void), dependencies?: readonly unknown[]) {effects.push({frame: active, index: position++, effect, dependencies});}};
  const runtimeRequire = createRequire(resolve('package.json')), ts = runtimeRequire('typescript') as typeof import('typescript'), marker = () => null;
  let sessionStateKit: Record<string, unknown> | undefined;
  const imports = (name: string): unknown => {
    if (name === 'react') return react; if (name === 'react/jsx-runtime') return runtimeRequire(name);
    if (name === 'next/link') return {__esModule: true, default: 'a'};
    if (name.endsWith('.module.css')) return {__esModule: true, default: new Proxy({}, {get: (_target, key) => String(key)})};
    if (name === '../identity/identity-client') return {createIdentityGateway: () => ({session: async () => session})};
    if (name === '../identity/identity-workflow') return identity;
    if (name === '../administration/access-client' || name === './access-client') return {createAccessGateway: () => access};
    if (name === '../administration/workflow' || name === './workflow') return workflow;
    if (name === '../administration/operation-journal' || name === './operation-journal') return operations;
    if (name === './operation-recovery-navigation') return navigation;
    if (name === '../administration/OperationRecovery') return recovery;
    if (name === '../administration/AdministrationNavigation') return {AdministrationNavigation: marker};
    if (name === '../inventory/ScanInput') return {ScanInput: marker};
    if (name === '../inventory/scan-client') return scans;
    if (name === '../ordering/order-line-identity') return lineIdentity;
    if (name === './dispatch-client') return {...dispatch, createDispatchGateway: () => gateway};
    if (name === '@/lib/format') return displayFormat;
    if (name === '@/lib/document-numbers') return documentNumbers;
    if (name === '@/components/workspace-kit/session-access-state') return sessionStateKit ??= load('src/components/workspace-kit/session-access-state.tsx');
    throw new Error('Unexpected mounted dispatch import: ' + name);
  };
  function load(file: string) {
    const code = ts.transpileModule(readFileSync(resolve(file), 'utf8'), {compilerOptions: {module: ts.ModuleKind.CommonJS,
      target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
    const evaluated = {exports: {} as Record<string, unknown>};
    new Function('require', 'module', 'exports', 'window', 'performance', code)(imports, evaluated, evaluated.exports, browser, {now: () => 0});
    return evaluated.exports;
  }
  const recovery = load('src/modules/administration/OperationRecovery.tsx');
  const component = load('src/modules/fulfillment/ShipmentDispatchWorkspace.tsx').ShipmentDispatchWorkspace as () => React.ReactElement;
  function render() {
    const seen = new Set<string>(); effects = []; dirty = false;
    function expand(value: unknown, path: string): unknown {
      if (Array.isArray(value)) return value.map((child, index) => expand(child, path + '/' + index));
      if (!React.isValidElement(value)) return value;
      const props = value.props as Record<string, unknown>;
      if (typeof value.type === 'function') {
        const id = path + ':' + (value.key ?? value.type.name), fn = value.type as Frame['component']; seen.add(id);
        let frame = frames.get(id); if (!frame) {frame = {component: fn, slots: [], effects: new Map()}; frames.set(id, frame);}
        assert.equal(frame.component, fn); active = frame; position = 0; return expand(fn(props), id);
      }
      const node = {type: value.type, props: {...props, children: expand(props.children, path + '/children')}, focus() {}};
      if (props.ref && typeof props.ref === 'object' && 'current' in props.ref) (props.ref as {current: unknown}).current = node;
      return node;
    }
    tree = expand(React.createElement(component), 'root');
    for (const [id, frame] of frames) if (!seen.has(id)) {for (const effect of frame.effects.values()) effect.cleanup?.(); frames.delete(id);}
    for (const item of effects) {const previous = item.frame.effects.get(item.index);
      if (!previous || changed(previous.dependencies, item.dependencies)) {previous?.cleanup?.(); const cleanup = item.effect();
        item.frame.effects.set(item.index, {dependencies: item.dependencies, cleanup: typeof cleanup === 'function' ? cleanup : undefined});}}
  }
  async function settle() {for (let tick = 0; tick < 60; tick++) {if (dirty) render(); await new Promise<void>(resolve => setImmediate(resolve));} assert.equal(dirty, false, 'Mounted effects did not settle');}
  function find(predicate: (node: Node) => boolean) {const found = nodes(tree).find(predicate); assert.ok(found, 'Expected mounted dispatch control'); return found;}
  function button(label: string) {return find(node => node.type === 'button' && text(node.props.children) === label);}
  async function click(label: string) {const control = button(label); assert.notEqual(control.props.disabled, true); (control.props.onClick as () => void)(); await settle();}
  async function fill() {
    await settle(); await click(`Periksa keberangkatan pesanan ${documentNumbers.orderNumber(source.orderId)}`);
    for (const [label, value] of [['Jumlah lot 202', '1'], ['Catatan keberangkatan', 'Barang diperiksa']]) {
      const control = nodes(find(node => node.type === 'label' && text(node.props.children) === label).props.children).find(node => node.type === 'input')!;
      (control.props.onChange as (event: unknown) => void)({target: {value}}); await settle();
    }
    (find(node => node.type === 'select').props.onChange as (event: unknown) => void)({target: {value: review.courierId}}); await settle();
    (find(node => node.type === 'form').props.onSubmit as (event: unknown) => void)({preventDefault() {}}); await settle();
    assert.notEqual(button('Konfirmasi keberangkatan').props.disabled, true);
  }
  return {storage, writes, lookups, fill, settle, click, button, find, get tree() {return tree;}, get lists() {return lists;},
    succeed() {assert.ok(finish); finish(Response.json(result));}, lost() {assert.ok(lose); lose(new TypeError('Lost committed response'));},
    unmount() {for (const frame of frames.values()) for (const effect of frame.effects.values()) effect.cleanup?.(); frames.clear();
      assert.equal(intervals.size, 0); assert.equal([...listeners.values()].reduce((sum, value) => sum + value.size, 0), 0);}};
}

test('mounted dispatch shows the original reference while POST is held, disables reconciliation and never sends twice', async t => {
  for (const outcome of ['success', 'lost'] as const) {
    const harness = mountDispatch(); t.after(() => harness.unmount()); await harness.fill();
    const confirm = harness.button('Konfirmasi keberangkatan').props.onClick as () => void;
    confirm(); confirm(); await harness.settle();
    assert.equal(harness.writes.length, 1);
    const journal = new operations.OperationJournal(() => harness.storage), [original] = journal.list(actorId);
    assert.ok(original); assert.equal(original.operation, 'fulfillment.shipment.dispatch');
    assert.equal(original.key, harness.writes[0].headers.get('Idempotency-Key'));
    assert.match(text(harness.tree), new RegExp(pendingMessage));
    assert.equal(harness.find(node => node.type === 'code').props.children, original.key);
    const check = harness.button('Periksa hasil'); assert.equal(check.props.disabled, true);
    assert.equal(harness.button('Konfirmasi keberangkatan').props.disabled, true);
    assert.equal(harness.button('Muat persiapan terbaru').props.disabled, true);
    assert.equal(nodes(harness.tree).some(node => node.type === 'button' && text(node.props.children) === 'Selesaikan dan tutup'), false);
    (check.props.onClick as () => void)(); confirm(); await harness.settle();
    assert.equal(harness.lookups.length, 0); assert.equal(harness.writes.length, 1);
    assert.doesNotMatch(text(harness.tree), /Hasil belum terkonfirmasi|Permintaan telah tersimpan|Keberangkatan tercatat/);
    assert.deepEqual(Object.keys(original).sort(), ['actorId', 'createdAt', 'key', 'operation']);
    assert.doesNotMatch(JSON.stringify([...harness.storage.values.values()]), /Barang diperiksa|Pena biru|Kurir fixture/);
    if (outcome === 'success') harness.succeed(); else harness.lost();
    await harness.settle(); assert.equal(text(harness.tree).includes(pendingMessage), false);
    if (outcome === 'lost') {
      assert.deepEqual(journal.list(actorId), [original]); assert.notEqual(harness.button('Periksa hasil').props.disabled, true);
      confirm(); await harness.settle(); assert.equal(harness.writes.length, 1);
      await harness.click('Periksa hasil'); assert.equal(harness.lookups.length, 1);
      assert.deepEqual(await harness.lookups[0].json(), {purpose: 'operation-recovery', key: original.key});
    }
    assert.deepEqual(journal.list(actorId), []); assert.match(text(harness.tree), /Keberangkatan tercatat/);
    assert.ok(harness.lists >= 2); assert.equal(harness.writes.length, 1);
  }
});

test('mounted dispatch storage refusal prevents POST and does not claim an in-flight request', async t => {
  const harness = mountDispatch(); t.after(() => harness.unmount()); await harness.fill(); harness.storage.blocked = true;
  await harness.click('Konfirmasi keberangkatan');
  assert.equal(harness.writes.length, 0); assert.equal(harness.lookups.length, 0); assert.equal(harness.storage.length, 0);
  assert.equal(text(harness.tree).includes(pendingMessage), false);
  assert.equal(nodes(harness.tree).some(node => node.type === 'button' && text(node.props.children) === 'Periksa hasil'), false);
  assert.ok(nodes(harness.tree).some(node => node.props.role === 'alert'));
});
