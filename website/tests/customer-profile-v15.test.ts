import {test} from "node:test";
import assert from "node:assert/strict";
import {readFileSync} from "node:fs";
import {resolve} from "node:path";
import {createRequire} from "node:module";
import React from "react";
import ts from "typescript";
import * as drafts from "../lib/client/customer-profile-draft";
import * as requests from "../lib/client/requests";
import {emptyState} from "../lib/domain/seed";
import type {Actor} from "../lib/domain/accounts";

function memoryStorage() {
  const data = new Map<string, string>();
  return {data, getItem: (key: string) => data.get(key) ?? null, setItem: (key: string, value: string) => {data.set(key, value);}, removeItem: (key: string) => {data.delete(key);}};
}
const original: drafts.ProfileValues = {name: "Pelanggan", phone: "08123", position: "", address: "Alamat lama"};

test("v15 profile drafts retain the original server baseline but never serialize credentials or another actor", () => {
  const storage = memoryStorage();
  const values = {...original, address: "Alamat lokal", password: "do-not-store", currentPassword: "also-secret", token: "private"};
  assert.equal(drafts.saveCustomerProfileDraft("buyer-a", original, values, storage, 1000), true);
  const saved = drafts.loadCustomerProfileDraft("buyer-a", storage, 2000).draft!;
  assert.deepEqual(saved.baseline, original);
  assert.deepEqual(saved.values, {...original, address: "Alamat lokal"});
  assert.doesNotMatch(storage.getItem(drafts.customerProfileDraftKey("buyer-a"))!, /password|token|secret|do-not-store/i);
  assert.equal(drafts.loadCustomerProfileDraft("buyer-b", storage, 2000).draft, undefined);
  storage.setItem(drafts.customerProfileDraftKey("buyer-b"), JSON.stringify(saved));
  assert.equal(drafts.loadCustomerProfileDraft("buyer-b", storage, 2000).draft, undefined, "payload owner must also match its key");
});

test("v15 clean fields follow refreshed server data while dirty fields and conflicts survive remount", () => {
  const current = {...original, name: "Nama lokal"};
  const incoming = {...original, name: "Nama dari server", phone: "08999"};
  const merged = drafts.reconcileProfileValues(original, current, incoming);
  assert.equal(merged.values.name, "Nama lokal");
  assert.equal(merged.values.phone, "08999");
  assert.deepEqual(merged.conflicts, ["name"]);
  assert.deepEqual(merged.changedOnServer, ["name", "phone"]);
  assert.deepEqual(drafts.reconcileProfileValues(original, original, incoming).values, incoming, "fully clean form follows the server");
  const updated = {...incoming, address: "Alamat server terbaru"};
  const second = drafts.reconcileProfileValues(incoming, merged.values, updated);
  assert.equal(second.values.name, "Nama lokal");
  assert.equal(second.values.address, "Alamat server terbaru");
});

test("v15 reverted and explicitly discarded drafts stay cleared without erasing another customer's draft", () => {
  const storage = memoryStorage();
  for (const actor of ["a", "b"]) drafts.saveCustomerProfileDraft(actor, original, {...original, name: actor}, storage);
  assert.equal(drafts.saveCustomerProfileDraft("a", original, original, storage), true);
  assert.equal(drafts.loadCustomerProfileDraft("a", storage).draft, undefined);
  assert.ok(drafts.loadCustomerProfileDraft("b", storage).draft);
  assert.equal(drafts.clearCustomerProfileDraft("b", storage), true);
  assert.equal(drafts.loadCustomerProfileDraft("b", storage).draft, undefined);
});

test("v15 stale, invalid and unavailable storage never report a recovered or successfully saved draft", () => {
  const storage = memoryStorage();
  drafts.saveCustomerProfileDraft("a", original, {...original, name: "Draft"}, storage, 1000);
  assert.equal(drafts.loadCustomerProfileDraft("a", storage, 1000 + 8 * 60 * 60 * 1000 + 1).draft, undefined);
  assert.equal(drafts.loadCustomerProfileDraft("a", storage, -100_000).draft, undefined);
  storage.setItem(drafts.customerProfileDraftKey("a"), "not-json");
  assert.equal(drafts.loadCustomerProfileDraft("a", storage).draft, undefined);
  const denied = {getItem() {throw new Error("denied");}, setItem() {throw new Error("denied");}, removeItem() {throw new Error("denied");}};
  assert.equal(drafts.loadCustomerProfileDraft("a", denied).available, false);
  assert.equal(drafts.saveCustomerProfileDraft("a", original, {...original, name: "Draft"}, denied), false);
  assert.equal(drafts.clearCustomerProfileDraft("a", denied), false);
  assert.equal(drafts.saveCustomerProfileDraft("a", original, {...original, name: "Draft"}, {...storage, setItem() {}}), false, "silently dropped writes also fail readback");
});

