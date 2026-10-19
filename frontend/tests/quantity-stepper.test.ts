import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { QuantityStepper, stepQuantity } from "../src/components/workspace-kit/quantity-stepper";

// Valid package quantities are multiples of the increment and at least the minimum (orderQuantity's rule).
test("steps move between valid quantities only", () => {
  assert.equal(stepQuantity("", 3, 2, 1), "4", "empty starts at the lowest valid quantity");
  assert.equal(stepQuantity("abc", 1, 1, -1), "1");
  assert.equal(stepQuantity("4", 3, 2, 1), "6");
  assert.equal(stepQuantity("6", 3, 2, -1), "4");
  assert.equal(stepQuantity("4", 3, 2, -1), "4", "never below the lowest valid quantity");
  assert.equal(stepQuantity("5", 1, 2, 1), "6", "an off-increment value snaps to the next valid one");
  assert.equal(stepQuantity("5", 1, 2, -1), "4");
  assert.equal(stepQuantity("1000000", 1, 1, 1), "1000000", "never above the order limit");
  assert.equal(stepQuantity("12", 12, 12, -1), "12");
});

test("an optional maximum caps the steps and marks increase unavailable at the top", () => {
  assert.equal(stepQuantity("4", 2, 1, 1, 4), "4", "never above the maximum");
  assert.equal(stepQuantity("3", 2, 1, 1, 4), "4");
  assert.equal(stepQuantity("9", 2, 1, -1, 4), "4", "a typed value above the maximum steps down to it");
  const top = renderToStaticMarkup(createElement(QuantityStepper, { id: "q3", label: "Jumlah karton", value: "4", minimum: 2, increment: 1, maximum: 4, onChange() {} }));
  assert.match(top, /aria-label="Tambah jumlah karton" aria-disabled="true"/);
  assert.match(top, /aria-label="Kurangi jumlah karton"(?! aria-disabled)/);
});

test("the stepper keeps the field's label and names each button with it", () => {
  const html = renderToStaticMarkup(createElement(QuantityStepper, { id: "q1", label: "Jumlah kemasan 1", value: "4", minimum: 3, increment: 2, onChange() {} }));
  assert.match(html, /<label for="q1">Jumlah kemasan 1<\/label>/);
  assert.match(html, /aria-label="Kurangi jumlah kemasan 1"/);
  assert.match(html, /aria-label="Tambah jumlah kemasan 1"/);
  assert.match(html, /inputMode="numeric"/);
  const lowest = renderToStaticMarkup(createElement(QuantityStepper, { id: "q2", label: "Jumlah kemasan 2", value: "4", minimum: 3, increment: 2, onChange() {} }));
  assert.match(lowest, /aria-label="Kurangi jumlah kemasan 2" aria-disabled="true"/, "decrease is marked unavailable at the lowest valid quantity");
  assert.doesNotMatch(lowest, /aria-label="Kurangi jumlah kemasan 2"[^>]*disabled=""/, "not disabled: a focused disabled button drops keyboard focus");
});
