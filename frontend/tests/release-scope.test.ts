import { test } from "node:test";
import assert from "node:assert/strict";
import { isMaintainedEntryAlias, isPilotApiPathAllowed, isPilotPathAllowed } from "../src/lib/release-scope";

test("pilot exposes catalog, requests and required account maintenance only", () => {
  for (const path of ["/", "/shop", "/shop/package-1", "/identity/login", "/ordering/requests", "/ordering/requests/incoming", "/administration/catalog", "/operations/health"]) assert.ok(isPilotPathAllowed(path), path);
  for (const path of ["/checkout", "/cart", "/customer/login", "/staff/login", "/account", "/billing/invoices", "/catalog/prices", "/procurement/funding", "/operations/anomalies", "/shop/../checkout", "/shop/%2e%2e", "//shop"]) assert.equal(isPilotPathAllowed(path), false, path);
  for (const path of ["/api/v1/identity/session", "/api/v1/catalog/variants/query", "/api/v1/ordering/requests", "/api/v1/ordering/requests/1/query", "/api/v1/operations/health/query"]) assert.ok(isPilotApiPathAllowed(path), path);
  for (const path of ["/api/login", "/api/v1/ordering/drafts", "/api/v1/ordering/requests-other", "/api/v1/billing/payments", "/api/v1/catalog/prices", "/api/v1/inventory/counts", "/api/v1/operations/anomalies/latest", "/api/v1/operations/health-other"]) assert.equal(isPilotApiPathAllowed(path), false, path);
});

test("legacy bookmarks may reach redirect pages without reopening legacy APIs", () => {
  for (const path of ["/login", "/customer/login", "/staff/login", "/account", "/account/orders", "/account/orders/9007199254741001", "/cart", "/checkout"])
    assert.equal(isMaintainedEntryAlias(path), true, path);
  for (const path of ["/api/auth/login", "/api/state", "/api/commands", "/api/catalog", "/account/profile", "/account/orders/1/extra", "/account/orders/%2f", "//login", "/checkout/extra"])
    assert.equal(isMaintainedEntryAlias(path), false, path);
});
