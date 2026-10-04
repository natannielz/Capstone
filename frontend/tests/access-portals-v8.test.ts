import {test} from "node:test";
import assert from "node:assert/strict";
import {ROLES} from "../../backend/legacy/domain/accounts";
import {loginForDestination, safeLoginDestination} from "../src/lib/domain/navigation";

test("v8 legacy links select the matching portal and retain safe shopping or staff context", () => {
  for (const next of ["/shop?category=pantry", "/shop/kopi?sku=kopi-p3", "/cart", "/checkout?mode=buy", "/account", "/account/orders/order_1"]) {
    assert.equal(loginForDestination(next), `/customer/login?next=${encodeURIComponent(next)}`);
  }
  const next = "/workspace?view=admin";
  assert.equal(loginForDestination(next), `/staff/login?next=${encodeURIComponent(next)}`);
  for (const bad of [null, "", "https://evil.invalid", "//evil.invalid", "/\\evil.invalid", "/api/state", "/shop/../../api/state", "/customer/login", "/staff/login", "/checkout\n", "/accounting", "/shop/%2f%2fevil.invalid"]) {
    assert.equal(loginForDestination(bad), "/staff/login");
  }
});

test("v8 actual role governs post-login navigation in either portal", () => {
  for (const role of ROLES) {
    assert.equal(safeLoginDestination(null, role), role === "customer" ? "/shop" : "/workspace");
    assert.equal(safeLoginDestination("/customer/login", role), role === "customer" ? "/shop" : "/workspace");
    assert.equal(safeLoginDestination("/staff/login", role), role === "customer" ? "/shop" : "/workspace");
    assert.equal(safeLoginDestination("/checkout", role), role === "customer" ? "/checkout" : "/workspace");
  }
  assert.equal(safeLoginDestination("/workspace?view=admin", "customer"), "/shop");
  assert.equal(safeLoginDestination("/workspace?view=admin", "admin"), "/workspace?view=admin");
});
