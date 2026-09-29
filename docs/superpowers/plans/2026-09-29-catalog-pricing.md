# Reviewed catalog pricing implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Let authorized staff propose and independently approve exact per-package prices, preserve immutable history, and publish typed price facts for buyer quote and packaging workflows.

**Architecture:** Catalog owns an immutable priceVersions history and a separate current-price head per SKU. Commands bind the current SKU/definition and previous price version, use scoped maker/checker authorization and person fences, and commit proposal/decision/version/head/audit/receipt/outbox atomically. Buyer access is a separate entitled projection; no staff permission or metadata listing implicitly grants a buyer price.

**Tech Stack:** Existing .NET 10, MongoDB transactional persistence, Money numeric contract, Next.js typed API clients and durable command recovery.

**Spec:** docs/superpowers/plans/2026-09-28-report-catalog-packaging.md CAT01/CAT03; docs/requirements/unit-toko-production.md; docs/requirements/unit-toko-report-2026-09-28.md V03/V04/V07/V08 and D02/D03.

## Global Constraints

- This fills an identified CAT01 dependency of quotes/packaging selection. Accepted source control/handoff and all other original/report requirements remain required.
- Preserve existing six templates. pricing.manage prepares/withdraws proposals; new pricing.approve is an explicit scoped grant with fresh MFA and independently verified person approval, added to no role automatically.
- Amounts use the existing Money type, exact decimal strings at the API and BSON Decimal128. Explicit zero is permitted only as a reviewed value; absence is never a zero price.
- Currency is explicit canonical uppercase three-letter data, as in OperationalBookKey. Shape validation is not a currency-support, tax, conversion or finance-policy approval.
- No inferred HET minimum/maximum, margin, tax, discount, exchange-rate or customer-tier policy.
- Initial published price takes effect at server approval time. Scheduled prices require an explicit future lifecycle and are not inferred from client time.
- Price history belongs to one SKU. A material package change remains a new SKU; price approval cannot reinterpret existing packages or issued transaction snapshots.
- Proposals bind exact SKU revision, current definition ID/version and base price version. Stale proposals may be rejected/withdrawn, but cannot approve.
- A successful receipt replays only under current action authority and independently resolved original participants; lookup/recovery never submits replacement work.
- Existing applied migrations 1–19 are frozen. Register the next free forward migration only after complete storage/provenance preflight.
- Preserve unrelated work and publish verified checkpoints to the authorized GitHub branch. Vercel access is still unavailable; archives are not deployments.

## Review Focus

1. Price for a 12-unit carton is reused for a replacement 10-unit SKU: Task 1 identity/binding tests and Task 2 graph rejection.
2. Two proposals based on one head both approve: Task 2 controlled real-Mongo race; exactly one version/head update commits.
3. A price appears free because no price exists or BSON precision is lost: Task 1 explicit zero/missing/range cases; Task 2 missing-head and Decimal128 decoding cases.
4. Same person uses two accounts or MFA expires during approval: Task 2 real person/MFA final-fence tests.
5. Buyer sees another channel/tier through the staff endpoint or quote reuses an obsolete head: Task 3/4 separate staff/buyer authority and CAT03 transactional head fence tests.

## Task 1 — Define immutable prices and independent approval authority

**Files:**
- Create backend/src/UnitToko.Modules/Catalog/PriceVersion.cs
- Modify backend/src/UnitToko.Modules/Identity/Permissions.cs
- Modify backend/src/UnitToko.Modules/Identity/ApprovalPolicy.cs
- Modify backend/src/UnitToko.Modules/Identity/PersonApprovalPolicy.cs
- Create backend/tests/UnitToko.Tests/Catalog/CatalogPricePolicyTests.cs
- Create backend/tests/UnitToko.Tests/Catalog/CatalogPriceAuthorityStoreTests.cs (real-Mongo authority probes only)

**Consumes:** Money, LongId, Sku, VariantDefinitionVersion, CatalogPolicy.Validate and existing account/person approval policy.
**Produces:**

    public sealed record PriceVersion(LongId Id, LongId SkuId, long Version,
        string Currency, Money UnitPrice, DateTimeOffset EffectiveAt);
    public sealed record CatalogPriceInput(string Currency, Money UnitPrice);
    public sealed record CatalogPriceExpectation(long SkuRevision, LongId DefinitionId,
        long DefinitionVersion, long PriceVersion);
    public static class CatalogPricePolicy
    {
        public static void Validate(CatalogPriceInput input);
        public static void Validate(PriceVersion price);
        public static PriceVersion Approve(Sku sku, VariantDefinitionVersion definition,
            PriceVersion? current, CatalogPriceExpectation expected, CatalogPriceInput input,
            LongId id, DateTimeOffset now);
    }

- [x] Write tests for first and later prices, immutable old value, exact cents/large values, explicit zero versus missing, canonical currency, cross-SKU history, stale SKU/definition/base-price expectations, retired SKU, non-UTC/backdated time, same price no-op, positive IDs/revision exhaustion and new immutable version ID.
- [x] Run the CatalogPricePolicyTests slice before production implementation; retain the expected missing-contract/permission failures.
- [x] Implement the pure transition using current catalog pair validation, exact expected comparisons, Money validation, checked next revision and server UTC millisecond time. Return a new PriceVersion; never mutate SKU/definition/history.
- [x] Add pricing.approve to the operations permission catalog with five-minute MFA assurance and to both account/person ProposedBy independence rules. Do not add it to role templates. Test same-account denial and required person-account set.
- [x] Run the new tests and affected existing numeric/catalog/authorization policy slices; record exact results.

