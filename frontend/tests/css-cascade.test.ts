import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";

const app = "src/app";
const globals = readFileSync(`${app}/globals.css`, "utf8");
const legacyFiles = readdirSync(app).filter(name => name.endsWith(".css") && !["globals.css", "foundation.css"].includes(name));

// CSS outside a cascade layer outranks every layered rule, so unlayered legacy files override Tailwind/shadcn.
test("the root layout loads exactly one stylesheet", () => {
  const imports = readFileSync(`${app}/layout.tsx`, "utf8").match(/^import\s+["'][^"']+\.css["'];/gm) ?? [];
  assert.deepEqual(imports, ['import "./globals.css";']);
});

test("globals.css fixes the layer order before any rule and imports every legacy file into the legacy layer", () => {
  const firstStatement = globals.replace(/\/\*[\s\S]*?\*\//g, "").trim().split(";")[0];
  assert.equal(firstStatement.replace(/\s+/g, " "), "@layer theme, base, legacy, components, utilities");
  for (const file of legacyFiles) assert.match(globals, new RegExp(`@import\\s+["']\\./${file.replace(".", "\\.")}["']\\s+layer\\(legacy\\);`), file);
  assert.match(globals, /@import\s+["']\.\/foundation\.css["'];/);
});

// Inside a low layer !important flips to the strongest; only reduced-motion resets (which must beat inline animation styles) keep it.
function importantOutsideReducedMotion(css: string): number {
  let count = 0; const stack: boolean[] = []; let prelude = "";
  for (let index = 0; index < css.length; index++) {
    const char = css[index];
    if (char === "{") { stack.push(/prefers-reduced-motion/.test(prelude) || stack.at(-1) === true); prelude = ""; }
    else if (char === "}") { stack.pop(); prelude = ""; }
    else if (char === ";") prelude = "";
    else prelude += char;
    if (css.startsWith("!important", index) && stack.at(-1) !== true) count++;
  }
  return count;
}

test("no !important outside prefers-reduced-motion blocks", () => {
  for (const file of [...legacyFiles, "globals.css", "foundation.css"])
    assert.equal(importantOutsideReducedMotion(readFileSync(`${app}/${file}`, "utf8")), 0, file);
});
