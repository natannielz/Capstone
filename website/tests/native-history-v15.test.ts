import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import ts from "typescript";
import { DEMO_ACCOUNTS, type Actor } from "../lib/domain/accounts";
import * as navigation from "../lib/domain/navigation";
import * as nativeHistory from "../lib/client/native-history";

type HistoryData = Record<string, unknown>;
type QueryPatch = Record<string, string | null>;
type Navigation = {
  page: navigation.WorkspacePage;
  selected?: string;
  query: URLSearchParams;
  go: (page: navigation.WorkspacePage, id?: string, patch?: QueryPatch) => void;
  updateQuery: (patch: QueryPatch) => void;
  onDirtyChange: (dirty: boolean) => void;
  pendingLeave: { run: () => void } | null;
  confirmLeave: () => void;
  cancelLeave: () => void;
};

/** Models Next 16's native-history wrapper and subsequent HistoryUpdater commit. */
function browser(initial = "/workspace", index = 0) {
  const events = new EventTarget();
  const tree = { tree: ["workspace"], renderedSearch: "" };
  const entries = [{ url: new URL(initial, "https://unit-toko.test"), data: { __NA: true, __PRIVATE_NEXTJS_INTERNALS_TREE: tree, unitTokoIndex: index } as HistoryData }];
  const applicationCalls: { method: string; data: HistoryData; url?: string }[] = [];
  let cursor = 0, canonical = entries[0].url.href;
  const location = () => entries[cursor].url;
  function write(method: "push" | "replace", input: HistoryData | null, raw?: string) {
    applicationCalls.push({ method, data: { ...input }, url: raw });
    let data = input ?? {};
    if (!data.__NA && !data._N) {
      data = { ...data, __NA: entries[cursor].data.__NA, __PRIVATE_NEXTJS_INTERNALS_TREE: entries[cursor].data.__PRIVATE_NEXTJS_INTERNALS_TREE };
      if (raw) canonical = new URL(raw, location()).href;
    }
    const entry = { url: raw ? new URL(raw, location()) : location(), data: { ...data } };
    if (method === "push") { entries.splice(cursor + 1); entries.push(entry); cursor += 1; }
    else entries[cursor] = entry;
  }
  const history = {
    get state() { return entries[cursor].data; },
    get length() { return entries.length; },
    scrollRestoration: "auto",
    pushState(data: HistoryData | null, _unused: string, url?: string) { write("push", data, url); },
    replaceState(data: HistoryData | null, _unused: string, url?: string) { write("replace", data, url); },
    go(delta: number) {
      const next = cursor + delta;
      if (next < 0 || next >= entries.length) return;
      cursor = next;
      canonical = location().href;
      events.dispatchEvent(new Event("popstate"));
    },
  };
  const window = {
    get location() { return location(); }, history, scrollY: 0, scrollTo() {},
    addEventListener: events.addEventListener.bind(events),
    removeEventListener: events.removeEventListener.bind(events),
    dispatchEvent: events.dispatchEvent.bind(events),
  };
  return { window, history, applicationCalls, tree,
    get href() { return location().pathname + location().search; },
    get canonical() { return new URL(canonical).pathname + new URL(canonical).search; },
    commitRouter() { history.replaceState({ ...history.state, __NA: true }, "", canonical); },
  };
}

function evaluate(file: string, imports: (name: string) => unknown, window: ReturnType<typeof browser>["window"], extra = "") {
  const source = readFileSync(resolve(file), "utf8") + extra;
  const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true } }).outputText;
  const evaluatedModule: { exports: Record<string, unknown> } = { exports: {} };
  new Function("require", "module", "exports", "window", "document", "requestAnimationFrame", code)(
    imports, evaluatedModule, evaluatedModule.exports, window,
    { getElementById: () => ({ focus() {} }) }, (callback: () => void) => callback(),
  );
  return evaluatedModule.exports;
}

function workspaceHarness(role: Actor["role"] = "penagihan", initial = "/workspace", index = 0) {
  const context = browser(initial, index), slots: unknown[] = [], effects: (() => void)[] = [];
  let position = 0;
  const react = {
    useRef(value: unknown) { const slot = position++; return slots[slot] ??= { current: value }; },
    useState(value: unknown) {
      const slot = position++;
      if (!(slot in slots)) slots[slot] = value;
      return [slots[slot], (next: unknown) => { slots[slot] = next; }];
    },
    useCallback(callback: unknown) { return callback; },
    useEffect(effect: () => void) { const slot = position++; if (!(slot in slots)) { slots[slot] = true; effects.push(effect); } },
    useSyncExternalStore(_subscribe: unknown, snapshot: () => string) { return snapshot(); },
  };
  const evaluated = evaluate("components/use-workspace-navigation.ts", name => {
    if (name === "react") return react;
    if (name === "@/lib/domain/navigation") return navigation;
    if (name === "@/lib/client/native-history") return nativeHistory;
    return {};
  }, context.window);
  const navigationHook = evaluated.useWorkspaceNavigation as (actor: Actor, close: () => void) => Navigation;
  const actor = DEMO_ACCOUNTS.find(account => account.role === role)!;
  const close = () => {};
  const render = () => { position = 0; return navigationHook(actor, close); };
  render(); effects.forEach(effect => effect());
  return { ...context, browser: context, render };
}

