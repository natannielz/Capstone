import {test} from "node:test";
import assert from "node:assert/strict";
import {readFileSync} from "node:fs";
import {resolve} from "node:path";
import {createRequire} from "node:module";
import React from "react";
import {renderToStaticMarkup} from "react-dom/server";
import ts from "typescript";
import * as requests from "../lib/client/requests";
import * as navigation from "../lib/domain/navigation";

type Node = {type: unknown; props: Record<string, unknown>};
type LoginComponent = (props: {initialError?: string}) => React.ReactElement;
const runtimeRequire = createRequire(resolve("package.json"));

/** Execute the real Login component; decorative children do not participate in these form contracts. */
function loadLogin(react: unknown): LoginComponent {
  const source = readFileSync(resolve("components/login.tsx"), "utf8");
  const code = ts.transpileModule(source, {compilerOptions: {module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true}}).outputText;
  const evaluatedModule: {exports: {Login?: LoginComponent}} = {exports: {}};
  const imports = (id: string): unknown => {
    if (id === "react") return react;
    if (id === "react/jsx-runtime") return runtimeRequire(id);
    if (id === "next/link") return {__esModule: true, default: "a"};
    if (id === "lucide-react") return new Proxy({}, {get: () => "svg"});
    if (id === "./art") return {Art: () => null};
    if (id === "./landing") return {Brand: () => null};
    if (id === "@/components/ui/button") return {Button: "button"};
    if (id === "@/components/ui/field") return {Field: "div", FieldError: "p", FieldGroup: "div", FieldLabel: "label"};
    if (id === "@/components/ui/input-group") return {InputGroup: "div", InputGroupAddon: "div", InputGroupButton: "button", InputGroupInput: "input"};
    if (id === "@/lib/client/requests") return requests;
    if (id === "@/lib/domain/navigation") return navigation;
    throw new Error(`Unexpected Login dependency: ${id}`);
  };
  new Function("require", "module", "exports", code)(imports, evaluatedModule, evaluatedModule.exports);
  assert.ok(evaluatedModule.exports.Login);
  return evaluatedModule.exports.Login;
}

function nodes(value: unknown): Node[] {
  if (Array.isArray(value)) return value.flatMap(nodes);
  if (!value || typeof value !== "object" || !("props" in value)) return [];
  const node = value as Node;
  return [node, ...nodes(node.props.children)];
}
function find(tree: unknown, predicate: (node: Node) => boolean): Node {
  const result = nodes(tree).find(predicate);
  assert.ok(result, "Expected form control is present");
  return result;
}
function text(value: unknown): string {
  if (typeof value === "string" || typeof value === "number") return String(value);
  if (Array.isArray(value)) return value.map(text).join("");
  if (value && typeof value === "object" && "props" in value) return text((value as Node).props.children);
  return "";
}

test("v6 Login server output uses POST and disables credential submission before hydration", () => {
  const Login = loadLogin(React);
  const html = renderToStaticMarkup(React.createElement(Login));
  const form = html.match(/<form\b[^>]*>/)?.[0];
  assert.match(form || "", /method="post"/);
  assert.match(form || "", /aria-busy="true"/);
  for (const name of ["email", "password"]) {
    const input = html.match(/<input\b[^>]*>/g)?.find(tag => tag.includes(`name="${name}"`));
    assert.ok(input);
    assert.match(input, /disabled=""/);
    assert.match(input, /value=""/);
  }
  const submit = html.match(/<button\b[^>]*>/g)?.find(tag => tag.includes('type="submit"'));
  assert.match(submit || "", /disabled=""/);
  assert.match(html, /Menyiapkan halaman/);
});

test("v6 Login hydration, duplicate guard and failed request retries preserve a POST-only credential flow", async t => {
  const hooks: unknown[] = [];
  let position = 0, hydrated = false;
  const mockReact = {...React,
    useState(initial: unknown) {
      const index = position++;
      if (!(index in hooks)) hooks[index] = typeof initial === "function" ? initial() : initial;
      return [hooks[index], (value: unknown) => {hooks[index] = typeof value === "function" ? value(hooks[index]) : value;}];
    },
    useRef(initial: unknown) {const index = position++; return hooks[index] ??= {current: initial};},
    useSyncExternalStore(_subscribe: unknown, client: () => boolean, server: () => boolean) {return hydrated ? client() : server();},
  };
  const Login = loadLogin(mockReact);
  const render = () => {position = 0; return Login({});};
  const event = {preventDefault() {}};
  const submit = (tree: unknown) => (find(tree, node => node.type === "form").props.onSubmit as (input: typeof event) => Promise<void>)(event);
  const change = (tree: unknown, id: string, value: string) => (find(tree, node => node.props.id === id).props.onChange as (event: {target: {value: string}}) => void)({target: {value}});
  const originalFetch = globalThis.fetch, windowDescriptor = Object.getOwnPropertyDescriptor(globalThis, "window"), redirects: string[] = [];
  const target = "/workspace?view=orders&queue=needs-pic";
  Object.defineProperty(globalThis, "window", {configurable: true, value: {location: {search: `?next=${encodeURIComponent(target)}`, assign: (url: string) => redirects.push(url)}}});
  t.after(() => {globalThis.fetch = originalFetch; if (windowDescriptor) Object.defineProperty(globalThis, "window", windowDescriptor); else Reflect.deleteProperty(globalThis, "window");});
  const email = `qa-${crypto.randomUUID()}@example.invalid`, password = crypto.randomUUID();
  const calls: {url: string; init?: RequestInit}[] = [];
  let rejectRequest: (error: Error) => void = () => {throw new Error("Request has not started");};
  globalThis.fetch = async (input, init) => {
    calls.push({url: String(input), init});
    return await new Promise<Response>((_resolve, reject) => {rejectRequest = reject;});
  };
  let tree = render();
  await submit(tree);
  assert.equal(calls.length, 0, "Even a programmatic pre-hydration handler cannot submit");
  hydrated = true; tree = render();
  assert.equal(find(tree, node => node.props.id === "email").props.disabled, false);
  change(tree, "email", email); change(tree, "password", password); tree = render();
  const first = submit(tree), duplicate = submit(tree);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "/api/auth/login");
  assert.equal(calls[0].init?.method, "POST");
  assert.deepEqual(JSON.parse(String(calls[0].init?.body)), {email, password});
  assert.ok(calls[0].init?.signal);
  assert.equal(find(render(), node => node.props.type === "submit").props.disabled, true);
  rejectRequest(new TypeError("Simulated offline")); await Promise.all([first, duplicate]); tree = render();
  assert.equal(redirects.length, 0);
  assert.match(text(tree), /Koneksi terputus atau terlalu lama/);
  assert.equal(find(tree, node => node.props.type === "submit").props.disabled, false);
  assert.equal(find(tree, node => node.props.id === "password").props.value, password);
  globalThis.fetch = async (input, init) => {calls.push({url: String(input), init}); return Response.json({error: "Email atau kata sandi tidak benar."}, {status: 401});};
  await submit(tree); tree = render();
  assert.equal(find(tree, node => node.props.id === "email").props["aria-invalid"], true);
  assert.equal(find(tree, node => node.props.id === "password").props["aria-invalid"], true);
  assert.equal(redirects.length, 0);
  globalThis.fetch = async (input, init) => {calls.push({url: String(input), init}); return Response.json({user: {role: "pic"}});};
  await submit(tree);
  assert.equal(calls.length, 3);
  assert.ok(calls.every(call => call.url === "/api/auth/login" && call.init?.method === "POST"));
  assert.deepEqual(redirects, [target]);
});
