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

test("the pilot hides catalog photo management, whose endpoints it does not serve (F-008)", async () => {
  const { catalogPhotoManagementAvailable } = await import("../src/lib/release-scope");
  assert.equal(catalogPhotoManagementAvailable(true), false);
  assert.equal(catalogPhotoManagementAvailable(false), true);
  for (const path of ["/api/v1/catalog/skus/1/primary-photo/query", "/api/v1/catalog/skus/1/photo-evidence", "/api/v1/catalog/primary-photos/operations/query", "/api/v1/catalog/photo-revalidations/operations/query"])
    assert.equal(isPilotApiPathAllowed(path), false, path);
});

test("the catalog page offers the photo button only where photo management is served (F-008)", async () => {
  const { readFileSync } = await import("node:fs");
  const source = readFileSync(new URL("../src/modules/catalog/CatalogAdministration.tsx", import.meta.url), "utf8");
  // The row buttons render through the workspace-kit Button since UI-PAGES-B; the condition is what this test pins.
  assert.match(source, /\{item\.active&&catalogPhotoManagementAvailable\(\)&&<(?:button|Button)\b[^>]*onClick=\{\(\)=>void choosePhoto\(item\.id\)\}>Foto/);
});

test("pilot buyers see the approved product photo: the request-only catalog loads it like the priced one (F-012)", async () => {
  const { readFileSync } = await import("node:fs");
  assert.ok(isPilotApiPathAllowed("/api/v1/catalog/variants/00000065-0000-4000-8000-000000000101/primary-photo/query"));
  const source = readFileSync(new URL("../src/modules/catalog/BuyerCatalog.tsx", import.meta.url), "utf8");
  assert.doesNotMatch(source, /requestOnly \? <div className=\{styles\.photoPlaceholder\}>/, "no request-only placeholder in place of the photo");
});