## Task 2 — Persist proposals, decisions and immutable price versions

**Files:**
- Create backend/src/UnitToko.Platform/Catalog/CatalogPriceService.cs
- Create backend/src/UnitToko.Platform/Catalog/CatalogPriceBson.cs
- Create backend/src/UnitToko.Platform/Mongo/Migrations/CatalogPriceSchemas.cs
- Create backend/tests/UnitToko.Tests/Catalog/CatalogPriceServiceTests.cs
- Create backend/tests/UnitToko.Tests/Catalog/CatalogPriceRaceTests.cs

**Consumes:** Task 1 contracts; CatalogRepository and actual family/category/SKU/definition graph; AuthorizationStore.ExecuteCommandAsync with final scope/session checks and person approval; immutable command metadata and existing outbox.
**Produces:** scoped preview/propose/approve/reject/withdraw/current/history methods on CatalogPriceService; exact typed price facts for a separately authorized quote consumer.

- [ ] Use priceVersions already reserved by Foundation. New proposal/decision/head collections store strict schemaVersion 1 and complete maker/decision/audit/receipt binding. Head is keyed by SKU and references one immutable accepted version. Unique proposal decision and (skuId,version) constraints serialize decisions.
- [ ] Write real-Mongo tests before service implementation: explicit first approval, second version leaves old row unchanged, absent price returns null, same-person denial, cross-owner denial, stale catalog/base-price denial, failed audit/outbox rollback, same-key replay/different-body conflict.
- [ ] In each command load actual SKU/family/category/definition ownership and the relevant price graph in the transaction; authorize pricing.manage or pricing.approve on that owner. Approve requires active graph and exact proposed binding; withdraw requires original maker. Persist all effects atomically and conditionally fence the actual SKU/current price head.
- [ ] Reads/replay validate the selected immutable proposal, decision, price, original audit/whole-command receipt and current head binding. Historical definitions need not remain current; keep original facts. Recovery is action-specific and reference-only.
- [ ] Add controlled approval/head, retirement, grant/session/person/MFA races. A failed final fence rolls back head/version/decision/audit/receipt/outbox.
- [ ] Register no service route before the forward migration and recovery contract are verified.

## Task 3 — Install storage and publish API/client recovery

**Files:**
- Create backend/src/UnitToko.Platform/Mongo/Migrations/CatalogPriceMigration.cs
- Modify backend/src/UnitToko.Platform/Mongo/CollectionMigrations.cs with next free version
- Create backend/src/UnitToko.Api/Catalog/CatalogPriceEndpoints.cs and CatalogPriceDtos.cs
- Extend backend/contracts/openapi.json and frontend/src/modules/contracts/openapi.generated.ts through generators
- Create frontend/src/modules/catalog/catalog-price-client.ts
- Create backend/tests/UnitToko.Tests/Catalog/CatalogPriceMigrationTests.cs and CatalogPriceHttpTests.cs
- Create frontend/tests/catalog-price-client.test.ts
- Create docs/runbooks/catalog-price-migration.md

- [ ] Preflight every existing price/proposal/decision/head and their ownership/provenance, duplicate keys and counter bounds before DDL; reject incompatible Foundation-era price data rather than inventing approval. Resume partial DDL under migration lease; preserve stronger validators/indexes.
- [ ] API DTOs use required numeric-string IDs/versions and decimal-string Money; reject unknown fields, unsupported actions and missing expected-actor/idempotency headers.
- [ ] Scope preview/decision/history/recovery separately; public-ID guessing and metadata grants cannot read price history. Generated clients freeze reviewed body and original key, preserve uncertain outcomes and resolve only the matching actor/action receipt.
- [ ] Verify migration upgrade/failure/replay, HTTP actor/scope/MFA/stale/conflict/recovery and generated-contract checks.

## Task 4 — Staff price desk and buyer quote integration

**Files:**
- Create frontend/src/modules/catalog/CatalogPriceWorkspace.tsx and catalog-price-state.ts
- Create frontend/src/app/catalog/prices/page.tsx
- Extend current-access/navigation/exact Auth0 return-path allowlists
- Create frontend/tests/catalog-price-state.test.ts
- Create backend/tests/UnitToko.Tests/Catalog/CatalogPriceBrowserTests.cs
- Create frontend/scripts/verify-catalog-price-browser.mjs
- Extend docs/quality/catalog-browser-scenarios.md and production-progress.md

- [ ] Maker selects an assigned SKU and sees exact packaging/current approved price; enters explicit currency/amount/reason, reviews frozen before/after and confirms.
- [ ] Checker sees immutable proposal/package comparison, independently approves or rejects with fresh MFA. Original maker can withdraw. Staff reload preserves original recovery reference without resubmitting.
- [ ] Test real browser against frontend/API/Mongo: first/second prices, stale definition/head, wrong person, changed session, lost committed response, mobile/keyboard/focus.
- [ ] CAT03 buyer quote uses an explicitly entitled price projection and fences the current head in order submit. No early authority switch to the new catalog while legacy stock/money writers remain active.
- [ ] Independent review, full relevant regression, precise publication evidence and controlled business/provider acceptance remain part of completion.

## Execution decision

The existing autonomous goal authorizes implementation. Work starts with the immutable price/approval contract and proceeds through the real producer and user workflows; Task 1 alone does not complete pricing. D02/D03 real grants/commercial policy and stock-control/operational writer gates stay explicit. Independent agent capacity is unavailable until 4 October; author review is not substituted for independent acceptance.
