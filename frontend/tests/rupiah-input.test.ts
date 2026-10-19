import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { parseRupiahInput, rupiahAmount } from "../src/lib/rupiah-input";
import { RupiahInput } from "../src/components/workspace-kit/rupiah-input";

const value = (text: string) => { const result = parseRupiahInput(text); assert.ok("value" in result, `${text}: ${"error" in result ? result.error : ""}`); return result.value; };
const refused = (text: string) => assert.ok("error" in parseRupiahInput(text), text);

// People type rupiah the Indonesian way (1.000.000,50), the API way (1000000.50) or pasted from elsewhere (1,000,000).
test("rupiah_input_resolves_each_separator_style", () => {
  assert.equal(value("12.50"), "12.50");
  assert.equal(value("1000.25"), "1000.25");
  assert.equal(value("1.000"), "1000");
  assert.equal(value("4,250,000"), "4250000");
  assert.equal(value("1.000,5"), "1000.5");
  assert.equal(value("Rp 4.250.000,00"), "4250000.00");
  assert.equal(value("IDR 4.250.000"), "4250000");
  assert.equal(value("1,234"), "1234");
  assert.equal(value("1,5"), "1.5");
  assert.equal(value("4.250.000"), "4250000");
  assert.equal(value("0"), "0");
  assert.equal(value("007"), "7");
  assert.equal(value("1,234.56"), "1234.56");
  assert.equal(value("1,005"), "1005");
});

test("rupiah_input_refuses_overflow_and_junk", () => {
  for (const text of ["", "  ", "1.2.3,4.5", "1.2.3", "-5", "12a", "1234567890123456", "1.23,4", "1,2,3.4,5", "12.345.6", "1.000,123", "Rp", ",5"]) refused(text);
  assert.match((parseRupiahInput("1.000,123") as { error: string }).error, /dua angka/);
});

test("the input shows what will be recorded or why it cannot be", () => {
  const ok = renderToStaticMarkup(createElement(RupiahInput, { id: "amount", label: "Jumlah transfer", value: "4.250.000", onChange() {} }));
  assert.match(ok, /<label for="amount">Jumlah transfer<\/label>/);
  assert.match(ok, /inputMode="decimal"/);
  assert.match(ok, /Akan dicatat: Rp4\.250\.000,00/);
  const bad = renderToStaticMarkup(createElement(RupiahInput, { id: "amount", label: "Jumlah transfer", value: "12a", onChange() {} }));
  assert.match(bad, /aria-invalid="true"/);
  assert.match(bad, /role="alert"/);
  assert.doesNotMatch(renderToStaticMarkup(createElement(RupiahInput, { id: "amount", label: "Jumlah", value: "", onChange() {} })), /role="alert"/);
});

test("rupiahAmount gives the canonical value, padded to two decimals where the API requires it", () => {
  assert.equal(rupiahAmount("1.000,5"), "1000.5");
  assert.equal(rupiahAmount("1.000,5", true), "1000.50");
  assert.equal(rupiahAmount("Rp 4.250.000", true), "4250000.00");
  assert.equal(rupiahAmount("12a", true), null);
});
