import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { StatusBadge } from "../src/components/workspace-kit/status-badge";

// Tone rules live in @layer components; shadcn Badge utilities (bg-secondary, text-secondary-foreground) sit in
// @layer utilities and always win, so a tone badge must not carry them or every status looks the same.
test("status badge carries its tone and no utility colour that would override it", () => {
  for (const [label, tone] of [["Ditolak", "danger"], ["Lunas", "success"], ["Menunggu tinjauan", "warning"], ["Status baru", "neutral"]] as const) {
    const html = renderToStaticMarkup(createElement(StatusBadge, { text: label }));
    assert.match(html, new RegExp(`data-tone="${tone}"`), label);
    assert.match(html, /class="status-badge"/, label);
    assert.doesNotMatch(html, /bg-|text-secondary|rounded-full/, label);
  }
});

test("every tone badge renders through the one StatusBadge", () => {
  for (const file of ["src/components/workspace.tsx", "src/components/order-experience.tsx"]) {
    const source = readFileSync(file, "utf8");
    assert.doesNotMatch(source, /<Badge[^>]*status-badge/, `${file} renders its own Badge-based status`);
    assert.match(source, /StatusBadge/, file);
  }
});
