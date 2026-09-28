# Stock-count observations

Status: bounded implementation in progress on 28 September 2026. This is part of CAT02/F03 and preserves V05/V08 packaging facts. It is not acceptance of complete inventory, stock adjustment, conversion, reservation or valuation.

## Scope and ownership

One observation records a staff member's explicit count of one current package definition at one warehouse. It preserves the quantity entered, exact package contents, checked base-unit equivalent, actor and server time. An optional lot label is explicitly unverified. Zero is a valid observed count; absence of a record is never interpreted as zero. The baseline is `unavailable` until a validated stock ledger exists. Coverage is `single-observation`, not a completed stocktake.

The canonical location is FulfillmentWarehouse with positive organization, shop, warehouse and channel Long IDs. A current `stock.count` grant matching that location authorizes a new observation. Catalog management, buyer entitlement or publication does not grant stock authority. The active SKU, family and category must share one authoring owner containing the requested location. Counting retired definitions and cross-owner stock are later extensions requiring explicit contracts.

Inventory owns the immutable observation, audit, idempotency receipt and one outer Mongo transaction. Catalog owns same-session graph validation and the final write fences on mutable catalog heads. Neither module writes balances, movements, reservations, cost layers or journals in this slice. No business source-authority or costing policy is provisioned.

## Command and replay

`RecordCountAsync` takes the current actor, exact location, SKU ID, expected SKU revision, definition ID, 0..1,000,000 observed packages, optional bounded lot label, reason, correlation ID and idempotency key. Base units use checked integer arithmetic; names never determine pack contents. All public Long IDs retain the existing string serialization contract.

A new count, required audit and receipt commit together. The receipt operation is `inventory.stock-count.record`; it binds actor ID, provider issuer/subject, key and canonical request hash. Exact replay returns the original observation. Changed data or reason under the same key conflicts. Deliberate later observations use new keys and cannot overwrite earlier evidence. Unknown commit outcomes retain their original key for receipt recovery.

Recovery and replay reauthorize the stored warehouse under current `stock.count` before exposing facts. They do not require an already-completed observation's SKU to remain current or active. `stock.read` separately authorizes scoped detail and bounded list access. Current identities, membership, grants, scope topology and live sessions remain fenced through the final awaited work; expiry and cancellation are checked before the owning transaction returns.

## Catalog transaction adapter

`CatalogAuthoringService.WithStockCountDefinitionAsync` accepts the owning database/client/session, actor, canonical warehouse location, SKU, optional expected revision/definition and a trusted metadata-only callback. The active transaction must belong to the exact same Mongo client and database namespace. It returns only server-loaded SKU/family/category revisions, authoring owner, physical-item key, package kind/label, base unit, contents and immutable definition ID/version to the callback.

The callback cannot commit or perform external I/O. Catalog validates the complete graph, fences all mutable heads, exact actor/grants/membership/location nodes and session, then rechecks current time and cancellation. Any failure aborts the caller transaction even when an outer caller catches the exception; an unconfirmed abort invalidates the session handle while preserving the original failure. A returned snapshot is not reusable authority.

Inventory checks its actor-bound receipt before invoking Catalog for a new observation, so valid historical replay does not apply stale expected-head requirements. Nullable adapter expectations do not exempt new Inventory commands from requiring reviewed positive revision and definition inputs.

## Persistence and verification gates

Use a new immutable `stockCountObservations` collection and conditional validation of its shared audit/receipt extensions. Preserve existing shared collection validation and indexes. Draft v14 integration follows evidence v13; it cannot rewrite v1-v13 or invent successful migration history. Preflight malformed/orphan records, audit/receipt graph, duplicates, strict schema/index compatibility and ID counters before changing target DDL. Test resumed migrations under the real migration protocol after integration.

Required tests cover real Catalog/session setup; stock-count-only authority; zero/max/12/24-pack counts; unchanged physical collections; exact/conflicting/concurrent replay; audit rollback; uncertain commits; recovery after SKU retirement; cross-scope reads; identity binding; revocation and definition races; expiry after the last Mongo response; caught failures; and migration preflight/resume. Passing a pure helper or build is not service, API, browser or production acceptance.

Remaining CAT02 work includes validated physical lots/balances, source ownership and idempotency, reservations/releases, stocktakes and independent adjustment approval, expiry/quarantine, conversion with approved cost depletion/remainder rules, and the real accounting/source adapters. API, staff counting UI, pilot and production activation are separate acceptance gates.
