import * as displayFormat from '../src/lib/format';
import {test} from 'node:test';
import assert from 'node:assert/strict';
import * as React from 'react';
import {createRequire} from 'node:module';
import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {createUnitTokoClient} from './helpers/api-client';
import {actorId, family, sku, definition} from './catalog-fixtures';
import * as catalog from '../src/modules/catalog/catalog-buyer-client';
import * as editor from '../src/modules/catalog/catalog-editor-state';
import * as history from '../src/modules/catalog/catalog-history';
import * as identity from '../src/modules/identity/identity-workflow';
import * as workflow from '../src/modules/administration/workflow';

type Node = {type: unknown; props: Record<string, unknown>};
const nodes = (value: unknown): Node[] => Array.isArray(value) ? value.flatMap(nodes) : value && typeof value === 'object' && 'props' in value
  ? [value as Node, ...nodes((value as Node).props.children)] : [];
const text = (value: unknown): string => Array.isArray(value) ? value.map(text).join('') : value && typeof value === 'object' && 'props' in value
  ? text((value as Node).props.children) : typeof value === 'string' || typeof value === 'number' ? String(value) : '';
const session = identity.parseIdentitySession({authenticated: true, account: {userId: actorId, displayName: 'Catalog buyer fixture', verifiedEmail: 'buyer@example.invalid'},
  serverTime: '2026-10-08T02:00:00Z', factorCompletedAt: '2026-10-08T02:00:00Z', freshFactorUntil: '2026-10-08T02:05:00Z',
  idleExpiresAt: '2026-10-08T02:30:00Z', absoluteExpiresAt: '2026-10-08T10:00:00Z', stepUpOperations: []});

function browser(urls: string[]) {
  const entries = urls.map(url => ({url, state: {} as Record<string, unknown>})), events = new EventTarget(); let cursor = 0, timer = 0;
  const listeners = new Map<string, Set<EventListenerOrEventListenerObject>>(), intervals = new Map<number, () => void>();
  function go(delta: number) {const next = cursor + delta; if (next < 0 || next >= entries.length) return; cursor = next; events.dispatchEvent(new Event('popstate'));}
  const window = {get location() {return new URL(entries[cursor].url, 'https://unit-toko.test');},
    history: {get state() {return entries[cursor].state;},
      replaceState(state: Record<string, unknown>, _unused: string, url: string) {entries[cursor] = {state, url};},
      pushState(state: Record<string, unknown>, _unused: string, url: string) {entries.splice(cursor + 1); entries.push({state, url}); cursor = entries.length - 1;},
      back: () => go(-1), forward: () => go(1)},
    addEventListener(kind: string, listener: EventListenerOrEventListenerObject) {if (!listeners.has(kind)) listeners.set(kind, new Set()); listeners.get(kind)!.add(listener); events.addEventListener(kind, listener);},
    removeEventListener(kind: string, listener: EventListenerOrEventListenerObject) {listeners.get(kind)?.delete(listener); events.removeEventListener(kind, listener);},
    setInterval(callback: () => void) {intervals.set(++timer, callback); return timer;}, clearInterval(id: number) {intervals.delete(id);}};
  return {window, get href() {return window.location.pathname + window.location.search + window.location.hash;},
    snapshot: () => structuredClone(entries), listenerCount: (kind: string) => listeners.get(kind)?.size ?? 0, timerCount: () => intervals.size};
}
type Query = {search: string; offset: number; signal: AbortSignal; finish?: (response: Response) => void};
type Effect = {dependencies?: readonly unknown[]; cleanup?: () => void};
const changed = (before?: readonly unknown[], next?: readonly unknown[]) => !before || !next || before.length !== next.length || next.some((value, index) => !Object.is(value, before[index]));

