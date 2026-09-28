# Scoped staff stock-count workflow

This contract extends stock-count-observations.md for the next API and staff-screen boundary. It implements immutable observations; balances, stock adjustments, availability, costing, accounting and stocktake approval remain separate requirements.

## Staff experience

The route is /inventory/counts. Its navigation is visible when the current verified account has stock.count or stock.read. Server authorization remains authoritative on every request. Current-access may project those two capabilities without broadening the administrative permission-catalog guard.

A counter chooses an authorized, named warehouse and channel, then an explicit package from its current catalog choices. Names come from actual organization profiles. Raw numeric warehouse/SKU entry, guessed names and package contents inferred from labels are excluded. Show code, family name, package label, kind and contents before confirming a count.

Packages are required whole numbers from 0 through 1,000,000. Blank is not zero. Show the exact base-unit conversion from explicit package contents. The optional lot label is an unverified observation, not a recognized inventory lot. Require a bounded reason. Confirmation says this records one observation and does not establish a warehouse balance or available stock.

Count-only staff may submit and recover their own observation. History uses the separate stock.read permission. The screen clears dependent package selections when warehouse/search context changes, discards late responses, rechecks account/session deadlines, and retains the original frozen request/key after an uncertain result. A deliberate later observation receives a new key.

## Catalog choice contract

CatalogAuthoringService owns:

- StockWarehouseOptionsAsync(actor, purpose, audit, ct), returning named canonical warehouse options.
- CountablePackagesAsync(actor, location, offset, limit, search, audit, ct), returning bounded countable package choices.

StockCountChoicePurpose is a closed Count/History enum. Count requires stock.count; History requires stock.read. Unknown values deny. Countable package choices always require stock.count and never CatalogManage or buyer publication.

Each warehouse option contains location, organizationName, shopName, warehouseName and channelName. Expand effective authorized exact warehouses and active warehouse children of authorized shop scopes; preserve channel and validate actual topology. Deduplicate canonical scopes and bound results to 256. Do not fabricate labels when profiles are missing or malformed.

Each package option contains skuId, skuRevision, definitionId, definitionVersion, skuCode, familyName, packageLabel, packageKind, baseUnitCode and baseUnitsPerPackage. Include only exact warehouse ownership or the same organization/shop/channel shop ownership. Strict active SKU, family and category plus exact current immutable definition relations must be filtered before total and page. Revalidate returned graphs in the transaction and fence mutable heads, actual scope nodes, matched grants/memberships, identity, session and time through final awaited work. Historical observation reads do not require active current catalog selections.

## HTTP contract

All routes are under /api/v1/inventory/stock-counts and use verified application sessions, same-origin CSRF, no-store responses and X-Expected-Actor-Id for POST, including reads. IDs, positive revisions, totals and base-unit counts are canonical decimal strings. Package quantities and package contents are bounded JSON integers; no string coercion. Unknown fields and missing required fields deny.

| Method and suffix | Request | Response | Permission |
| --- | --- | --- | --- |
| POST /warehouses/query | purpose: inventory-stock-count or inventory-stock-history | items of named warehouse options | Purpose-specific stock.count or stock.read |
| POST /packages/query | purpose: inventory-stock-count, location, optional search, offset, limit | total and package-choice items | stock.count |
| POST / | location, skuId, expectedSkuRevision, expectedDefinitionId, packages, optional observedLotLabel, reason; Idempotency-Key header | public immutable observation | stock.count |
| POST /query | purpose: inventory-stock-history, location, offset, limit | total and public observation items | stock.read |
| POST /{id}/query | purpose: inventory-stock-history | public observation or JSON null when missing/hidden | stock.read |
| POST /operations/query | purpose: operation-recovery, operation exactly inventory.stock-count.record, key | committed receipt or not_confirmed with null result/time | stock.count |

Omit a trailing slash when registering the collection command path. Use the existing bounded offset 0..10,000 and limit 1..100. Search is bounded by the existing Catalog search contract. A stale expected SKU revision or definition conflicts and requires review of fresh package choices.

The public observation contains id, version ("1"), location, recordedAt, skuId, skuCode, definitionId, definitionVersion, packageLabel, packageKind, baseUnitCode, baseUnitsPerPackage, packages, baseUnits, observedLotLabel, reason, baseline ("unavailable") and coverage ("single-observation"). It excludes issuer, subject, countedBy, physicalItemKey, audit IDs, authoring owner, family/category provenance, request key and permission internals.

A committed receipt contains state ("committed"), result { id, version: "1" }, and recordedAt. The browser maps not_confirmed to null for its lookup-only adapter. Neither a missing receipt nor a permission/network failure means the count failed. There is no close, abandon, delete, update or automatic retry route.

## Implementation ownership and acceptance

Catalog choice reader: new Modules/Catalog/CatalogStockCountChoices.cs and Platform/Catalog/CatalogStockCountReads.cs with focused tests. A small shared graph-loader extraction may change CatalogStockCountDefinition.cs while retaining its existing callback transaction and fences.

API: new Api/Inventory/StockCountDtos.cs, StockCountEndpoints.cs and nullable-read OpenAPI adjustment; thin composition of existing StockCountService and the Catalog reader. Register DI/routes in Program.cs, add inventory to both expected-actor predicates, and extend only the current-access capability projection. Add /inventory/counts to the protected return-path allowlists when its screen exists. Regenerate both checked-in contracts using the repository tooling.

Frontend: new modules/inventory counting client, workflow and screen; app/inventory/counts/page.tsx; explicit stock navigation and operation-recovery routing. Reuse the existing session, CSRF, mutation ledger, reference-only journal and per-view response fencing. Use actual generated API types after the HTTP contract exists.

Acceptance requires real-session HTTP tests for count-only/read-only/cross-scope/actor/origin/CSRF, zero and missing packages, precise large IDs, explicit packaging, immutable same-key replay, conflicting replay, recovery without stock.read, preserved historical package facts, safe output fields and null reads. Choice tests cover join validity before pagination and final authority/catalog races. UI tests cover explicit zero, packaging review, stale selections/account replacement, lost responses, storage failure, expired sessions and keyboard/mobile usability. Passing the browser journal tests alone does not satisfy this boundary.
