import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { contrastRatio, readTokens } from "../src/lib/design/contrast";

const tokens = readTokens(readFileSync("src/app/globals.css", "utf8"));
const token = (name: string) => {
  const value = tokens[name];
  assert.ok(value, `token --${name} is defined in globals.css :root`);
  return value;
};

test("contrast ratio follows WCAG relative luminance for hex and OKLCH colours", () => {
  assert.ok(Math.abs(contrastRatio("#000000", "#ffffff") - 21) < 0.01);
  assert.ok(contrastRatio("#087f8c", "#ffffff") >= 4.5);
  assert.ok(Math.abs(contrastRatio("oklch(1 0 0)", "oklch(0 0 0)") - 21) < 0.05);
});

// WCAG 2.2 AA: body text 4.5:1; focus indicators and UI boundaries 3:1.
const textPairs: Array<[string, string]> = [
  ["foreground", "background"], ["card-foreground", "card"], ["popover-foreground", "popover"],
  ["muted-foreground", "background"], ["muted-foreground", "card"], ["muted-foreground", "muted"],
  ["primary-foreground", "primary"], ["secondary-foreground", "secondary"], ["accent-foreground", "accent"],
  ["sidebar-foreground", "sidebar"],
  // text-primary / text-destructive are used as text colours on page and card surfaces.
  ["primary", "background"], ["primary", "card"], ["destructive", "background"], ["destructive", "card"],
];
for (const [fg, bg] of textPairs) {
  test(`text token --${fg} on --${bg} meets 4.5:1`, () => {
    const ratio = contrastRatio(token(fg), token(bg));
    assert.ok(ratio >= 4.5, `--${fg} on --${bg} is ${ratio.toFixed(2)}:1`);
  });
}

for (const bg of ["background", "card"]) {
  test(`focus ring --ring on --${bg} meets 3:1`, () => {
    const ratio = contrastRatio(token("ring"), token(bg));
    assert.ok(ratio >= 3, `--ring on --${bg} is ${ratio.toFixed(2)}:1`);
  });
}

for (const tone of ["neutral", "info", "warning", "success", "danger"]) {
  test(`status tone ${tone} text meets 4.5:1 on its badge surface`, () => {
    const ratio = contrastRatio(token(`tone-${tone}-foreground`), token(`tone-${tone}`));
    assert.ok(ratio >= 4.5, `--tone-${tone}-foreground on --tone-${tone} is ${ratio.toFixed(2)}:1`);
  });
}