test("v15 native-history metadata preserves only a valid workspace index and excludes Next loop markers", () => {
  const source = { __NA: true, _N: true, __PRIVATE_NEXTJS_INTERNALS_TREE: { private: true }, unitTokoIndex: 7, unrelated: "opaque" };
  assert.deepEqual(nativeHistory.applicationHistoryState(source), { unitTokoIndex: 7 });
  assert.deepEqual(nativeHistory.applicationHistoryState(source, 8), { unitTokoIndex: 8 });
  assert.equal(source.unitTokoIndex, 7);
  for (const value of [undefined, null, "4", -1, Infinity, 1.2, Number.MAX_SAFE_INTEGER + 1]) {
    assert.equal(nativeHistory.workspaceHistoryIndex({ unitTokoIndex: value }), 0);
  }
});

test("v15 external history updates keep Next canonical URL synchronized across a later router commit", () => {
  const legacy = browser();
  legacy.history.pushState({ ...legacy.history.state, unitTokoIndex: 1 }, "", "/workspace?view=payments");
  assert.equal(legacy.href, "/workspace?view=payments");
  legacy.commitRouter();
  assert.equal(legacy.href, "/workspace", "reusing Next's loop marker reproduces the stale canonical URL");
  const fixed = browser();
  fixed.history.pushState(nativeHistory.applicationHistoryState(fixed.history.state, 1), "", "/workspace?view=payments");
  fixed.commitRouter();
  assert.equal(fixed.href, "/workspace?view=payments");
  assert.equal(fixed.history.state.__PRIVATE_NEXTJS_INTERNALS_TREE, fixed.tree);
  assert.equal(fixed.history.state.unitTokoIndex, 1);
});

test("v15 workspace mount, module links and filter reset retain the current module after router commits", () => {
  const app = workspaceHarness("penagihan", "/workspace", 4);
  assert.deepEqual(app.applicationCalls[0].data, { unitTokoIndex: 4 });
  app.render().go("payments", undefined, { paymentStatus: "recorded", paymentSearch: "Nadia", paymentPage: "3" });
  app.browser.commitRouter();
  assert.equal(app.render().page, "payments");
  assert.equal(app.history.state.unitTokoIndex, 5);
  app.render().updateQuery({ paymentStatus: null, paymentPage: null });
  app.browser.commitRouter();
  assert.equal(app.browser.href, "/workspace?view=payments&paymentSearch=Nadia");
  assert.equal(app.render().query.get("paymentSearch"), "Nadia");
  app.render().updateQuery({ paymentSearch: null });
  app.browser.commitRouter();
  assert.equal(app.browser.href, "/workspace?view=payments");
  assert.equal(app.history.length, 2, "filter changes replace the same module entry");
  assert.equal(app.history.state.unitTokoIndex, 5);
});

test("v15 workspace back, forward and remembered filters retain independent module history", () => {
  const app = workspaceHarness("penagihan");
  app.render().go("payments", undefined, { paymentStatus: "recorded" });
  app.render().go("profile");
  app.history.go(-1); app.browser.commitRouter();
  assert.equal(app.render().page, "payments");
  assert.equal(app.render().query.get("paymentStatus"), "recorded");
  assert.equal(app.history.state.unitTokoIndex, 1);
  app.history.go(1); app.browser.commitRouter();
  assert.equal(app.render().page, "profile");
  assert.equal(app.history.state.unitTokoIndex, 2);
  app.render().go("payments"); app.browser.commitRouter();
  assert.equal(app.render().query.get("paymentStatus"), "recorded");
  assert.equal(app.history.state.unitTokoIndex, 3);
  app.render().go("admin");
  assert.equal(app.render().page, "payments", "existing role guard still rejects forbidden modules");
});

test("v15 workspace dirty back navigation restores its index until leave is explicitly confirmed", () => {
  const app = workspaceHarness("penagihan");
  app.render().go("payments");
  app.render().go("profile");
  app.render().onDirtyChange(true);
  app.history.go(-1);
  assert.equal(app.browser.href, "/workspace?view=profile");
  assert.equal(app.history.state.unitTokoIndex, 2);
  assert.ok(app.render().pendingLeave);
  app.render().cancelLeave();
  assert.equal(app.render().pendingLeave, null);
  app.history.go(-1);
  app.render().confirmLeave();
  app.browser.commitRouter();
  assert.equal(app.render().page, "payments");
  assert.equal(app.history.state.unitTokoIndex, 1);
});

test("v15 catalog query updates preserve the module, selection context and workspace back index", () => {
  const context = browser("/workspace?view=catalog&id=order-a&returnOrder=order-a#items", 6);
  const evaluated = evaluate("components/catalog.tsx", name => name === "@/lib/client/native-history" ? nativeHistory : {}, context.window, "\nexport { writeQuery };\n");
  const writeQuery = evaluated.writeQuery as (query: Record<string, string>) => void;
  writeQuery({ q: "kopi", category: "Semua", collection: "", product: "", page: "1" });
  context.commitRouter();
  assert.equal(context.window.location.search, "?view=catalog&id=order-a&returnOrder=order-a&q=kopi");
  assert.equal(context.window.location.hash, "#items");
  assert.equal(context.history.state.unitTokoIndex, 6);
  assert.equal(context.history.length, 1);
});
