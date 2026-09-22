import {test} from "node:test";
import assert from "node:assert/strict";
import {canOpenPage, safeLoginDestination} from "../lib/domain/navigation";

test("customer continues only into the customer storefront", () => {
  for (const target of ["/shop?category=pantry", "/shop/kopi?sku=KOPI-P3", "/cart", "/checkout?mode=buy", "/account", "/account/orders/order_1"]) {
    assert.equal(safeLoginDestination(target, "customer"), target);
  }
  for (const target of [null, "/workspace", "/api/state", "//example.com", "/\\example.com", "/shop/../../api/state", "/account/users/admin", "https://example.com", "/shop/%2f%2fevil.com", "/checkout\n"]) {
    assert.equal(safeLoginDestination(target, "customer"), "/shop");
  }
  assert.equal(canOpenPage("customer", "dashboard"), false);
  assert.equal(canOpenPage("customer", "admin"), false);
  assert.equal(safeLoginDestination("/checkout", "staf"), "/workspace");
});