function mountCatalog(context: ReturnType<typeof browser>, returnPath: '/shop' | '/catalog/browse', deferFirst = false) {
  const queries: Query[] = [];
  const reply = (query: Query, label = `${query.search || 'Catalog fixture'} / ${query.offset}`) => Response.json({total: '80', items: [{familyId: family.id,
    familyPublicId: family.publicId, familyName: label, description: family.description, categoryCode: family.categoryCode,
    skuId: sku.id, publicId: sku.publicId, code: sku.code, definition, currentPrice: null,
    buyerIdentity: {skuId: sku.id, skuRevision: sku.version, definitionId: definition.id, definitionVersion: definition.version,
      currentPrimary: {identifierId: '81', identifierRevision: '1', code: sku.code}}}]});
  const gateway = catalog.createBuyerCatalogGateway(createUnitTokoClient({baseUrl: 'https://unit-toko.test', fetch: async input => {
    assert.ok(input instanceof Request); assert.equal(new URL(input.url).pathname, '/api/v1/catalog/variants/query');
    assert.equal(input.headers.get('X-Expected-Actor-Id'), actorId); assert.equal(input.credentials, 'same-origin');
    const body = await input.json() as {search: string; offset: number; limit: number};
    assert.deepEqual(body, {search: body.search, offset: body.offset, limit: 25});
    const query: Query = {search: body.search, offset: body.offset, signal: input.signal}; queries.push(query);
    // Deliberately finish the old transport after cancellation to exercise the component's late-result fence too.
    if (deferFirst && queries.length === 1) return new Promise<Response>(resolve => {query.finish = resolve;});
    return reply(query);
  }}));
  const hooks: unknown[] = [], effects = new Map<number, Effect>(); let position = 0, dirty = true, mounted = true, tree: unknown;
  let pending: {index: number; effect: () => void | (() => void); dependencies?: readonly unknown[]}[] = [];
  // Same hook/effect lifecycle pattern as the maintained payment and attachment-boundary tests; render the actual BuyerCatalog.
  const react = {...React,
    useState(initial: unknown) {const index = position++; if (!(index in hooks)) hooks[index] = typeof initial === 'function' ? initial() : initial;
      return [hooks[index], (value: unknown) => {if (!mounted) return; const next = typeof value === 'function' ? value(hooks[index]) : value;
        if (!Object.is(next, hooks[index])) {hooks[index] = next; dirty = true;}}];},
    useRef(initial: unknown) {return hooks[position++] ??= {current: initial};},
    useCallback(callback: unknown, dependencies?: readonly unknown[]) {const index = position++, previous = hooks[index] as {value: unknown; dependencies?: readonly unknown[]} | undefined;
      if (!previous || changed(previous.dependencies, dependencies)) hooks[index] = {value: callback, dependencies}; return (hooks[index] as {value: unknown}).value;},
    useEffect(effect: () => void | (() => void), dependencies?: readonly unknown[]) {pending.push({index: position++, effect, dependencies});}};
  const runtimeRequire = createRequire(resolve('package.json')), ts = runtimeRequire('typescript') as typeof import('typescript'), marker = () => null;
  const imports = (name: string): unknown => {
    if (name === 'react') return react; if (name === 'react/jsx-runtime') return runtimeRequire(name);
    if (name === 'next/link') return {__esModule: true, default: 'a'};
    if (name.endsWith('.module.css')) return {__esModule: true, default: new Proxy({}, {get: (_target, key) => String(key)})};
    if (name === 'lucide-react') return {ArrowRight: marker, ArrowUpRight: marker, ClipboardList: marker, PackageOpen: marker, Search: marker, ShieldCheck: marker};
    if (name === '../../components/art') return {Art: marker};
    if (name === '../../components/pilot-site-chrome') return {PilotSiteHeader: marker, PilotSiteFooter: marker};
    if (name === '../identity/identity-client') return {createIdentityGateway: () => ({session: async () => session})};
    if (name === '../identity/identity-workflow') return identity;
    if (name === '../administration/access-client') return {createAccessGateway: () => ({current: async () => ({userId: actorId, allowedActions: ['catalog.read']})})};
    if (name === '../administration/workflow') return workflow;
    if (name === './catalog-editor-state') return editor;
    if (name === './catalog-buyer-client') return {...catalog, createBuyerCatalogGateway: () => gateway};
    if (name === './catalog-history') return history;
    if (name === './CatalogBuyerPhoto') return {CatalogBuyerPhoto: marker};
    if (name === '../ordering/OrderQuotePanel') return {OrderQuotePanel: marker};
    if (name === '@/lib/format') return displayFormat;
    throw new Error('Unexpected mounted catalog import: ' + name);
  };
  const code = ts.transpileModule(readFileSync(resolve('src/modules/catalog/BuyerCatalog.tsx'), 'utf8'), {compilerOptions: {module: ts.ModuleKind.CommonJS,
    target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
  const evaluated = {exports: {} as Record<string, unknown>};
  new Function('require', 'module', 'exports', 'window', 'performance', code)(imports, evaluated, evaluated.exports, context.window, {now: () => 0});
  const component = evaluated.exports.BuyerCatalog as (props: {returnPath: typeof returnPath; requestOnly: boolean}) => React.ReactElement;
  function render() {
    position = 0; pending = []; dirty = false; tree = component({returnPath, requestOnly: true});
    for (const item of pending) {const previous = effects.get(item.index);
      if (!previous || changed(previous.dependencies, item.dependencies)) {previous?.cleanup?.(); const cleanup = item.effect();
        effects.set(item.index, {dependencies: item.dependencies, cleanup: typeof cleanup === 'function' ? cleanup : undefined});}}
  }
  async function settle() {for (let tick = 0; tick < 60; tick++) {if (dirty && mounted) render(); await new Promise<void>(resolve => setImmediate(resolve));} assert.equal(dirty, false, 'Mounted catalog effects did not settle');}
  function find(predicate: (node: Node) => boolean) {const node = nodes(tree).find(predicate); assert.ok(node, 'Expected mounted catalog control'); return node;}
  function button(label: string) {return find(node => node.type === 'button' && text(node.props.children).trim() === label);}
  async function click(label: string) {const node = button(label); assert.notEqual(node.props.disabled, true); (node.props.onClick as () => void)(); await settle();}
  async function search(value: string) {(find(node => node.type === 'input' && node.props.placeholder === 'Nama barang, kode, atau jenis kemasan').props.onChange as (event: unknown) => void)({target: {value}}); await settle();
    (find(node => node.type === 'form').props.onSubmit as (event: unknown) => void)({preventDefault() {}}); await settle();}
  return {queries, settle, click, search, button, find, get tree() {return tree;},
    finishOld() {assert.ok(queries[0].finish); queries[0].finish(reply(queries[0], 'LATE_OLD_REPLY_MUST_NOT_RENDER'));},
    unmount() {mounted = false; for (const effect of effects.values()) effect.cleanup?.(); effects.clear();
      assert.equal(context.listenerCount('popstate'), 0); assert.equal(context.listenerCount('pageshow'), 0); assert.equal(context.timerCount(), 0);}};
}

test('actual buyer catalog canonicalizes its URL and restores query/page through popstate Back, Forward and remount', async t => {
  for (const path of ['/shop', '/catalog/browse'] as const) {
    const context = browser([path + '?q=%20Cafe%CC%81%20%26%20PENA%20&page=2&tracking=ignored#catalog-list']);
    let mounted = mountCatalog(context, path); t.after(() => mounted.unmount()); await mounted.settle();
    assert.equal(context.href, path + '?q=Caf%C3%A9+%26+PENA&page=2#catalog-list');
    assert.deepEqual(mounted.queries.map(query => [query.search, query.offset]), [['Café & PENA', 25]]);
    assert.equal(context.snapshot().length, 1); assert.deepEqual(context.window.history.state, {});
    await mounted.click('Berikutnya'); assert.equal(context.href, path + '?q=Caf%C3%A9+%26+PENA&page=3#catalog-list');
    context.window.history.back(); await mounted.settle(); assert.equal(mounted.queries.at(-1)!.offset, 25); assert.match(text(mounted.tree), /Halaman 2/);
    context.window.history.forward(); await mounted.settle(); assert.equal(mounted.queries.at(-1)!.offset, 50); assert.match(text(mounted.tree), /Halaman 3/);
    await mounted.search(' TAS '); assert.equal(context.href, path + '?q=TAS#catalog-list'); assert.match(text(mounted.tree), /Halaman 1/);
    context.window.history.back(); await mounted.settle(); assert.equal(mounted.queries.at(-1)!.search, 'Café & PENA');
    context.window.history.forward(); await mounted.settle(); assert.equal(mounted.queries.at(-1)!.search, 'TAS');
    const oldClick = mounted.button('Berikutnya').props.onClick as () => void, catalogUrl = context.href;
    context.window.history.pushState({}, '', '/identity/account'); context.window.history.pushState({}, '', catalogUrl); context.window.history.back(); await mounted.settle();
    const before = context.snapshot(), reads = mounted.queries.length; oldClick(); await mounted.settle();
    assert.equal(context.href, '/identity/account'); assert.deepEqual(context.snapshot(), before); assert.equal(mounted.queries.length, reads);
    context.window.history.forward(); await mounted.settle(); const storedHistory = context.snapshot(); mounted.unmount();
    mounted = mountCatalog(context, path); await mounted.settle();
    assert.deepEqual(mounted.queries.map(query => [query.search, query.offset]), [['TAS', 0]]); assert.deepEqual(context.snapshot(), storedHistory);
    assert.equal(mounted.find(node => node.type === 'input').props.value, 'TAS'); assert.match(text(mounted.tree), /Halaman 1/);
  }
});

test('actual buyer catalog cancels a superseded list, refuses its late reply and removes history listeners on unmount', async t => {
  const context = browser(['/shop?q=OLD&page=2', '/shop?q=NEW&page=3#catalog-main']), mounted = mountCatalog(context, '/shop', true);
  t.after(() => mounted.unmount()); await mounted.settle(); assert.equal(mounted.queries.length, 1);
  context.window.history.forward(); await mounted.settle();
  assert.deepEqual(mounted.queries.map(query => [query.search, query.offset]), [['OLD', 25], ['NEW', 50]]);
  assert.equal(mounted.queries[0].signal.aborted, true); assert.match(text(mounted.tree), /NEW \/ 50/);
  mounted.finishOld(); await mounted.settle();
  assert.doesNotMatch(text(mounted.tree), /LATE_OLD_REPLY_MUST_NOT_RENDER/); assert.match(text(mounted.tree), /NEW \/ 50/);
  assert.equal(context.href, '/shop?q=NEW&page=3#catalog-main'); assert.equal(mounted.find(node => node.type === 'input').props.value, 'NEW');
  mounted.unmount(); assert.equal(mounted.queries[1].signal.aborted, true);
  context.window.history.back(); await mounted.settle(); assert.equal(mounted.queries.length, 2);
});
