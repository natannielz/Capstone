import {test} from "node:test";
import assert from "node:assert/strict";
import {readFileSync} from "node:fs";
import {resolve} from "node:path";
import {createRequire} from "node:module";
import React from "react";
import ts from "typescript";
import * as drafts from "../lib/client/customer-action-draft";
import * as requests from "../lib/client/requests";
import type {CommandResult} from "../lib/domain/model";

function memoryStorage() {
  const values = new Map<string, string>();
  return {get length() {return values.size;}, key(index: number) {return [...values.keys()][index] || null;}, getItem(key: string) {return values.get(key) || null;}, setItem(key: string, value: string) {values.set(key, value);}, removeItem(key: string) {values.delete(key);}};
}
function draft(actorId = "customer-a"): drafts.CustomerActionDraft {
  return {version: 1, actorId, target: "payment.record:invoice-a", path: "/account/orders/order-a", command: {id: crypto.randomUUID(), type: "payment.record", data: {invoiceId: "invoice-a", amount: 10000}}, baseData: {invoiceId: "invoice-a"}, values: {amount: "10000"}, presentation: {title: "Pembayaran", description: "Simulasi", fields: [{key: "amount", label: "Jumlah", type: "number"}]}};
}

test("v7 unresolved customer action retains exact identity and payload across remount, scope and attempted replacement", () => {
  const storage = memoryStorage(), first = draft();
  drafts.saveCustomerActionDraft(first, storage);
  const changed = {...first, command: {...first.command, id: crypto.randomUUID(), data: {...first.command.data, amount: 20000}}};
  assert.deepEqual(drafts.saveCustomerActionDraft(changed, storage), first);
  assert.deepEqual(drafts.readCustomerActionDraft(first.actorId, first.target, storage), first);
  assert.deepEqual(drafts.findPendingCustomerAction(first.actorId, first.path, storage), first);
  assert.equal(drafts.findPendingCustomerAction("customer-b", first.path, storage), undefined);
  assert.equal(drafts.findPendingCustomerAction(first.actorId, "/account/orders/another-order", storage), undefined);
  assert.equal(drafts.readCustomerActionDraft(first.actorId, "payment.record:invoice-b", storage), undefined);
  drafts.clearCustomerActionDraft(first.actorId, first.target, "not-the-same-command", storage);
  assert.ok(drafts.readCustomerActionDraft(first.actorId, first.target, storage));
  drafts.clearCustomerActionDraft(first.actorId, first.target, first.command.id, storage);
  assert.equal(drafts.readCustomerActionDraft(first.actorId, first.target, storage), undefined);
});

test("v7 ambiguous failures preserve drafts while explicit non-auth 4xx allow correction", () => {
  for (const status of [0, 200, 201, 401, 500, 503]) assert.equal(drafts.ambiguousCustomerActionError(new requests.ApiError("Failure", status)), true);
  for (const status of [400, 403, 404, 409, 422, 429]) assert.equal(drafts.ambiguousCustomerActionError(new requests.ApiError("Failure", status)), false);
  assert.equal(drafts.ambiguousCustomerActionError(new TypeError("Offline")), true);
  assert.deepEqual(drafts.customerActionValues("shipment.receive", {id: "shipment-a"}, {receiver: "Penerima", qty_line1: "2", reason_line1: "Satu ditolak", qty_line2: "0", reason_line2: "Tidak diterima"}), {id: "shipment-a", receiver: "Penerima", lines: [{id: "line1", accepted: 2, reason: "Satu ditolak"}, {id: "line2", accepted: 0, reason: "Tidak diterima"}]});
});

