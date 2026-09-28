# Catalog, Packaging and Inventory Report Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every sellable package an explicit, durable identity; let buyers deliberately choose it; preserve its agreed definition through fulfillment and returns; and keep sealed, loose and externally sourced stock consistent under concurrency and retries.

**Architecture:** Extend the existing C# modular backend. Catalog owns descriptive masters, identities, immutable definitions, prices and identifier mappings. Inventory alone writes physical stock and reservations. Ordering consumes server-validated catalog snapshots and reserves stock in the same transaction. Procurement, fulfillment, documents and returns consume those snapshots rather than reconstructing packages from current product names. Next.js uses generated API contracts and one shared selection controller across purchasing entry points.

**Tech Stack:** Existing .NET 10/ASP.NET Core, official MongoDB driver and replica-set transactions; Next.js/React/TypeScript; generated OpenAPI client; xUnit with real Mongo integration tests; existing frontend Node test runner; browser/accessibility verification coordinated with OPS04/OPS05.

**Spec:** [Report requirements and acceptance map](../../requirements/unit-toko-report-2026-09-28.md), [production specification](../../requirements/unit-toko-production.md), [umbrella execution plan](2026-09-28-report-completion.md), and original [T06–T08, T12 and T15](2026-09-27-unit-toko-production.md). This subplan refines those tasks; it does not create a second inventory implementation.

## Global Constraints

- Planning baseline: commit `e84faa6`. This document does not claim application changes, passing new tests, production data import or external provisioning. Preserve other agents' changes and completed Foundations, authorization, identity, transaction, idempotency and migration work.
- Launch remains BNI branches and approved business accounts. November dates are references, not release deadlines. Retain `frontend/`, `backend/` and `docs/` responsibilities.
- Internal IDs remain positive Int64/BSON Long and cross JSON as decimal strings through `LongId`. Versions and potentially large balance/base-unit counts also cross JSON as strings. Package command quantities use the existing bounded integer contract, `1..1,000,000`. Use checked arithmetic; no JavaScript `Number` conversion of IDs or Int64 totals. Money uses exact decimal strings and existing bounds, with T05A-approved rounding.
- New public catalog references use UUIDv4, an adopted implementation choice from the report's optional recommendation. They do not replace Long keys or authorization. SKU, public reference, GTIN, supplier/source code, lot and any optional serial number remain different fields.
- Never allocate a plausible-looking GTIN. Preserve legitimate supplied identifiers and their provenance. When GS1 allocation is required, its authorized owner performs it outside this implementation. Missing GTIN does not block an otherwise valid internal SKU.
- Keep one authoritative writer per physical stock pool. Until OPS02 records the OMI/S-Mart authority, reconciliation and freshness policy, that pool cannot produce a new order promise. Source channel is distinct from category; S-Mart also contains water products.
- No quantity inference from a name, ID, image, barcode prefix, search score or language model. A search suggestion is not buyer consent or an approved source mapping.
- Existing catalog and SKU indexes are preserved. Add versioned migrations with preflight validation; never rewrite `FoundationMigration.cs` or silently drop an existing uniqueness constraint.
- Shared files (`Program.cs`, permissions, migration registry, generated schema and legacy adapters) have one integration owner. Agree a short edit/build window with that owner before modifying them. Read `frontend/AGENTS.md` and applicable installed Next.js documentation before UI route changes.
- Modules contain pure policies with explicit time inputs and no Mongo or Platform clock types. Platform owns transaction orchestration and reuses authorization actor/scope fences, current authority, idempotency receipts and durable outbox patterns. Catalog access must implement ACC02's entitled-catalog policy, not accidentally reuse buyer-owned order scope.
- Do not enable a standalone catalog authority switch while legacy business commands still interpret old product IDs/prices. Use the reviewed bridge or coordinated business-cohort cutover from T15/OPS06. No live data, secrets, real supplier files or demo seeds enter the repository.

## Review Focus

1. A URL, search match or first available option silently becomes a package choice: CAT03 `Unselected_entry_points_never_add_or_buy` and keyboard/mobile browser scenarios.
2. A 12-unit package becomes 10 in old stock or documents, or conversion creates value/quantity: CAT01 `Material_package_change_requires_new_sku`, CAT02 `Conversion_preserves_quantity_cost_and_provenance`, CAT03 `Historical_snapshot_survives_master_changes`.
3. A reservation races conversion/expiry/count, or a cashier echo reduces stock twice: CAT02 `Last_stock_race_has_one_winner`, `Source_echo_does_not_repeat_web_movement`, and rollback/replay cases.
4. Leading zeros, source ambiguity, catalog scope or an identifier collision resolve to the wrong SKU: CAT01 identifier races; CAT04 purpose-specific unknown/ambiguous/cross-scope scan cases.
5. Migration invents pack contents or loses historical references: CAT04 deterministic import/rejection/reconciliation tests and OPS06's coordinated cutover gate.

---

## Verified starting point and ownership

The storefront selects a representative product at `frontend/src/components/shop/storefront.tsx:67` and directly adds it at lines 135–138. `shop-data.ts:85–100` chooses an exact SKU match or a first matching/available product; it does **not** establish that the current implementation always chooses the cheapest. Product-detail selection is initialized from the URL (`shopping-pages.tsx:130`), while Buy Now submits the selected product's ID, quantity and price. The staff catalog has a separate direct-add path (`frontend/src/components/catalog.tsx:606`). All must migrate together.

Family inference and packaging regexes remain in `frontend/src/lib/domain/catalog.ts:44,64–84`; the legacy Product model lacks explicit family/contents/version/GTIN fields (`backend/legacy/domain/model.ts:5`). Legacy SKU uniqueness already exists (`backend/legacy/db/schema.ts:8`), as do random UUID product IDs and stock/price checks. Existing `minimum` is a **reorder threshold**, not minimum order quantity (`frontend/src/components/inventory-workspace.tsx:41`). Checkout already detects changed prices; extend that behavior rather than removing it.

Foundation Mongo collections and `skus(organizationId,sku)` uniqueness already exist (`backend/src/UnitToko.Platform/Mongo/Migrations/FoundationMigration.cs:13,57`). They are not complete Catalog/Inventory modules. Current documents resolve product names/units from current master data (`backend/legacy/server/documents.ts:10–12`); prices alone do not preserve package identity. New production modules below are deliberate additions.

| Task | Responsibility | Dependency / handoff |
| --- | --- | --- |
| CAT01 | Catalog masters, immutable definitions/prices, identifiers and scoped search | Existing T02/T05; ACC02 catalog-entitlement contract; Documents image eligibility from T05A/PAY01 |
| CAT02 | Physical package balances, conversion, reservations, source freshness, stocktakes and replenishment baseline | CAT01; T05A exact valuation; ACC02 narrow stock permissions; jointly define the source-authority contract early with OPS02, without waiting for OPS02 completion |
| CAT03 | Explicit selection, server quote/submit checks and immutable line snapshots | CAT01; CAT02 for promises; publish contracts before OPS01/OPS02 consume them; join their integration tests for final acceptance |
| CAT04 | Operational scanning, deterministic migration and end-to-end acceptance | CAT01–CAT03 for resolver/preview; join OPS01/OPS02 operational tests, OPS05 browser checks and OPS06 final cutover acceptance |

Requirements owned here: **V01–V10, I01–I10, F01**, Catalog/Inventory portions of **F03/F04**, **M06** code/fuzzy suggestions and **M08** threshold/lead-time baseline. OPS02 owns external cashier adapters/acknowledgments and broad procurement/finance workflows. OPS01/OPS02 own downstream documents and return commands; CAT03 supplies a mandatory shared snapshot and integration tests. Demand forecasting and embeddings remain conditional enhancements outside these four tasks.

## Shared data and integration contract

### Catalog visibility producer handoff

ACC02.3 supplies the policy, strict v6 entitlement/assortment persistence and metadata-read service described in [catalog visibility](../../architecture/catalog-visibility.md). Reuse those contracts; do not rebuild them or widen BuyerOwned order authority. Current acceptance is recorded separately from this still-open Catalog module.

