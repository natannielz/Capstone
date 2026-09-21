import {test} from "node:test";
import assert from "node:assert/strict";
import {safeLoginDestination, canOpenPage} from "../lib/domain/navigation";
import {matchesOrderQueue, readOrderQuery} from "../lib/domain/order-views";
import {seedState} from "../lib/domain/seed";

test("login continuation preserves a permitted catalogue choice", () => {
  assert.equal(safeLoginDestination("/workspace?view=catalog&collection=rapat&product=kopi", "pic"), "/workspace?view=catalog&collection=rapat&product=kopi");
});

test("login continuation rejects external URLs and unavailable pages", () => {
  for (const target of ["https://example.com", "//example.com", "javascript:alert(1)", "/workspace/../api/state", "/workspace?view=admin", "/workspace?view=unknown"]) {
    assert.equal(safeLoginDestination(target, "pic"), "/workspace");
  }
  assert.equal(safeLoginDestination("/workspace?view=catalog", "staf"), "/workspace");
  assert.equal(canOpenPage("pic", "admin"), false);
  assert.equal(canOpenPage("admin", "admin"), true);
});

test("review queue exposes exactly the submitted orders", () => {
  const s = seedState();
  const result = s.orders.filter(order => matchesOrderQueue(s, order, "review"));
  assert.ok(result.length > 0);
  assert.deepEqual(result.map(order=>order.id).sort(), s.orders.filter(order=>order.status==="submitted").map(order=>order.id).sort());
});

test("invalid URL filter parameters recover to usable defaults", () => {
  const result = readOrderQuery(new URLSearchParams("queue=unknown&sort=unknown&orderPage=-99"));
  assert.equal(result.queue, "all");
  assert.equal(result.sort, "newest");
  assert.equal(result.page, 1);
});
