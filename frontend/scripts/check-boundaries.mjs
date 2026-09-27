import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const frontend = fileURLToPath(new URL("../", import.meta.url));
const source = join(frontend, "src");
const legacy = resolve(frontend, "../backend/legacy");
const server = join(source, "lib/server");

function collect(directory, files = new Map()) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) collect(path, files);
    else if (/\.(ts|tsx|js|mjs)$/.test(entry.name)) files.set(path, readFileSync(path, "utf8"));
  }
  return files;
}

function imports(path, content) {
  const kind = path.endsWith(".tsx") ? ts.ScriptKind.TSX : path.endsWith(".ts") ? ts.ScriptKind.TS : ts.ScriptKind.JS;
  const ast = ts.createSourceFile(path, content, ts.ScriptTarget.Latest, true, kind);
  const names = [];
  function walk(node) {
    if ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) && node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
      names.push(node.moduleSpecifier.text);
    }
    if (ts.isCallExpression(node) && node.arguments.length && ts.isStringLiteral(node.arguments[0]) &&
        (node.expression.kind === ts.SyntaxKind.ImportKeyword || ts.isIdentifier(node.expression) && node.expression.text === "require")) {
      names.push(node.arguments[0].text);
    }
    ts.forEachChild(node, walk);
  }
  walk(ast);
  const client = ast.statements.some(statement => ts.isExpressionStatement(statement) &&
    ts.isStringLiteral(statement.expression) && statement.expression.text === "use client");
  return { names, client };
}

function within(path, directory) {
  const name = relative(directory, path);
  return name === "" || name !== ".." && !name.startsWith(".." + "\\" ) && !name.startsWith(".." + "/") && !/^[A-Za-z]:/.test(name);
}

function resolveImport(from, name, files) {
  let base;
  if (name.startsWith("@/")) base = join(source, name.slice(2));
  else if (name.startsWith("@legacy/")) base = join(legacy, name.slice(8));
  else if (name.startsWith(".")) base = resolve(dirname(from), name);
  else return null;
  for (const candidate of [base, base + ".ts", base + ".tsx", base + ".js", base + ".mjs", base + ".json",
    join(base, "index.ts"), join(base, "index.tsx")]) {
    if (files.has(candidate) || within(candidate, legacy) || within(candidate, server)) return candidate;
  }
  return base;
}

function findViolations(files) {
  const parsed = new Map([...files].map(([path, content]) => [path, imports(path, content)]));
  const roots = [...parsed].filter(([path, info]) => {
    const name = relative(source, path).replaceAll("\\", "/");
    return /^((components|hooks|lib\/client|lib\/domain)\/|modules\/)/.test(name) || info.client;
  }).map(([path]) => path);
  const violations = [];
  const visited = new Set();
  function walk(path, chain) {
    if (within(path, legacy) || within(path, server)) {
      violations.push(chain.map(item => relative(frontend, item).replaceAll("\\", "/")).join(" -> "));
      return;
    }
    if (visited.has(path)) return;
    visited.add(path);
    const info = parsed.get(path);
    if (!info) return;
    for (const name of info.names) {
      const target = resolveImport(path, name, files);
      if (target) walk(target, [...chain, target]);
    }
  }
  for (const root of roots) walk(root, [root]);
  return violations;
}

// Negative fixtures prove that transitive imports, re-exports and future module folders are checked.
const fixture = (files) => new Map(Object.entries(files).map(([name, content]) => [join(source, name), content]));
assert.equal(findViolations(fixture({
  "components/client.tsx": '"use client"; import "@/lib/server/page-session";',
})).length, 1);
assert.equal(findViolations(fixture({
  "components/client.tsx": '"use client"; import "../shared/helper";',
  "shared/helper.ts": 'import "@legacy/server/repository";',
})).length, 1);
assert.equal(findViolations(fixture({
  "modules/orders/view.tsx": '"use client"; import "../../shared/bridge";',
  "shared/bridge.ts": 'export { loadState } from "@legacy/server/repository";',
})).length, 1);
assert.equal(findViolations(fixture({
  "components/safe.tsx": '"use client"; import "@/lib/utils";',
  "lib/utils.ts": 'export const safe = true;',
})).length, 0);

const violations = findViolations(collect(source));
if (violations.length) {
  console.error("Browser import boundary violations:\n" + violations.join("\n"));
  process.exitCode = 1;
} else console.log("Import boundaries passed (including indirect and re-export checks).");