CAT01 must publish SKUs using bounded `catalogListings` tuples of assortment ID and canonical fulfillment owner scope. **Every addition, removal or move must atomically increment all affected assortment versions in the same transaction as the SKU change.** Add real writer/read races proving withdrawn or moved publications cannot leak through list/count/detail, and preserve SKU identity when one SKU is listed in multiple assortments.

Implement governed entitlement issuance only after the D02 audience/approver decisions are resolved. Extend the metadata service through explicit product-definition/price projections with their own authorization and version rules; no current metadata reference grants a price tier or file capability. Establish immutable public UUID database uniqueness and needed query indexes without altering applied migrations. Product-image/export/event delivery must reauthorize at delivery and enforce PAY01's immutable document eligibility.

### Identity, definition and price boundaries

Implement the following records in the named domain files under `UnitToko.Modules.Catalog`; use immutable collections for stored attributes. `LongId` and `ActorContext` already exist. Domain `long` revisions receive strict string DTO adapters; the DTO layer must not change domain types into floating point.

```csharp
// ProductFamily.cs: OrganizationId is the catalog-owning fulfillment organization.
public sealed record ProductFamily(LongId Id, LongId OrganizationId, Guid PublicId,
    string Name, string CategoryCode, string Description,
    ImmutableArray<LongId> ImageAssetIds, bool Active, long Revision);

// Sku.cs: physical compatibility is explicit, not inferred from FamilyId.
public sealed record Sku(LongId Id, LongId OrganizationId, LongId FamilyId,
    Guid PublicId, string Code, string PhysicalItemKey, bool Active,
    LongId CurrentDefinitionId, long Revision);

// VariantDefinitionVersion.cs: all persisted rows are immutable.
public sealed record VariantDefinitionVersion(LongId Id, LongId SkuId, long Version,
    string PackageLabel, string PackageKind, string BaseUnitCode,
    int BaseUnitsPerPackage, ImmutableSortedDictionary<string, string> Attributes,
    ImmutableArray<LongId> ImageAssetIds, int MinimumOrderPackages,
    int OrderIncrementPackages, DateTimeOffset EffectiveAt);

// PriceVersion.cs: no mutable current-price row replacing historical prices.
public sealed record PriceVersion(LongId Id, LongId SkuId, long Version,
    string Currency, decimal UnitPrice, DateTimeOffset EffectiveAt);

// CatalogQueries.cs: scope comes from authenticated, validated context.
public sealed record CatalogContext(LongId BuyerOrganizationId, LongId BranchId,
    LongId? DivisionId, LongId FulfillmentOrganizationId, LongId ShopId,
    LongId WarehouseId, string Channel);
public sealed record CatalogSearch(string Text, string? CategoryCode,
    string? Cursor, int Limit);
```

Use `PhysicalItemKey` only as a reviewed equality key within the owning organization for the same base item and intrinsic size/color/material. It never grants authority. `PackageKind` is a controlled internal code (`loose`, `sealed`, `other`); display labels remain editable Indonesian data. This baseline handles discrete counted contents. Mixed bundles or measured goods require a separately reviewed composition/unit contract and cannot be flattened into an invented integer conversion.

The chosen rule for a material contents, intrinsic size/color, base-unit or physical-item change is a **new sellable SKU, public UUID and initial definition**, linked to the predecessor. Old stock and history keep the old identity. A label/image/order-rule correction can create a new immutable definition on the same SKU; existing accepted snapshots remain unchanged. A price change creates a PriceVersion only. Changing “Dus” from 12 to 10 is never an in-place balance reinterpretation. Applicable GTIN changes follow the GS1 packaging rules, independently of local SKU generation.

SKU code is canonical uppercase ASCII, trimmed, 1–64 characters from letters/digits/dot/underscore/hyphen. Retain existing `(organizationId, sku)` uniqueness by storing this canonical code in `sku`; retain the submitted display form in audit if needed. Preflight legacy collisions before enforcing the canonical format. Names, labels and descriptions remain Unicode. Codes are not recycled after retirement; correction creates a new primary code with a permanent alias to the same identity, and the alias stays reserved.

`CatalogIdentifier.cs` defines an immutable record containing `Id`, `OrganizationId`, `SkuId`, `Kind` (`skuCode`, `gtin`, `supplier`, `omi`, `smart`), `Namespace`, `RawValue`, `CanonicalValue`, `IsPrimary`, `EffectiveAt`, nullable `RetiredAt`, `Revision`, and `ProvenanceReference`. SKU primary codes and aliases both use `kind=skuCode` in one reservation namespace so an alias cannot collide with another SKU's primary code. External namespaces include the precise source tenant/supplier. Registry uniqueness is `(organizationId, kind, namespace, canonicalValue)` including retired reservations. Corrections append immutable assignment history and update the current reserved-code projection transactionally. Public UUID indexes are globally unique within their collection. Within one catalog namespace, a GTIN must resolve to one physical sellable identity; multiple supplier offers point to that identity rather than duplicating GTIN-to-SKU matches. Two independently scoped catalogs can legitimately reference the same manufacturer's GTIN.