type Node = {type: unknown; props: Record<string, unknown>};
type Action = drafts.CustomerActionPresentation & {type: string; data: Record<string, unknown>; map?: (values: Record<string, string>) => Record<string, unknown>};
type DialogProps = {actorId: string; action: Action; close: () => void; complete: (result: CommandResult) => Promise<void>};
const runtimeRequire = createRequire(resolve("package.json"));
function harness(props: DialogProps) {
  const hooks: unknown[] = [];
  let position = 0;
  const mockReact = {...React,
    useState(initial: unknown) {const index = position++; if (!(index in hooks)) hooks[index] = typeof initial === "function" ? initial() : initial; return [hooks[index], (value: unknown) => {hooks[index] = typeof value === "function" ? value(hooks[index]) : value;}];},
    useRef(initial: unknown) {const index = position++; return hooks[index] ??= {current: initial};},
    useEffect() {},
  };
  const source = readFileSync(resolve("components/customer-account.tsx"), "utf8") + "\nexport {CustomerActionDialog};\n";
  const code = ts.transpileModule(source, {compilerOptions: {module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
  const evaluatedModule: {exports: {CustomerActionDialog?: (props: DialogProps) => React.ReactElement}} = {exports: {}};
  const imports = (id: string) => {
    if (id === "react") return mockReact;
    if (id === "react/jsx-runtime") return runtimeRequire(id);
    if (id === "@/lib/client/customer-action-draft") return drafts;
    if (id === "@/lib/client/requests") return requests;
    if (id.startsWith("./ui/")) return new Proxy({}, {get: (_, name) => name === "Input" ? "input" : name === "Textarea" ? "textarea" : name === "Button" ? "button" : String(name)});
    return {};
  };
  new Function("require", "module", "exports", code)(imports, evaluatedModule, evaluatedModule.exports);
  assert.ok(evaluatedModule.exports.CustomerActionDialog);
  return () => {position = 0; return evaluatedModule.exports.CustomerActionDialog!(props);};
}
function nodes(value: unknown): Node[] {if (Array.isArray(value)) return value.flatMap(nodes); if (!value || typeof value !== "object" || !("props" in value)) return []; const node = value as Node; return [node, ...nodes(node.props.children)];}
function find(tree: unknown, predicate: (node: Node) => boolean) {const node = nodes(tree).find(predicate); assert.ok(node); return node;}
function submit(tree: unknown) {return (find(tree, node => node.type === "form").props.onSubmit as (event: {preventDefault: () => void}) => Promise<void>)({preventDefault() {}});}
function edit(tree: unknown, value: string) {(find(tree, node => node.props.id === "customer-action-amount").props.onChange as (event: {target: {value: string}}) => void)({target: {value}});}
function props(complete: (result: CommandResult) => Promise<void> = async () => {}): DialogProps {
  return {actorId: "customer-a", action: {title: "Bayar", description: "Simulasi", type: "payment.record", data: {invoiceId: "invoice-a"}, fields: [{key: "amount", label: "Jumlah", type: "number", value: 10000}], map: values => ({amount: Number(values.amount)})}, close() {}, complete};
}

test("v7 payment commit with lost response survives close/reopen and replays once with exact payload", async t => {
  const storage = memoryStorage(), descriptor = Object.getOwnPropertyDescriptor(globalThis, "window"), originalFetch = globalThis.fetch;
  Object.defineProperty(globalThis, "window", {configurable: true, value: {sessionStorage: storage, location: {pathname: "/account/orders/order-a", search: "", assign() {}}}});
  t.after(() => {globalThis.fetch = originalFetch; if (descriptor) Object.defineProperty(globalThis, "window", descriptor); else Reflect.deleteProperty(globalThis, "window");});
  const committed = new Map<string, CommandResult>(), bodies: string[] = [];
  let dropResponse = true, completions = 0;
  globalThis.fetch = async (_url, options) => {
    const body = String(options?.body); bodies.push(body);
    const command = JSON.parse(body);
    if (!committed.has(command.id)) committed.set(command.id, {id: crypto.randomUUID(), message: "Tersimpan"});
    if (dropResponse) {dropResponse = false; throw new TypeError("Simulated response lost after commit");}
    return Response.json(committed.get(command.id));
  };
  const first = harness(props(async () => {completions++;}));
  await submit(first());
  assert.equal(committed.size, 1); assert.equal(completions, 0);
  assert.equal(find(first(), node => node.props.id === "customer-action-amount").props.disabled, true);
  edit(first(), "20000"); assert.equal(find(first(), node => node.props.id === "customer-action-amount").props.value, "10000");
  // A wholly new hooks array simulates unmount/reload rather than retrying one mounted dialog.
  const reopened = harness(props(async () => {completions++;}));
  assert.equal(find(reopened(), node => node.props.id === "customer-action-amount").props.disabled, true);
  await submit(reopened());
  assert.equal(bodies[0], bodies[1]); assert.equal(committed.size, 1); assert.equal(completions, 1); assert.equal(storage.length, 0);
});

test("v7 explicit validation error unlocks correction but 503 and unreadable success keep exact draft", async t => {
  const storage = memoryStorage(), descriptor = Object.getOwnPropertyDescriptor(globalThis, "window"), originalFetch = globalThis.fetch;
  Object.defineProperty(globalThis, "window", {configurable: true, value: {sessionStorage: storage, location: {pathname: "/account/orders/order-a", search: "", assign() {}}}});
  t.after(() => {globalThis.fetch = originalFetch; if (descriptor) Object.defineProperty(globalThis, "window", descriptor); else Reflect.deleteProperty(globalThis, "window");});
  const bodies: string[] = []; let mode = "invalid";
  globalThis.fetch = async (_url, init) => {bodies.push(String(init?.body)); return mode === "invalid" ? Response.json({error: "Jumlah tidak valid"}, {status: 400}) : mode === "503" ? Response.json({error: "Sementara gagal"}, {status: 503}) : mode === "unreadable" ? new Response("broken", {status: 200}) : Response.json({});};
  const render = harness(props());
  await submit(render()); assert.equal(storage.length, 0); assert.equal(find(render(), node => node.props.id === "customer-action-amount").props.disabled, false);
  edit(render(), "9000"); mode = "503"; await submit(render());
  assert.notEqual(JSON.parse(bodies[0]).id, JSON.parse(bodies[1]).id); assert.equal(JSON.parse(bodies[1]).data.amount, 9000); assert.equal(storage.length, 1);
  for (mode of ["unreadable", "malformed"]) {await submit(render()); assert.equal(bodies.at(-1), bodies[1]); assert.equal(find(render(), node => node.props.id === "customer-action-amount").props.disabled, true);}
});

test("v7 expired customer session redirects safely and retains unresolved command for login recovery", async t => {
  const storage = memoryStorage(), descriptor = Object.getOwnPropertyDescriptor(globalThis, "window"), originalFetch = globalThis.fetch, redirects: string[] = [];
  Object.defineProperty(globalThis, "window", {configurable: true, value: {sessionStorage: storage, location: {pathname: "/account/orders/order-a", search: "?created=1", assign(value: string) {redirects.push(value);}}}});
  t.after(() => {globalThis.fetch = originalFetch; if (descriptor) Object.defineProperty(globalThis, "window", descriptor); else Reflect.deleteProperty(globalThis, "window");});
  globalThis.fetch = async () => Response.json({error: "Sesi berakhir"}, {status: 401});
  await submit(harness(props())());
  assert.deepEqual(redirects, ["/customer/login?next=%2Faccount%2Forders%2Forder-a%3Fcreated%3D1"]);
  assert.ok(drafts.findPendingCustomerAction("customer-a", "/account/orders/order-a", storage));
  assert.equal(drafts.findPendingCustomerAction("customer-b", "/account/orders/order-a", storage), undefined);
});

test("v7 unavailable browser draft storage prevents sending a non-recoverable payment", async t => {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, "window"), originalFetch = globalThis.fetch;
  const storage = {...memoryStorage(), setItem() {throw new Error("Storage blocked");}};
  Object.defineProperty(globalThis, "window", {configurable: true, value: {sessionStorage: storage, location: {pathname: "/account/orders/order-a", search: "", assign() {}}}});
  t.after(() => {globalThis.fetch = originalFetch; if (descriptor) Object.defineProperty(globalThis, "window", descriptor); else Reflect.deleteProperty(globalThis, "window");});
  let requestsSent = 0; globalThis.fetch = async () => {requestsSent++; return Response.json({id: "should-not-send", message: "Unexpected"});};
  await submit(harness(props())()); assert.equal(requestsSent, 0);
});
