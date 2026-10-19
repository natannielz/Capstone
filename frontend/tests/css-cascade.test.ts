import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync } from "node:fs";

const app = "src/app";
const globals = readFileSync(`${app}/globals.css`, "utf8");
const owned = ["globals.css", "foundation.css"];

// CSS outside a cascade layer outranks every layered rule, so unlayered files would override Tailwind/shadcn.
test("the root layout loads exactly one stylesheet", () => {
  const imports = readFileSync(`${app}/layout.tsx`, "utf8").match(/^import\s+["'][^"']+\.css["'];/gm) ?? [];
  assert.deepEqual(imports, ['import "./globals.css";']);
});

// Phase G retired the 24 versioned legacy stylesheets; element defaults now live in foundation.css's base layer.
test("globals.css fixes the layer order before any rule and the legacy layer is gone", () => {
  const firstStatement = globals.replace(/\/\*[\s\S]*?\*\//g, "").trim().split(";")[0];
  assert.equal(firstStatement.replace(/\s+/g, " "), "@layer theme, base, components, utilities");
  assert.match(globals, /@import\s+["']\.\/foundation\.css["'];/);
  assert.doesNotMatch(globals, /layer\(legacy\)|@layer legacy/);
  assert.deepEqual(readdirSync(app).filter(name => name.endsWith(".css")).sort(), [...owned].sort(), "no other global stylesheet in src/app");
});

test("foundation.css provides the element defaults and the shared classes the legacy files used to style", () => {
  const css = readFileSync(`${app}/foundation.css`, "utf8");
  assert.match(css, /@layer base\{[\s\S]*body\{[^}]*font-family:var\(--font-body\)/);
  assert.match(css, /:where\(a,button,input,select,textarea,summary\):focus-visible\{outline:3px solid var\(--ring\)/);
  for (const selector of [".unit-brand{", ".unit-brand-mark{", ".unit-brand--inverse{", ".empty-state{", ":is(th,td).numeric{", "[data-slot=dialog-content]{"])
    assert.ok(css.includes(selector), selector);
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
  for (const file of owned) assert.equal(importantOutsideReducedMotion(readFileSync(`${app}/${file}`, "utf8")), 0, file);
});

// A rule split mid-selector (".a td" newline ".b{...}") silently becomes a descendant selector.
test("no stylesheet line ends in a dangling selector fragment", () => {
  for (const file of owned) {
    readFileSync(`${app}/${file}`, "utf8").split("\n").forEach((raw, index) => {
      const line = raw.trimEnd(), trimmed = line.trim();
      if (!trimmed || trimmed.startsWith("/*") || trimmed.startsWith("*") || trimmed.endsWith("*/")) return;
      assert.match(line, /[{},;:]$/, `${file}:${index + 1} ends mid-selector: ${trimmed.slice(0, 80)}`);
    });
  }
});