test("v15 cleanup overwrites an undeletable draft with a content-free marker and saved normalized values do not resurrect", () => {
  const storage = memoryStorage();
  const untrimmed = {...original, name: "  Nama tersimpan  "};
  drafts.saveCustomerProfileDraft("saved-buyer", original, untrimmed, storage);
  const removalDenied = {...storage, removeItem() {throw new Error("remove denied");}};
  assert.equal(drafts.clearCustomerProfileDraft("saved-buyer", removalDenied), true, "overwrite fallback removes profile contents");
  assert.deepEqual(JSON.parse(storage.getItem(drafts.customerProfileDraftKey("saved-buyer"))!), {version: 1, actorId: "saved-buyer", discarded: true});
  assert.equal(drafts.loadCustomerProfileDraft("saved-buyer", storage).draft, undefined);
  const freshServer = {...original, name: "Nama tersimpan", phone: "08999"};
  const restored = drafts.reconcileProfileValues(original, untrimmed, freshServer, true);
  assert.deepEqual(restored.values, freshServer, "even a stale raw draft after a full reload is clean once the server saved its normalized values");
  assert.equal(restored.conflicts.length, 0);
  drafts.saveCustomerProfileDraft("unavailable-buyer", original, untrimmed, storage);
  const writeDenied = {...storage, setItem() {throw new Error("set denied");}, removeItem() {throw new Error("remove denied");}};
  assert.equal(drafts.clearCustomerProfileDraft("unavailable-buyer", writeDenied), false, "do not claim physical cleanup when all writes fail");
  assert.ok(storage.getItem(drafts.customerProfileDraftKey("unavailable-buyer")));
  assert.equal(drafts.loadCustomerProfileDraft("unavailable-buyer", storage).draft, undefined, "discard remains effective for this mounted application");
});

test("v15 unavailable optional draft storage never prevents logout; server failure preserves the draft", async () => {
  const denied = {getItem() {throw new Error("denied");}, setItem() {throw new Error("denied");}, removeItem() {throw new Error("denied");}};
  let revoked = 0;
  const result = await drafts.logoutWithProfileCleanup("clean-buyer", async () => {revoked++;}, denied);
  assert.equal(revoked, 1);
  assert.equal(result.draftCleared, false, "cleanup uncertainty is separate from successful server logout");
  const storage = memoryStorage();
  drafts.saveCustomerProfileDraft("retry-buyer", original, {...original, name: "Unsent"}, storage);
  await assert.rejects(() => drafts.logoutWithProfileCleanup("retry-buyer", async () => {throw new Error("network error");}, storage), /network error/);
  assert.equal(drafts.loadCustomerProfileDraft("retry-buyer", storage).draft?.values.name, "Unsent");
});

test("v15 draft cleanup owns a persisted generation and cannot erase an identical newer write", () => {
  const storage = memoryStorage();
  const values = {...original, name: "Draft"};
  drafts.saveCustomerProfileDraft("generation-buyer", original, values, storage, 1000);
  const first = drafts.captureCustomerProfileDraft("generation-buyer", storage);
  drafts.saveCustomerProfileDraft("generation-buyer", original, values, storage, 1000);
  const second = drafts.captureCustomerProfileDraft("generation-buyer", storage);
  assert.notEqual(first, second, "writes within one millisecond still have different generations");
  assert.equal(drafts.clearCustomerProfileDraftSnapshot("generation-buyer", first, storage), "superseded");
  assert.equal(drafts.captureCustomerProfileDraft("generation-buyer", storage), second);
  assert.ok(drafts.loadCustomerProfileDraft("generation-buyer", storage, 1000).draft, "old cleanup must not add a tombstone or invalidation for a newer draft");
  assert.equal(drafts.clearCustomerProfileDraftSnapshot("generation-buyer", second, storage), "cleared");
  assert.equal(storage.getItem(drafts.customerProfileDraftKey("generation-buyer")), null);
});

type Node = {type: unknown; props: Record<string, unknown>};
function nodes(value: unknown): Node[] {if (Array.isArray(value)) return value.flatMap(nodes); if (!value || typeof value !== "object" || !("props" in value)) return []; const node = value as Node; return [node, ...nodes(node.props.children)];}
function find(tree: unknown, predicate: (node: Node) => boolean) {const node = nodes(tree).find(predicate); assert.ok(node); return node;}
function text(value: unknown): string {if (typeof value === "string" || typeof value === "number") return String(value); if (Array.isArray(value)) return value.map(text).join(""); return value && typeof value === "object" && "props" in value ? text((value as Node).props.children) : "";}
const runtimeRequire = createRequire(resolve("package.json"));