`Gtin.Parse(string)` returns `Gtin(Raw, Canonical14)` after accepting ASCII digits of length 8, 12, 13 or 14, preserving the input zeros, checking the alternating 3/1 check digit, then left-padding for comparison. Reject whitespace inside digits, Unicode lookalikes and numeric JSON input. Syntactic validity is not allocation/authenticity. Store the supplied length as well as canonical form. GS1 describes 14-digit normalization in [its identifier guidance](https://support.gs1.org/support/solutions/articles/43000734386-are-all-gtins-unique-numbers-); its [GTIN Management Standard, release 1.1, §2.8](https://ref.gs1.org/standards/gtin-management/) requires a changed case count to have a new case-level GTIN. Use existing legitimate manufacturer identifiers; this plan does not authorize allocation or registration.

### Inventory and source authority boundaries

`StockContracts.cs` defines `StockLocation(OrganizationId, ShopId, WarehouseId, Channel)` using LongId ID fields and string Channel; `StockBalanceKey(Location, SkuId, DefinitionId, LotId)`; and `StockBalance(Key, OnHand, Reserved, Held, Expired, Revision)` using long counts. Held/expired buckets are mutually exclusive classified quantities; reservations select eligible lots only. Available is eligible on-hand minus reservations, never a second physical balance. Views explain the buckets; age/expiry transitions cannot double-subtract a unit classified into a different bucket. A purely descriptive definition revision does not strand physical stock: an explicit compatibility check permits allocation from an older definition only when SKU, physical-item identity, base unit and exact contents match. Preserve the actual lot's definition alongside the accepted sales snapshot. Material changes never satisfy this compatibility rule.

`StockAuthority.cs` defines:

```csharp
public enum StockAuthorityMode { Unconfirmed, WebLedger, ExternalLedger }
public sealed record SourceStockPolicy(LongId Id, StockLocation Location,
    long Revision, StockAuthorityMode Mode, TimeSpan MaximumAge,
    long SafetyBufferBaseUnits, string ExternalNamespace);
public sealed record SourceStockObservation(LongId PolicyId, long PolicyRevision,
    long Revision, DateTimeOffset ObservedAt, DateTimeOffset? ReconciledAt,
    bool Reconciled, string SourceBatchReference);
public sealed record AvailabilitySnapshot(long AvailablePackages,
    long Revision, DateTimeOffset ObservedAt, DateTimeOffset? ReconciledAt,
    bool CanPromise, string ReasonCode, long SourcePolicyRevision);
```

OPS02 must supply a reviewed policy **per physical pool/channel**, specifying whether OMI and S-Mart pools overlap, which system records physical movement, how external reservations are acknowledged, feed interval/maximum age, safety buffer, discrepancy owner and handoff. Missing policy, a revision mismatch, stale/unreconciled feed or unacknowledged external reservation yields `CanPromise=false`; show a dated “availability needs confirmation” state rather than claiming zero or live stock. `ExternalLedger` cannot become synchronously sellable merely because a fresh file arrived: require a proven external hold/acknowledgment protocol or an exclusively allocated web-owned pool. Otherwise accept only an explicitly unconfirmed request under OPS01, not a stock promise.

OPS02 imports carry `(sourceSystem, sourceTenant, externalTransactionId, externalLineId, eventKind, payloadHash)`, plus nullable `originWebOperationId` and external acknowledgment reference. Preview is read-only. A changed payload under the same external key conflicts; identical replay is inert. The accepted external effect invokes Inventory in the same transaction as deduplication/mapping/audit. A web-originated sale returning as an external echo acknowledges/reconciles the original event; it does not dispatch the stock a second time. No adapter writes `stockBalances` directly. Catalog quote and order submit both check the same current source policy/observation; frontend age indicators alone are insufficient.

### Accepted package snapshot

`OrderLineSkuSnapshot.cs` is a Catalog-owned immutable value contract shared by Ordering, Fulfillment, Billing and AfterSales. It contains `FamilyId`, `SkuId`, `PublicId`, `SkuCode`, `FamilyName`, `DefinitionId`, `DefinitionVersion`, `PackageLabel`, `PackageKind`, `BaseUnitCode`, `BaseUnitsPerPackage`, immutable selected attributes, `PriceVersionId`, `PriceVersion`, `Currency`, `UnitPrice`, `OrderedPackages`, `OrderedBaseUnits`, source channel and the accepted source-mapping revision. All values are server-produced. Link document lines to that accepted snapshot and to their **actual** picked/delivered/accepted/returned package and base-unit quantities; never invoice the requested quantity merely because it appears in the order snapshot.

For partial loose returns from a sealed package, the return command refers to the original accepted SKU/definition and explicitly records returned base units and condition. Inventory inspection/quarantine and a reviewed conversion/disposition determine later stock identity. A later 10-unit package definition cannot change how a returned historical 12-unit package is interpreted. This contract does not independently set return entitlement or finance rounding policy.

## CAT01 — Persist catalog masters, stable variant identity and versioned definitions

> CAT01.1 pure SKU/immutable package-definition rules are now locally accepted with94/94 domain tests and independent source review. [Evidence and exact limits](../../quality/report-catalog-acceptance.md) distinguish this prerequisite from persistence, authorization, identifier reservation, pricing, images, UI and full CAT01 acceptance, which remain open.


### CAT01.2 — Persisted staff authoring workflow (locally accepted)

The scoped Mongo/API/editor workflow is locally accepted. [Detailed evidence and limits](../../quality/catalog-authoring-acceptance-2026-09-28.md) record 381 distinct passing selected backend checks, the frontend/browser results, immutable ownership and same-owner category reclassification, and the remaining catalog/production gates. This does not complete full CAT01 or any production acceptance row.

Deliver a real staff page backed by Mongo: choose a currently permitted shop/channel or warehouse/channel, create category and family, explicitly enter 12-unit and 24-unit variants, reload/edit them, and review replacement or retirement. These quantities are test examples, never defaults inferred from a package label. Keep all full CAT01 obligations below; a saved draft is not buyer publication or purchase availability.

- Persist one canonical immutable authoring owner on category/family/SKU aggregates, separate from publication tuples. Resolve its current hierarchy and catalog.manage permission on the server. A SKU inherits its family's owner; changing category or definition cannot transfer it. Family category changes require an active category with the exact same owner and preserve the original audit snapshots. Reuse existing shop-to-warehouse scope containment without widening a warehouse grant.
- Add permitted-owner selectors and owner-scoped authoring queries before count/page/joins. Draft editing must work with zero buyer entitlements/assortments. Existing directory presentation options are not authority; compile the current complete permission clauses and recheck lifetimes after final awaited work.
- Preserve publicId as the existing canonical UUID string and add matching standard BSON UUID bytes with global uniqueness and exact equality checks. Keep the existing organization/SKU uniqueness index and permanent primary-code reservations shared with future identifier corrections. Applied migrations remain unchanged; reject incomplete legacy masters before any new DDL rather than infer contents or ownership.
- Make publication explicit. Load every old/new assortment and canonical owner inside one transaction; require management permission on the master and all affected owners, then atomically change listings and every affected assortment epoch. Retirement must withdraw all listings under the same authorization because existing metadata reads do not rely on the active flag alone. Define and test any family-wide deactivation before exposing it.
- Ship protected API, generated client and responsive editor together, including exact revisions, actor checks, same-key retry and scoped receipt lookup. Do not copy person-governance closure powers into catalog. Buyer definition joins require current entitlement/assortment access in the same transaction and never turn metadata references into image/price capabilities.
- Prove the persisted staff flow, simultaneous identity/code creation, scope isolation and final-await rollback, publication/read races, immutable revisions/replacement, strict migration rejection/resume, and desktop/mobile/keyboard use. Preserve legacy reader-defense fixtures at their explicit schema version and add complete current-schema fixtures; do not weaken production uniqueness to accommodate old synthetic duplicates.

Images remain unavailable until PAY01 eligibility; reject supplied image IDs/URLs in this boundary. Price/availability remain absent until their own policies and authorities exist. D02 still gates real buyer audiences, pricing and entitlement issuance. The authoring slice seeds no role grants, assortments, entitlements or product data. Root owns shared composition/contracts/fixture changes; separate storage, service and UI/API owners coordinate the schema and build windows.

**Requirements:** F01; I01–I07/I09; V03–V05/V08; M06 baseline. **Depends on:** T05 foundation and ACC02's catalog entitlement contract; reuse T05A/PAY01's approved image-asset contract.

**Files to create:**

- `backend/src/UnitToko.Modules/Catalog/ProductFamily.cs`, `CatalogCategory.cs`, `Sku.cs`, `VariantDefinitionVersion.cs`, `PriceVersion.cs`, `CatalogIdentifier.cs`, `Gtin.cs`, `CatalogCommands.cs`, `CatalogQueries.cs`, `CatalogPolicy.cs`.
- `backend/src/UnitToko.Platform/Catalog/CatalogRepository.cs`, `CatalogCommandService.cs`, `CatalogQueryService.cs`, `ProductMatchService.cs`.
- `backend/src/UnitToko.Platform/Mongo/Migrations/CatalogMigration.cs`, `CatalogSchemas.cs`.
- `backend/src/UnitToko.Api/Catalog/CatalogEndpoints.cs`, `CatalogContracts.cs`.
- `frontend/src/modules/catalog/catalog-client.ts`, `catalog-editor.tsx`, `catalog-editor.module.css`, `catalog-editor-state.ts`, `README.md`; `frontend/src/app/administration/catalog/page.tsx`.
- `backend/tests/UnitToko.Tests/Catalog/CatalogPolicyTests.cs`, `CatalogIdentityTests.cs`, `CatalogPersistenceTests.cs`, `CatalogQueryTests.cs`, `ProductMatchTests.cs`; `frontend/tests/catalog-editor.test.ts`.

**Files to modify through the integration owner:** `backend/src/UnitToko.Platform/Mongo/CollectionMigrations.cs`, `backend/src/UnitToko.Api/Program.cs`, `backend/contracts/openapi.json`, `frontend/src/api/generated/schema.ts`. Register the next unused migration version at implementation time; freeze prior migration implementations. Shared permission changes belong to ACC02, not this task.

**Command interfaces:** Declare these pure input records in `CatalogCommands.cs`. `FamilyInput` and `DefinitionInput` contain editable data; IDs, current revision numbers, actor and timestamps remain server-produced. `CatalogCategory.cs` stores `(LongId Id, LongId OrganizationId, string Code, string Name, bool Active, long Revision)` and enforces category ownership when assigning a family.

```csharp
public sealed record FamilyInput(string Name, string CategoryCode, string Description,
    ImmutableArray<LongId> ImageAssetIds);
public sealed record DefinitionInput(string PackageLabel, string PackageKind,
    string BaseUnitCode, int BaseUnitsPerPackage,
    ImmutableSortedDictionary<string, string> Attributes,
    ImmutableArray<LongId> ImageAssetIds, int MinimumOrderPackages,
    int OrderIncrementPackages);
public sealed record SaveCategory(LongId OrganizationId, LongId? CategoryId,
    long ExpectedRevision, string Code, string Name, bool Active);
public sealed record CreateFamily(LongId OrganizationId, FamilyInput Values);
public sealed record EditFamily(LongId FamilyId, long ExpectedRevision, FamilyInput Values);
public sealed record CreateSku(LongId FamilyId, long ExpectedFamilyRevision,
    string Code, string PhysicalItemKey, DefinitionInput Definition);
public sealed record ReviseDefinition(LongId SkuId, long ExpectedRevision,
    DefinitionInput Definition);
public sealed record CreateReplacementSku(LongId PredecessorSkuId,
    long ExpectedRevision, string Code, string PhysicalItemKey,
    DefinitionInput Definition, string Reason);
public sealed record CorrectSkuCode(LongId SkuId, long ExpectedRevision,
    string NewCode, string Reason);
public sealed record SetPrice(LongId SkuId, long ExpectedRevision,
    string Currency, decimal UnitPrice, DateTimeOffset EffectiveAt);
public sealed record MapIdentifier(LongId SkuId, long ExpectedRevision,
    string Kind, string Namespace, string RawValue, string ProvenanceReference);
public sealed record RetireSku(LongId SkuId, long ExpectedRevision, string Reason);
public sealed record CatalogCommandResult(LongId Id, long Revision,
    LongId? PredecessorSkuId = null);
```

`CatalogCommandService` exposes one `<InputName>Async` method per input record above, each returning `Task<CatalogCommandResult>` and taking `(ActorContext actor, InputName input, AuthorizationCommandMetadata metadata, CancellationToken token)`. The documented `InputName` correspondence is mechanical: for example, `SetPriceAsync(ActorContext actor, SetPrice input, AuthorizationCommandMetadata metadata, CancellationToken token)`. New category uses `CategoryId=null, ExpectedRevision=0`; editing requires a positive existing revision. Family/category edits cannot reparent the catalog owner. Replacement creation does not silently retire the predecessor's remaining stock. Identifier correction uses a new versioned assignment/history and retained reservation; it cannot overwrite a used code onto another SKU.

**Query interfaces:** `CatalogQueryService.SearchAsync(actor, CatalogContext, CatalogSearch, token)` returns `CatalogPage(ImmutableArray<CatalogFamilyView> Items, string? NextCursor)`; each `CatalogFamilyView` contains the authorized ProductFamily and `ImmutableArray<CatalogVariantView>`, whose fields are Sku, current VariantDefinitionVersion, nullable current PriceVersion and nullable AvailabilitySnapshot. A missing price/availability blocks purchase without inventing zero. Direct public-ID/SKU lookups use the same policy. Limit 1–100, query text ≤128 characters, allowlisted sorts and opaque cursor tied to scope/filter. Search ranks exact SKU/GTIN/source code, prefix, then bounded normalized text distance; it returns labeled candidates and reasons, never an approved mapping or inferred pack contents.

The first GTIN red test can be expressed without a database (the numbers below are validation fixtures, not production allocations):

```csharp
[Theory]
[InlineData("4006381333931", "04006381333931")]
[InlineData("04006381333931", "04006381333931")]
public void Leading_zero_forms_compare_canonically(string raw, string canonical)
{
    var result = Gtin.Parse(raw);
    Assert.Equal(raw, result.Raw);
    Assert.Equal(canonical, result.Canonical14);
}

[Fact]
public void Incorrect_check_digit_is_rejected() =>
    Assert.Throws<DomainValidationException>(() => Gtin.Parse("4006381333932"));
```

- [ ] **Write failing domain tests first.** Assert two products both labeled “Dus” can contain 12 and 24 respectively; MOQ/increment differs from reorder minimum; an unrelated same-name family cannot merge; intrinsic size/color or 12→10 changes require a replacement SKU; label correction preserves old definitions; retiring a SKU does not release its codes. Add GTIN vectors including a standard check-digit example, invalid last digit, all four accepted lengths, leading-zero canonical equivalence, Unicode digits and numeric JSON rejection. Tests use documented synthetic/standard examples and never present them as allocated production codes.
- [ ] **Run the red test slice.** From the repository root, run `.tools/dotnet10/dotnet.exe test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter FullyQualifiedName~UnitToko.Tests.Catalog`. Confirm the missing domain behavior causes failure; record the actual result rather than claiming tests ran during planning.
- [ ] **Implement pure validation and immutable revisions.** Separate sale rules from replenishment rules. Define valid effective-price ordering and reject overlapping/current-pointer races. Make SKU retirement reject new orders while preserving authorized historical reads. Persist predecessor links for replacement variants and require explicit reviewed source/GTIN mapping for the replacement; never copy a retired GTIN onto a changed pack automatically.
- [ ] **Write failing persistence and authorization cases.** Race same canonical SKU/public UUID/GTIN/source-code creation; primary-code versus alias collision; simultaneous price changes; direct public-ID substitution from another catalog; list/count/search/export pagination with inaccessible families; malformed BSON fields; and stale actor/scope/grant revisions. Verify active catalog entitlement is distinct from order ownership. Current SKU uniqueness and Foundations tests must still pass.
- [ ] **Implement repository/schema/index changes.** Preserve `productFamilies`, `skus`, `priceVersions`; add `catalogCategories`, `variantDefinitions`, `catalogIdentifiers` and immutable identifier-assignment history. Use BSON Long IDs/revisions, standard BSON UUID subtype 4 with canonical lowercase UUID strings at the API, explicit schema versions, strict validators and immutable histories. Index `catalogCategories(organizationId,code)`, `variantDefinitions(skuId,version)`, `priceVersions(skuId,version)`, catalog-scoped family/search fields and the identifier keys above. Preflight before stronger validators/unique indexes; fail with a bounded rejected-row report instead of rewriting data. Use per-aggregate revision predicates, current authorization fences and same-key command receipts.
- [ ] **Implement staff editing and image association.** Authorized staff can create a complete family and variants without seed metadata; enter descriptions/categories/attributes, per-variant pictures, labels/contents, minimum packages/increment, prices and explicit supplier/source mappings. Attach only authorized eligible asset IDs through the Documents image pipeline; do not accept arbitrary server-fetch URLs or require image upload to create a draft. Display retirement/replacement and effective-date effects before submission. Retain uncertain-result idempotency and expected-actor behavior from existing administration clients.
- [ ] **Test M06 suggestions before implementing them.** Exact code outranks a similar name; mixed source namespaces remain separate; ambiguous labels show all authorized candidates; “Dus” never yields a guessed 12; a suggested mapping needs user confirmation; typo ranking is deterministic and bounded. Implement normalized code lookup plus bounded token/prefix/edit-distance matching behind `ProductMatchService`. Use no embeddings, model runtime or generated package data in this baseline.
- [ ] **Run green verification and independent review.** Re-run the Catalog test slice and `npm --prefix frontend test` after implementing editor-state tests. Regenerate OpenAPI through the existing export/generation scripts, then run typecheck and contract drift checks. Reviewer examines uniqueness under concurrency, namespace/source boundaries, immutable definitions and absence of seed/name inference in the new path. Record evidence in `docs/quality/report-catalog-acceptance.md`; fix findings before marking CAT01 done.

**Acceptance:** A staff member creates a new family and two variants entirely in the UI, with different pictures and exact contents. Two simultaneous duplicate-code requests cannot both succeed. Buyers entitled to the catalog can find it, while another organization cannot bypass access with its UUID. Old definitions and prices remain readable after corrections.

## CAT02 — Make package inventory, conversion and availability authoritative

**Domain checkpoint, 28 September 2026:** Exact quantity transitions, physical/historical conversion rules and source-authority preconditions pass 123 affected tests; see [the implemented contract](../../architecture/stock-authority-contract.md) and [verification](../../quality/inventory-policy-verification-2026-09-28.md). The conversion signature now includes SKU/current/stored definition facts, and location uses the existing numeric channel identity. Derived ledger quantities are Int64; user-entered steps remain bounded. Persistence, costs, API/UI and source/pilot acceptance are not completed by these pure rules; the encompassing tasks below remain open.

**Storage/provenance checkpoint, 28 September 2026:** The private Mongo balance layer and persisted pool/lot graph checks now pass127 focused cases with real-Mongo rollback/concurrency coverage; see [verification](../../quality/inventory-provenance-verification-2026-09-28.md). This adds canonical pool ownership, lot-to-package binding, bounded ancestry/expiry validation and a pool revision fence. Governed source/lot creation, final schema/preflight, movements/costs and API/UI remain open; no migration or stock writer is activated.

**Movement transaction checkpoint, 29 September 2026:** The private source-owned ledger now records exact before/after effects atomically and rejects duplicate source events;175 focused tests pass, including real-Mongo approval races, semantic storage constraints and downstream accounting rollback. See [verification](../../quality/inventory-movement-verification-2026-09-29.md). Genuine source services, valuation/cost conservation, full migration and usable command/UI acceptance remain open; the encompassing tasks below are not closed by this component.

**Requirements:** V05/V09; F03/F04 Inventory boundary; M08 baseline. **Depends on for implementation:** CAT01; T05A valuation/rounding; ACC02 explicit conversion/release/count-adjustment permissions. Prepare the source-authority policy/contract jointly with the OPS02 owner before integration; CAT02 can implement and test unconfirmed/accepted policy states without waiting for all of OPS02. Actual order promises remain disabled for an affected pool until its authority/reconciliation decision is accepted. This is an activation gate, not a circular completion dependency.

**Files to create:**

- `backend/src/UnitToko.Modules/Inventory/StockContracts.cs`, `StockAuthority.cs`, `StockMovement.cs`, `PackConversionPolicy.cs`, `ReservationPolicy.cs`, `StocktakePolicy.cs`, `ReplenishmentPolicy.cs`.
- `backend/src/UnitToko.Platform/Inventory/InventoryRepository.cs`, `PackConversionService.cs`, `ReservationService.cs`, `StocktakeService.cs`, `AvailabilityService.cs`, `ReplenishmentService.cs`.
- `backend/src/UnitToko.Platform/Mongo/Migrations/InventoryMigration.cs`, `InventorySchemas.cs`.
- `backend/src/UnitToko.Api/Inventory/InventoryEndpoints.cs`, `InventoryContracts.cs`.
- `frontend/src/modules/inventory/inventory-client.ts`, `conversion-workbench.tsx`, `stocktake-workbench.tsx`, `replenishment-list.tsx`, `inventory.module.css`, `README.md`; `frontend/src/app/administration/inventory/page.tsx`.
- `backend/tests/UnitToko.Tests/Inventory/PackConversionTests.cs`, `InventoryConcurrencyTests.cs`, `ReservationTests.cs`, `SourceAuthorityTests.cs`, `StocktakeTests.cs`, `ReplenishmentTests.cs`; `frontend/tests/inventory-workflows.test.ts`.
- `docs/architecture/stock-authority-contract.md`, jointly reviewed by the OPS02 owner; `docs/quality/report-catalog-acceptance.md` if not yet created.

**Integration files:** Registry/Program/OpenAPI/generated schema under the same single-owner procedure as CAT01. Preserve the foundation `lots`, `stockBalances`, `movements`, `reservations` collections. OPS02 owns its import adapter files and calls this service; it must not add a parallel balance writer.

**Conversion contract** (`PackConversionPolicy.cs`):

```csharp
public sealed record PackConversionDefinition(LongId Id, long Version,
    LongId SourceSkuId, LongId SourceDefinitionId,
    LongId DestinationSkuId, LongId DestinationDefinitionId,
    int SourcePackagesPerStep, int DestinationPackagesPerStep);
public sealed record ConvertPackages(LongId DefinitionId, long DefinitionVersion,
    StockBalanceKey Source, long ExpectedSourceRevision,
    LongId? DestinationLotId, long ExpectedDestinationRevision,
    int Steps, string Reason);
public sealed record ConversionQuantities(long SourcePackages,
    long DestinationPackages, long BaseUnits);
public static class PackConversionPolicy
{
    public static ConversionQuantities Quantities(PackConversionDefinition definition,
        VariantDefinitionVersion source, VariantDefinitionVersion destination, int steps);
}
```

Platform `PackConversionService.ConvertAsync(ActorContext actor, ConvertPackages command, AuthorizationCommandMetadata metadata, CancellationToken token)` returns `(LongId ConversionId, LongId DestinationLotId, long SourceRevision, long DestinationRevision)`. Destination SKU/definition/location derive from the approved conversion and source location; they are not arbitrary input. Existing destination lot/revision must match provenance. `DestinationLotId=null, ExpectedDestinationRevision=0` requests a server-created child lot; its unique conversion-origin reference prevents replay allocation, and the unique full balance key protects create races. The child lot references the source lot with the same expiry and physical origin; the client cannot assert an unrelated lot. Default one source lot per operation; no implicit commingling. Same organization/location/channel/physical-item identity and equal base-unit quantities are required. Transfers between warehouses or source channels use a separate workflow, not conversion.

`ReservationService` exposes transaction-aware `ReserveAsync`, `ReleaseAsync`, `DispatchAsync`, `ReceiveAsync` called only by their owning order/fulfillment/procurement workflows. Their input always carries operation/source event ID, actor/scope, SKU+definition, package quantity, eligible lot allocation and expected revisions; output carries reservation/movement IDs and resulting revisions. `AvailabilityService.GetAsync(actor, context, skuId, now, token)` produces the shared availability snapshot. Expiry and reservation timeout use explicit server time and the specified Jakarta business-date policy, not client time or test-wall-clock dates.

- [ ] **Write failing conservation tests.** For one sealed 12-unit carton and no loose balance, an explicit conversion step yields source −1 and destination +12. Define a second “Dus” of 24 to prove ratios are product-specific. Fail incompatible physical item/size/color, mismatched definitions, zero/negative/fractional/excessive steps, checked overflow, stale conversion revision, different channel/warehouse and a client-selected unrelated lot. Test reverse packing only with a separately approved conversion definition and valid packaging condition; “mathematically divisible” is not proof a package is sealed.
- [ ] **Run red inventory tests.** Run `.tools/dotnet10/dotnet.exe test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter FullyQualifiedName~UnitToko.Tests.Inventory`. Capture the expected failing conversion/reservation cases before implementation.
- [ ] **Implement pure conversion and valuation rules.** Validate each definition's base quantities, then use checked long multiplication: `sourcePackages = steps * sourcePackagesPerStep`, `destinationPackages = steps * destinationPackagesPerStep`; require `sourcePackages * source.BaseUnitsPerPackage == destinationPackages * destination.BaseUnitsPerPackage`. No name-based lookup. Retain exact total cost-layer value across conversion. A total cost of 100.00 split into 12 units cannot round every unit independently and silently lose value: use T05A's deterministic remainder allocation, consuming the residual exactly on final depletion. The finance-approved cost policy is a dependency; do not invent more currency precision or silently discard the remainder.
- [ ] **Write failing real-Mongo transaction tests.** Inject failure between source decrement and destination increment: both balances/movements/audit must roll back. Retry a committed response with the same key: no new movement. Retry a changed request with the same key: conflict. Race conversion versus reservation, dispatch, count adjustment and a second conversion; no negative stock or double cost. Include destination create races, grant revocation during conversion, expired/held/reserved source stock and one winning buyer for the last available units.
- [ ] **Implement transaction orchestration.** Re-read source/destination definition and conversion versions, authorization and active hierarchy in the same unit of work; fence actor/scopes and all affected balance revisions. Only unreserved eligible source stock can convert. Commit linked immutable movements, cost provenance, resulting balances, command receipt and outbox event together. Release/expiry is idempotent and never changes physical on-hand. Dispatch consumes the correct reservation and physical stock once; receive writes actual counted/inspected quantities under Procurement's command. Quarantine/expiry reclassifies eligibility without creating sellable units.
- [ ] **Port independently reviewed stocktakes.** Count captures location/lot/SKU definition and balance revision; a different authorized checker reviews proposed deltas and actual actor/person independence from ACC03. A intervening receive/reserve/dispatch/convert forces rebase and review, not a blind overwrite. Counted sealed and loose packages are separate rows. Reason, evidence references and immutable before/after movement remain linked. Show shortages as an owned exception, not an automatic negative balance or hidden substitution.
- [ ] **Write source-authority tests before availability integration.** Cover missing/unconfirmed policy, feed older than maximum age, policy revision changed mid-submit, unreconciled imported totals, overlapping channels, external stock without a hold protocol and a web sale echoed back by a cashier. Assert no promise/reservation for unavailable authority, visible observation age, and no repeated physical effect for a replay/echo. Test different payload under the same source event key conflicts. Include total reconciliation `opening + receipts - all channel dispatches ± approved adjustments = physical` by pool and lot.
- [ ] **Implement and review the OPS02 boundary.** Write the authority document with concrete per-pool owner, mode, source namespace, freshness ceiling, safety buffer, acknowledgment protocol and discrepancy behavior. Synthetic fixtures may exercise both modes; they do not prove actual OMI/S-Mart capability. Enable `CanPromise` only for accepted authority/reconciliation. Source policy/observation versions participate in order-submit validation and transactional fences; stale cached availability cannot authorize a sale. Pass reconciliation/acknowledgment work back to OPS02 with the exact interfaces above.
- [ ] **Write M08 red tests, then implement a reviewed replenishment list.** Keep existing reorder threshold and add per-SKU/source/location lead time, safety stock, explicitly entered or transparently computed historical daily demand, verified open supply and its expected arrival. Suggest `max(0, ceiling(demandPerDay * leadTimeDays) + safetyStock - inventoryPosition)` rounded to the chosen purchase-pack increment; show threshold alerts even when demand history is absent. Inventory position includes usable stock plus eligible due supply minus committed demand, avoiding subtracting reservations twice. Missing lead time/history, unreconciled stock or late supply shows an explainable warning, not a fabricated forecast. Record inputs, observation time and rule version; human approval creates procurement work. No automatic PO, seasonal prediction, trained model or embedding is included.
- [ ] **Verify green and obtain independent review.** Run the Inventory test slice against a real replica set, then inventory controller/client tests and generated-contract checks. Reviewer checks conservation, one-writer ownership, cost remainder, active scopes, stale source policy, idempotent expiry, count-maker independence and import echoes. Record results and remaining source/business gates; do not describe synthetic-source results as external acceptance.

**Acceptance:** One carton of 12 converts once into 12 loose units with exact conserved cost and traceable lot/expiry. Competing reservation/conversion commands cannot oversell. A stale OMI feed visibly blocks its promises without suppressing unrelated validated S-Mart inventory. Replenishment proposals explain their inputs and await human action.

## CAT03 — Require explicit selection and preserve accepted package snapshots

**Requirements:** V01–V03/V06–V08/V10; I09 production-path removal; T07/OPS01 integration. **Depends on for initial implementation:** CAT01 and CAT02 before live stock promises. CAT03 publishes the selection/quote/snapshot contract first and can run standalone contract/controller tests before OPS01. OPS01/OPS02 then consume it; their downstream integration is a joint final acceptance gate, not a circular prerequisite to beginning CAT03.

**Files to create:**

- `backend/src/UnitToko.Modules/Catalog/OrderLineSkuSnapshot.cs`, `CatalogQuote.cs`; `backend/src/UnitToko.Platform/Catalog/CatalogQuoteService.cs`.
- `frontend/src/modules/catalog/selection-state.ts`, `PackageSelector.tsx`, `package-selector.module.css`, `catalog-quote-client.ts`, `snapshot-label.ts`.
- `backend/tests/UnitToko.Tests/Catalog/CatalogQuoteTests.cs`, `backend/tests/UnitToko.Tests/Ordering/PackageSubmissionTests.cs`, `backend/tests/UnitToko.Tests/Ordering/PackageSnapshotIntegrationTests.cs`.
- `frontend/tests/package-selection.test.ts`, `package-quote-client.test.ts`, `package-snapshot.test.ts`; `docs/quality/catalog-browser-scenarios.md`.

**Files to modify:** `frontend/src/components/shop/storefront.tsx`, `shopping-pages.tsx`, `checkout-page.tsx`, `shop-data.ts`; `frontend/src/components/catalog.tsx`; `frontend/src/lib/client/customer-cart.ts`; `frontend/src/lib/domain/catalog.ts`; `frontend/src/app/api/catalog/route.ts`; CAT01 Catalog API contract/endpoints. Coordinate legacy adapter retirement with OPS06 rather than creating a half-migrated production path. Through the named OPS01/OPS02 owners, modify/create `backend/src/UnitToko.Modules/Ordering/Order.cs`, `SubmitOrder.cs`, `backend/src/UnitToko.Modules/Fulfillment/Shipment.cs`, `backend/src/UnitToko.Modules/Billing/Invoice.cs`, `backend/src/UnitToko.Modules/Fulfillment/AfterSalesCase.cs` and their transaction/document adapters to consume the snapshot. These integration names are the agreed target files, not claims that the modules already exist; reconcile any established owner filenames before touching their code.

**Quote contract:**

```csharp
// CatalogQuote.cs
public sealed record CartSelection(LongId SkuId, LongId DefinitionId,
    long DefinitionVersion, LongId PriceVersionId, long PriceVersion, int Packages);
public sealed record CatalogQuoteRequest(CatalogContext Context,
    ImmutableArray<CartSelection> Lines);
public sealed record CatalogQuoteLine(OrderLineSkuSnapshot Snapshot,
    AvailabilitySnapshot Availability);
public sealed record CatalogQuote(Guid QuoteId, DateTimeOffset ExpiresAt,
    ImmutableArray<CatalogQuoteLine> Lines, string ConfirmationDigest);
public sealed record CatalogChange(LongId SkuId, string Code,
    CartSelection Expected, CartSelection? Current);
```

`CatalogQuoteService.QuoteAsync(actor, request, token)` returns `Task<CatalogQuote>` after reading current authoritative definitions, prices, source mappings and availability. Store a five-minute confirmation digest bound to actor, scope and complete canonical line contents in `catalogQuotes` with an expiry index; TTL cleanup is not the validity check. Quote is not a stock hold. `ValidateForSubmitAsync(IClientSessionHandle session, ActorContext actor, Guid quoteId, string confirmationDigest, ImmutableArray<CartSelection> selections, CancellationToken token)` returns `Task<ImmutableArray<OrderLineSkuSnapshot>>` and runs inside Ordering's transaction. It checks current scope, definition/price/source-policy validity, quantities and versions, or throws a structured catalog conflict carrying the safe `CatalogChange` list. Mutable SKU/current-price pointers and policy projections receive transactional fences; merely reading immutable version rows does not serialize a concurrent master change. The client supplies selected IDs/counts and an opaque confirmation reference, never an authoritative price or ratio. A current price/definition change cannot be bypassed by fabricating a client acceptance boolean. Price changes, contents/attributes, minimum/increment, source/channel or a required quantity change invalidate confirmation. A cosmetic update follows the documented material-change policy without rewriting an accepted order; an expired quote always requires revalidation.

UI `SelectionState` is a discriminated union in `selection-state.ts`: `closed`, `choosing` (family plus pending intent `add`/`buyNow`), `chosen` (explicit CartSelection), `quoting`, `reviewChanges`, `submitting`, `unknownOutcome`, `complete`. `open(intent,family)` starts with no selection; `choose(variant)` records a deliberate event; `cancel()` cannot emit an add/submit; `confirm()` requires valid choice/quantity and a current quote. No URL-loaded ID, display preference, exact search match or cached first result sets `chosen`. A still-valid explicit choice on the detail page can proceed without a redundant dialog. Even a single-option family requires deliberate choice if no choice exists; there is no hidden exception based on option count.

- [ ] **Write failing selection tests across entry points.** Quick-add, search, detail-page initial URL, Buy Now and staff catalog call the same controller and cannot mutate cart before choice. Cover first/cheapest/first-available variants, a one-option family, unavailable selected variant, two families with a “Dus” label, cancellation at every state and returning focus to the correct initiating control. Saved/repeat/bulk selections retain an explicit previously chosen SKU only after displaying/revalidating its current definition; unresolved legacy cart rows reopen choice instead of silently converting names.
- [ ] **Run red frontend tests.** Add the new `*.test.ts` files to the existing auto-discovered Node suite and run `npm --prefix frontend test`. Fail on the missing controller behavior, not CSS snapshots. Record the failing case names. In parallel, write/run the backend `CatalogQuoteTests` slice for stale price/definition, bad quantity increment, another actor's quote, branch substitution and unknown/inactive SKU.
- [ ] **Implement the accessible selection flow.** Display each package's image, package label, contents, relevant size/color, price per package, stock unit, source age and total base quantity (`packages × stored contents`). Keep nothing preselected until an explicit click/keyboard choice. Use the installed accessible dialog primitive with a name, description, contained tab focus, Escape/close cancellation and restored trigger focus; do not implement read/scroll tracking. At 360px and desktop, the choice, quantity, totals and confirmation remain reachable. Explain out-of-stock/stale-source states without substituting a different package. Follow the [W3C modal dialog pattern](https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/) and existing design tokens; review real browser behavior, not just attributes.
- [ ] **Implement quote and final transaction checks.** Quote services use server IDs/versions, the current entitled catalog and CAT02 freshness. Final submission re-reads and fences all affected definitions/prices/policy/balances and reserves with order creation atomically; uniqueness/idempotency prevents duplicate order creation. Return `409 catalog_changed` with safe, scoped difference codes and a newly reviewable quote; the buyer must affirm changes before a new logical submit. Return clear quantity/stock validation when demand cannot be fulfilled. Do not silently downsize quantities or move to another pack/source.
- [ ] **Implement resilient client confirmation.** Preserve exact immutable request content and idempotency key on unknown outcome, display pending recovery and check operation receipt before offering a new command. Changed content or confirmed completed/failed operation gets a new key; refreshed UI alone does not. Version conflict refreshes and requires review; 503 with uncertain commit retries the same key. Identity/context changes clear actor-owned selection/quote state, fence late responses and use expected-actor headers. A stale browser tab cannot submit its quote as a newly signed-in person.
- [ ] **Write failing snapshot integration tests before downstream changes.** Accept “Dus 12”, then change label/image/price and create “Dus 10”. Cart/checkout current state shows the pending difference; already accepted order, picking list, delivery note, invoice and return still show the original “Dus 12” contents, identity and price. Partial shipment/invoice uses actual quantities with the original per-package meaning. Historical lookup does not require an active SKU. Return 3 base units from an original 12-unit package: case remains tied to the original snapshot and quarantine, never 3 new 10-unit packages. Missing historical contents remain flagged as unknown until reviewed migration, not inferred at read time.
- [ ] **Wire snapshots with OPS01/OPS02.** Server-generated `OrderLineSkuSnapshot` is copied/referenced immutably through order, pick, dispatch, delivery acknowledgment, invoice and return. Substitution uses a newly confirmed snapshot while preserving the requested one and the buyer's consent. Stop reading current catalog names/units for issued documents. Remove ID-prefix/name parsing from the new production path and retire or explicitly isolate legacy-only helpers/routes in the coordinated cutover; no new modules read whole legacy state.
- [ ] **Run green checks and independent UI/backend review.** Run focused quote/submission/snapshot xUnit tests, frontend tests, typecheck/lint and generated client checks. Execute `docs/quality/catalog-browser-scenarios.md` against real frontend→API→Mongo fixture data through the approved browser harness, with keyboard, Escape, screen-reader naming, mobile layout, stale price and lost-response paths. Reviewer confirms every entry point, honest stock age, no implicit consent and immutable downstream labels. OPS04/OPS05 owns any shared Playwright installation/config; this plan does not pretend it is already installed.

**Acceptance:** From every purchasing path the buyer deliberately selects “Dus berisi 12”, sees 2 packages = 24 base units and confirms the current price. A later change forces review for an unsubmitted cart, while an accepted transaction keeps its original package snapshot through invoice and return. Closing the chooser never adds anything.

## CAT04 — Resolve scans safely and rehearse catalog/stock migration

**Requirements:** I05/I06/I08/I10, V06/V08/V09 end-to-end; M06 operational matching. **Depends on for resolver/preview implementation:** CAT01–CAT03. Resolver, preview and synthetic rehearsal tests can finish before OPS06 begins final cutover. Operational integration joins OPS01/OPS02; only the real-data import/cutover acceptance requires OPS06's authorization and reconciliation gate.

**Files to create:**

- `backend/src/UnitToko.Modules/Catalog/ScanContracts.cs`, `backend/src/UnitToko.Platform/Catalog/ScanResolver.cs`, `backend/src/UnitToko.Api/Catalog/ScanEndpoints.cs`.
- `frontend/src/modules/inventory/scan-state.ts`, `ScanInput.tsx`, `scan-client.ts`.
- `backend/src/UnitToko.Platform/Migration/LegacyCatalogMapping.cs`, `CatalogMigrationPreview.cs`, `CatalogMigrationImporter.cs`; `backend/tests/UnitToko.Tests/Migration/CatalogMigrationTests.cs`.
- `backend/tests/UnitToko.Tests/Catalog/ScanResolverTests.cs`, `backend/tests/UnitToko.Tests/Inventory/OperationalScanTests.cs`; `frontend/tests/operational-scan.test.ts`.
- `backend/tests/Fixtures/catalog-packaging-migration.json`; `docs/runbooks/catalog-packaging-migration.md`; extend `docs/quality/report-catalog-acceptance.md` and `docs/quality/catalog-browser-scenarios.md`.

**Integration files:** CAT01 identifier API/contracts, CAT02 inventory API contracts; OPS01 receiving/picking/dispatch and CAT02 stocktake UIs use `ScanInput`. OPS06 owns importer execution/cutover orchestration; this task supplies a side-effect-free preview and transaction-safe importer library. Do not create a public HTTP endpoint that can import arbitrary live historical data.

**Scanning contract** (`ScanContracts.cs`):

```csharp
public enum ScanPurpose { Receiving, Picking, Dispatch, Stocktake }
public enum ScanResolutionKind { Match, Unknown, Ambiguous, Retired, Invalid }
public sealed record ScanRequest(ScanPurpose Purpose, StockLocation Location,
    string RawCode, string? Symbology, string? SourceNamespace,
    LongId? DocumentId, long? DocumentRevision);
public sealed record ScanCandidate(LongId SkuId, LongId DefinitionId,
    string SkuCode, string PackageLabel, int BaseUnitsPerPackage,
    string MatchReason, bool Active);
public sealed record ScanResolution(ScanResolutionKind Kind,
    ImmutableArray<ScanCandidate> Candidates, string ReasonCode);
```

`ScanResolver.ResolveAsync(actor, request, token)` is read-only and uses current scope/context plus CAT01 mappings. Scanner symbology/context distinguishes explicit internal SKU namespaces from GTIN and supplier codes; unknown format asks for a code type rather than interpreting any digit string as a GTIN. GS1 carrier parsing, if enabled, must use tested application-identifier handling for GTIN/lot/expiry and validate those values; an ordinary keyboard scanner without metadata uses explicit namespace/context. Resolution never receives/dispatches/counts stock on its own. The operational command carries document/line revision, explicit quantity and separate scanned lot/expiry validation, then rechecks current mapping/SKU/authorization before commit.

**Migration contract** (`LegacyCatalogMapping.cs`): immutable `LegacyCatalogMapping(SourceSystem, EntityType, LegacyId, ImporterVersion, SourceHash, NewId, PublicId)` with strings for source identifiers/hashes, int importer version, LongId new ID and Guid public ID; unique `(SourceSystem,EntityType,LegacyId)` prevents allocating a second identity on rerun. A changed source hash enters reviewed revision handling, never replaces the mapping. `CatalogMigrationPreview.Build(sourceRows, reviewedMappings)` returns accepted rows, rejected rows with reason/location, counts/checksums and reference reconciliation without writes. `CatalogMigrationImporter.ApplyAsync(previewId, expectedPreviewHash, cancellationToken)` runs only through OPS06's controlled importer after source freeze/backup and preflight; changed source or mapping invalidates the preview.

- [ ] **Write failing resolver tests first.** Exact GTIN with leading zeros and its canonical 14-digit equivalent resolve to the same allowed package. Wrong check digit is invalid; valid-but-unmapped GTIN is unknown. Supplier code resolves only in its source namespace. Multiple applicable mappings are ambiguous, not first-row wins. Retired identifiers remain traceable and cannot be reassigned silently. Same code in another catalog cannot leak its product/name/count; a syntactically valid random UUID never bypasses scope. Unknown or ambiguous scan cannot mutate stock or create a new SKU automatically.
- [ ] **Run the red slices.** Run `.tools/dotnet10/dotnet.exe test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter "FullyQualifiedName~ScanResolverTests|FullyQualifiedName~OperationalScanTests|FullyQualifiedName~CatalogMigrationTests"` and the new frontend scan-state tests. Record failures before resolver/importer implementation.
- [ ] **Implement context-aware resolution and operator confirmation.** Normalize according to the explicitly identified code kind, look up scoped active mappings and return bounded candidate lists. Unknown/ambiguous results offer manual search and a separate authorized mapping proposal; fuzzy suggestions are clearly labeled and cannot commit a selection automatically. Successful scans display package contents, target document line and entered quantity. A scanner event is one explicit observation; suppress hardware duplicate events within one input event, while deliberate repeated scans follow a visible count mode. Lost command responses use idempotency receipts, not timing-based duplicate guesses.
- [ ] **Exercise receiving-specific failures.** Scan one carton and enter quantity 1: receive one carton, not 12 cartons; base equivalent is informational until explicit unboxing/conversion. Unknown/ambiguous/wrong-supplier mapping cannot increment receipt. Require PO/receipt revision and validate actual lot/expiry; mismatched SKU, damaged or expired goods follow inspection/quarantine. Partial receipt and retry record actual count once. A mapped code changing after preview requires revalidation.
- [ ] **Exercise picking-specific failures.** Compare scan to the reserved order-line snapshot and expected package; a loose-unit code cannot satisfy a sealed-carton pick or silently substitute a 10-pack for a 12-pack. Wrong warehouse/channel/lot, held/expired stock, insufficient reservation, unknown/ambiguous codes and revoked picker assignment block the pick. Explicit substitution returns to buyer approval. Two pickers cannot consume the same final reserved package.
- [ ] **Exercise dispatch-specific failures.** Revalidate shipment and picked-line revision; scanning an unpicked SKU, extra quantity, another shipment's item or a changed mapping cannot dispatch it. Repeated successful response/retry does not decrement again. Unknown/ambiguous scans leave shipment and balances unchanged. Dispatch document records actual quantities using the accepted package snapshot; scanning alone is not recipient acceptance.
- [ ] **Exercise stocktake-specific failures.** Keep sealed/loose rows and lot/location explicit. Unknown scan creates an unresolved observation, not a zero balance or a new product. Ambiguous scan requires resolution before count submission. Show repeated-scan count mode and corrections explicitly. Count-maker cannot approve their own adjustment; stale balance revision after movement requires rebase/review. Full count versus sampled/unobserved inventory is distinguished so absent scans never imply stock is zero.
- [ ] **Write failing migration rehearsal tests.** Seed only synthetic legacy fixtures with actual foreign-key shapes: family/SKU, batches/movements, open order lines, shipments/receipts, invoice lines, return cases, attachments and accounting source references. Rerun identical import: IDs/UUIDs/counts/balances remain unchanged. Test duplicate normalized SKU, ambiguous family, missing contents, changed source hash, orphan references, demo rows, malformed IDs and previously imported real records. Reject unresolved rows with actionable reasons; do not use `split('--')` or a name regex as accepted migration proof. Reviewed mapping can use source documents/human-approved facts, with provenance.
- [ ] **Implement preview/mapping/import in bounded batches.** Allocate Long IDs and UUIDs once, persist mapping transactionally and retain legacy IDs as references/aliases without treating them as current authorization. Importer revisions cannot regenerate public references. Preserve actual known historical snapshots; unresolved historical pack facts remain explicit unknowns and block affected conversions/promises until resolved. Preflight foundation validators/indexes before writes, advance counters above imported IDs and link image/evidence hashes through the controlled asset migration. No demo product or balance becomes production inventory by default.
- [ ] **Verify reconciliation and coordinated acceptance.** Compare source/target counts, legacy-to-new mapping coverage, sealed/loose/held/expired/reserved quantities by SKU/lot/location, open commitments, actual deliveries, document-line snapshots, source accounting references and attachment hashes. Rehearse interruption/resume and stale-preview rejection. OPS06 signs the real-data map and one-writer cohort switch; before then the importer remains a tested preparation tool, not a live migration. Run green resolver/operational/migration tests and browser flows; have an independent reviewer inspect unknown/ambiguous paths and reference preservation. Record unresolved rows and business gates without claiming completion.

**Acceptance:** Operators can scan the same package through receiving, picking, dispatch and stocktake with a correct purpose-specific result. Unknown or ambiguous scans stop safely. A repeat migration produces the same identities and reconciled balances/documents, including historical 12-packs after a new 10-pack is introduced.

## Test execution, review and handoff

These are implementation-time commands, not checks performed by writing this plan. Coordinate the .NET build/test lease with other agents. Use the local SDK shown above and the existing isolated real-Mongo test fixture; no skipped Mongo cases count as acceptance. Frontend `npm --prefix frontend test` auto-discovers top-level `frontend/tests/*.test.ts`; it does not run browser scenarios. OPS05 supplies the shared browser harness, then the scenarios in `docs/quality/catalog-browser-scenarios.md` must run against the actual API path, with fixture-only shortcuts excluded from production.

- [ ] Confirm dependency contracts before each task; preserve existing stock/price/idempotency regressions. Add a minimal failing behavior test, implement it, run the focused suite, then broaden only for affected integration boundaries.
- [ ] Keep OpenAPI/generated client drift checks green; DTO tests cover numeric strings, strict unknown fields, nullable values, scoped errors, expected actor, version/idempotency headers and no client authority over ratios/prices.
- [ ] Record per-task test counts, no-skip Mongo evidence, browser widths/keyboard checks, independent review findings and source commit in `docs/quality/report-catalog-acceptance.md`. Link evidence from the umbrella progress ledger; do not treat a checked planning step as production acceptance.
- [ ] At each completed task, review only owned changes and make the project's requested task-sized commit through the execution owner. Do not commit concurrent work or create a commit while this planning request is active.
- [ ] CAT03/CAT04 remain open until downstream order→pick→delivery→invoice→return snapshots and purpose-specific scans are exercised. F04 remains externally gated until actual OMI/S-Mart authority/acknowledgment/reconciliation is accepted. I10 remains gated until real-data mapping and the coordinated cutover are approved and rehearsed.

## Required outcomes versus conditional enhancements

Required for this plan: explicit package choice; persisted catalog masters and immutable identities/definitions; Long/string numeric fidelity; SKU/GTIN/source-code separation; legitimate GTIN handling; atomic audited conversion; scoped versioned quotes; snapshot continuity; safe operational scans; repeatable migration; source freshness; explainable code/fuzzy matching; and human-reviewed threshold/lead-time replenishment.

Chosen implementation decisions: UUIDv4 for new public catalog references; permanent code reservations/aliases; new SKU for material package changes; integer discrete contents in this baseline; separate sealed/loose physical balances. These choices implement the report without claiming they are universal GS1 or legal requirements. UUIDv4 is a random identifier format described by [RFC 9562 §5.4](https://www.rfc-editor.org/rfc/rfc9562.html#section-5.4); it provides no access-control guarantee.

Conditional additions: standardized UNECE unit/packaging code mappings where a source integration requires them; serial tracking for items that need individual traceability; GS1 allocation by an authorized owner where new identifiers are actually required; mixed/variable-measure bundles through a reviewed composition contract. Local embeddings and demand forecasting require a later measured proposal with clean data and human confirmation; they are not prerequisites and cannot guess package quantities.

### New-pool source setup preparation — 29 September 2026

A private proposal/approve/reject/withdraw service now persists explicitly supplied source setup and immutable audit history. Separate source read/propose/approve grants preserve all existing role defaults; fresh-MFA approval requires an independently verified person. An approved setup creates an inactive physical pool with an organization-wide unique canonical key. Exact warehouse/channel read/list scope, whole-command retries, final access fences, terminal decision uniqueness and atomic rollback are part of the candidate boundary.

This narrows CAT02's governed pool-authoring gap. It does not complete CAT02: source activation, real authority observations, shared-channel aliases/revisions, lot authoring, strict forward migration/preflight, cost/source owning workflows and usable API/UI remain required. Actual business facts and operational acceptance have not been fabricated. See docs/architecture/stock-authority-contract.md and docs/quality/stock-source-setup-verification-2026-09-29.md for the 248 passing local checks and their limits.