function editorHarness(refresh: () => Promise<void> = async () => {}) {
  const hooks: unknown[] = [];
  const effects = new Map<number, {dependencies?: readonly unknown[]; cleanup?: () => void}>();
  let pendingEffects: {index: number; effect: () => void | (() => void); dependencies?: readonly unknown[]}[] = [];
  let position = 0, changed = false;
  const mockReact = {...React,
  useEffect(effect: () => void | (() => void), dependencies?: readonly unknown[]) {pendingEffects.push({index: position++, effect, dependencies});},
  useRef(initial: unknown) {const index = position++; return hooks[index] ??= {current: initial};},
  useState(initial: unknown) {
    const index = position++;
    if (!(index in hooks)) hooks[index] = typeof initial === "function" ? initial() : initial;
    return [hooks[index], (value: unknown) => {const next = typeof value === "function" ? value(hooks[index]) : value; changed ||= !Object.is(next, hooks[index]); hooks[index] = next;}];
  }};
  const source = readFileSync(resolve("components/profile.tsx"), "utf8") + "\nexport {ProfileEditor};\n";
  const code = ts.transpileModule(source, {compilerOptions: {module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
  const evaluated: {exports: {ProfileEditor?: (props: unknown) => React.ReactElement}} = {exports: {}};
  const imports = (id: string): unknown => {
    if (id === "react") return mockReact;
    if (id === "react/jsx-runtime") return runtimeRequire(id);
    if (id === "@/lib/client/customer-profile-draft") return drafts;
    if (id === "@/lib/client/requests") return requests;
    if (id === "lucide-react") return new Proxy({}, {get: () => "svg"});
    if (id === "./art") return {Art: () => null};
    if (id === "next/link") return {__esModule: true, default: "a"};
    if (id === "@/lib/domain/accounts") return {ROLE_LABELS: {customer: "Pelanggan"}};
    if (id.startsWith("@/components/ui/")) return new Proxy({}, {get: (_, name) => name === "Input" ? "input" : name === "Textarea" ? "textarea" : name === "Button" ? "button" : "div"});
    return {};
  };
  new Function("require", "module", "exports", code)(imports, evaluated, evaluated.exports);
  const render = (actor: Actor) => {
    let tree: React.ReactElement | undefined;
    for (let attempt = 0; attempt < 10; attempt++) {
      position = 0; changed = false; pendingEffects = [];
      tree = evaluated.exports.ProfileEditor!({actor, s: emptyState(), refresh});
      if (!changed) {
        for (const {index, effect, dependencies} of pendingEffects) {
          const previous = effects.get(index);
          if (!previous || !dependencies || !previous.dependencies || dependencies.some((item, key) => !Object.is(item, previous.dependencies?.[key]))) {
            previous?.cleanup?.();
            effects.set(index, {dependencies, cleanup: effect() || undefined});
          }
        }
        return tree;
      }
    }
    throw new Error("Profile render did not settle");
  };
  return Object.assign(render, {unmount() {for (const effect of effects.values()) effect.cleanup?.(); effects.clear();}});
}

test("v15 actual Profile edits survive a SPA remount; refresh merges and explicit discard uses latest server values", t => {
  const storage = memoryStorage(), descriptor = Object.getOwnPropertyDescriptor(globalThis, "window");
  Object.defineProperty(globalThis, "window", {configurable: true, value: {sessionStorage: storage}});
  t.after(() => {if (descriptor) Object.defineProperty(globalThis, "window", descriptor); else Reflect.deleteProperty(globalThis, "window");});
  const actor: Actor = {id: "buyer", name: original.name, phone: original.phone, address: original.address, email: "buyer@example.invalid", role: "customer", divisionId: null, active: true};
  const render = editorHarness();
  const edit = (tree: unknown, id: string, value: string) => (find(tree, node => node.props.id === id).props.onChange as (event: {target: {value: string}}) => void)({target: {value}});
  let tree = render(actor);
  edit(tree, "profile-name", "Nama lokal");
  assert.equal(drafts.loadCustomerProfileDraft(actor.id, storage).draft?.values.name, "Nama lokal", "persisted before the next render or unmount");
  tree = render(actor);
  edit(tree, "old-password", "never-persist-this-password");
  const freshActor = {...actor, name: "Nama server", phone: "08999"};
  tree = render(freshActor);
  assert.equal(find(tree, node => node.props.id === "profile-name").props.value, "Nama lokal");
  assert.equal(find(tree, node => node.props.id === "profile-phone").props.value, "08999");
  assert.match(text(tree), /Data profil di server telah berubah/);
  assert.doesNotMatch([...storage.data.values()].join(""), /never-persist/);
  const remount = editorHarness();
  tree = remount(freshActor);
  assert.equal(find(tree, node => node.props.id === "profile-name").props.value, "Nama lokal");
  assert.equal(find(tree, node => node.props.id === "profile-phone").props.value, "08999");
  assert.equal(find(tree, node => node.props.id === "old-password").props.value, "");
  assert.match(text(tree), /Draf data pribadi dipulihkan/);
  (find(tree, node => node.type === "button" && text(node.props.children) === "Buang perubahan & gunakan data terbaru").props.onClick as () => void)();
  tree = remount(freshActor);
  assert.equal(find(tree, node => node.props.id === "profile-name").props.value, "Nama server");
  assert.equal(drafts.loadCustomerProfileDraft(actor.id, storage).draft, undefined);
  const newest = {...freshActor, name: "Nama server berikutnya", address: "Alamat server berikutnya"};
  tree = remount(newest);
  assert.equal(find(tree, node => node.props.id === "profile-name").props.value, newest.name, "a clean mounted form tracks actor updates");
  assert.equal(find(tree, node => node.props.id === "profile-address").props.value, newest.address);
});

test("v15 a delayed Profile PATCH cannot erase drafts from a newer editor or newer edits in the same editor", async t => {
  const storage = memoryStorage(), descriptor = Object.getOwnPropertyDescriptor(globalThis, "window"), originalFetch = globalThis.fetch;
  Object.defineProperty(globalThis, "window", {configurable: true, value: {sessionStorage: storage}});
  t.after(() => {globalThis.fetch = originalFetch; if (descriptor) Object.defineProperty(globalThis, "window", descriptor); else Reflect.deleteProperty(globalThis, "window");});
  for (const mode of ["unmounted", "still-mounted", "same-editor", "failed-write"] as const) {
    const actor: Actor = {id: `race-${mode}`, name: original.name, phone: original.phone, address: original.address, email: "buyer@example.invalid", role: "customer", divisionId: null, active: true};
    let resolvePatch: (response: Response) => void = () => {throw new Error("PATCH did not start");};
    let refreshes = 0;
    globalThis.fetch = async (_url, init) => {
      assert.equal(init?.method, "PATCH");
      assert.equal(JSON.parse(String(init?.body)).name, "Draf dikirim");
      return new Promise<Response>(resolve => {resolvePatch = resolve;});
    };
    const first = editorHarness(async () => {refreshes++;});
    const edit = (tree: unknown, value: string) => (find(tree, node => node.props.id === "profile-name").props.onChange as (event: {target: {value: string}}) => void)({target: {value}});
    let tree = first(actor);
    edit(tree, "Draf dikirim"); tree = first(actor);
    const pending = (find(tree, node => node.type === "form").props.onSubmit as (event: {preventDefault(): void}) => Promise<void>)({preventDefault() {}});
    if (mode === "unmounted") first.unmount();
    const current = mode === "same-editor" ? first : editorHarness();
    tree = current(actor);
    // The same-editor branch exercises an already queued input event. Browser
    // controls are disabled while pending, but a late handler must still be safe.
    edit(tree, "Draf lebih baru"); tree = current(actor);
    if (mode === "failed-write") {
      const write = storage.setItem;
      storage.setItem = () => {throw new Error("write denied");};
      edit(first(actor), "Draf dikirim"); first(actor);
      storage.setItem = write;
    }
    const newerSnapshot = drafts.captureCustomerProfileDraft(actor.id, storage);
    resolvePatch(Response.json({message: "Profil disimpan"}));
    await pending;
    if (mode !== "unmounted") first(actor); // Run the older clean-state effect too.
    tree = current(actor);
    assert.equal(find(tree, node => node.props.id === "profile-name").props.value, "Draf lebih baru", mode);
    assert.equal(drafts.captureCustomerProfileDraft(actor.id, storage), newerSnapshot, mode);
    assert.equal(drafts.loadCustomerProfileDraft(actor.id, storage).draft?.values.name, "Draf lebih baru", mode);
    if (mode === "unmounted") assert.equal(refreshes, 0, "unmounted save does not refresh/focus another editor");
    const afterBack = editorHarness()({...actor, name: "Draf dikirim"});
    assert.equal(find(afterBack, node => node.props.id === "profile-name").props.value, "Draf lebih baru", `${mode} survives another remount`);
  }
});
