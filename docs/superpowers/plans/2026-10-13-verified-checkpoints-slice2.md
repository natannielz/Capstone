# Verified Checkpoints Slice 2 — Inventory Conversion and Release Seals (phase 1b)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut the remaining substitution/dispatch write latency (consent ~27 s, dispatch preview/command 16–18 s) by sealing the two Inventory provenance records whose full re-proof dominates `ShipmentDispatchFiniteSource` captures: completed pack conversions and stock-release decisions.

**Architecture:** Same primitive and pattern as slice 1 (`Integrity/*`, `Fulfillment/ShipmentDispatchSeals.cs`): each owner gets one seal collection (created by migration 116), seals a record once after its existing full proof inside an owning unit-of-work operation, and verifies the seal (MAC over raw BSON, live row hash, exact membership, scope shape) on later reads instead of replaying ancestry. Present-but-invalid seals refuse and raise `SealIntegrityAlerts`; absent seals run today's proof and then seal. Seals are written only when `ProvenanceSeal.Sealable`.

**Tech Stack:** C#/.NET 10, MongoDB 8.2 rs0 (127.0.0.1:27028), xUnit native tests via `tmp/production/run-full-migration-checks.ps1`, R02 browser gate.

**Spec:** `docs/superpowers/specs/2026-10-12-verified-checkpoints-design.md` (phase 1b row of §3.2; §3.3 read path; §3.4 write path).

## Global Constraints

- Fail closed: present-but-invalid seal → refuse (`*_unavailable`) + integrity alert; never re-prove around it.
- Only `MongoUnitOfWork.OwnsOperation(tx)` writes seals; caller-supplied sessions never write.
- Each domain owns its seal code and schema (per-domain module rule); no cross-module clutter.
- Forward-only migration 116 after 115; never weaken the 60 s transaction deadline or the 30 s browser action bound.
- Existing tamper tests keep passing unchanged.

## Measured starting point (browser22, instrumented)

`consent.stage admission` 38.5 s over 2 consents, of which `admission.lot finite` 36.3 s (finite capture: per-evidence `AuthenticateAsync` of conversions/releases through `PackConversionService.ConversionHistoryRead`); `order.graph miss` 0.38 s avg; `shipment.verify` 0.27 s avg.

## Review Focus

1. A conversion whose destination later receives another conversion/dispatch must keep verifying (membership bounded by the conversion's own transitions, like the shipment `$lte` scope).
2. Release decisions referenced by several conversions/shipments: one seal, memoized per operation, never per caller.
3. Callers relying on `HeadRead`/`releaseAuthenticated` side effects (`TryJoin`, `RecordAccount`, `AuthenticatedRelease.Create`) must still receive the exact proofs they register — sealed path supplies them or the caller stays on the full proof.
4. Seal invalidation when an Inventory migration legitimately rewrites a sealed row (document the delete-seal → full-proof → re-seal procedure).
5. Latency target met without relaxing any check: browser consent < 10 s, dispatch preview/command < 5 s locally (staging p95 targets still require M6).

---

### Task 0: Locate the exact proof boundaries (read-only, no code)

- [ ] Re-add a scratch timing wrapper (local only, not recorded) or read browser22 aggregate to split `admission.lot finite` between `ConversionAsync`/`CompletedConversionProof`, `AuthenticateReleaseOriginAsync` and shipment sources.
- [ ] Record in the ledger: for conversions — the function that builds `CompletedConversionProof` from a conversion row (`PackConversionService.ConversionAsync(tx, id, ct, read)` and `ConversionHistoryRead.ProofForHead`); for releases — `ConversionHistoryRead.AuthenticateReleaseOriginAsync` / `StockReleaseService` origin proof. Note every row each proof reads (collections + filters) — these become membership scopes.
- Expected: two owner functions identified with their read sets; ruling if either proof reads mutable state (that part stays fresh).

### Task 1: Migration 116 and owner schemas

**Files:** Create `backend/src/UnitToko.Platform/Inventory/InventorySealSchemas.cs` (Inventory-owned: `inventoryConversionSeals` kind `inventory.packConversion`, `inventoryReleaseSeals` kind `inventory.releaseDecision`, validator = `ProvenanceSealSchemas` shape with the owner kind), `backend/src/UnitToko.Platform/Mongo/Migrations/InventorySealMigration.cs` (Version 116, same guards as `ProvenanceSealMigration`), register in `CollectionMigrations` (`CurrentVersion = 116`). Test: extend `ReceiptOriginSealTests`-style migration test in new `backend/tests/UnitToko.Tests/Inventory/InventorySealMigrationTests.cs`.

- [ ] RED: test applies migrations, asserts both collections strict-validated, unique `kind_owner`, empty; reapply preserves an inserted seal; unsealed row (no mac) refused by validator. Run: build + `run-full-migration-checks.ps1 -Filter FullyQualifiedName~InventorySealMigrationTests` → FAIL (types missing).
- [ ] GREEN: implement; run → PASS; migration/persistence regression filter (MigrationLifecycle, HistoricalRead, CatalogVisibility, MigrationReview, PersistenceHttp) → PASS.

### Task 2: Conversion seals

**Files:** Create `backend/src/UnitToko.Platform/Inventory/PackConversionSeals.cs` (mirror of `ShipmentDispatchSeals`: `VerifyAsync(database, tx, conversionRow, ct)` → bool, `SealAsync(...)`; scopes = the conversion's own source/destination transitions with `_id <= ` its transition ids plus the read set recorded in Task 0); modify the conversion proof function from Task 0 to try the seal first and to seal after a full proof. Test: `backend/tests/UnitToko.Tests/Inventory/PackConversionSealTests.cs` using the existing converted-dispatch fixture (`ApprovedShipmentDispatchAdmissionReaderTests.PreparedAsync(fixture, converted: true)`).

- [ ] RED: caller session → no seal; owning read → one seal and a counter `PackConversionSeals.FullProofs` increments once; second operation → counter unchanged and identical proof facts; forged mac → refuses + alert; member row change → refuses; seal deleted → full proof + re-seal. Run → FAIL.
- [ ] GREEN: implement; run → PASS; then `StockReleaseServiceTests`, `PackConversionServiceTests`, `ShipmentDispatchSealTests` → PASS.

### Task 3: Release-decision seals

**Files:** Create `backend/src/UnitToko.Platform/Inventory/StockReleaseSeals.cs`; modify the release origin proof from Task 0 (sealed path returns the same `(StockReleaseSourceProof, HeldReceiptOriginProof)` pair, with the receipt origin itself coming from its slice-1 seal). Test: `backend/tests/UnitToko.Tests/Inventory/StockReleaseSealTests.cs` (release fixture from `StockReleaseServiceTests`).

- [ ] RED/GREEN exactly as Task 2 (caller session, seal once, verify without replay, forged/member/deleted cases).
- [ ] Dependency: the release seal lists the receipt-origin seal it consumed in `dependencies` (kind, ownerId, sealHash = SHA-256 of that seal's raw BSON); verification checks the dependency seal still exists with that hash.

### Task 4: Latency acceptance

- [ ] Native: full R02 9-class filter + all seal classes on one build → PASS.
- [ ] Browser: `OrderSubstitutionBrowserTests` (main tree run via `run-full-migration-checks.ps1 -Filter FullyQualifiedName~OrderSubstitutionBrowserTests` with `UNITTOKO_SUBSTITUTION_BROWSER_FRONTEND` pointing at a full build) → PASS; record request timings (temporary local wrapper only) and compare with browser22: consent decision and dispatch each drop ≥50 %.
- [ ] If not met, stop and open phase 1d (order-progress seals) per spec; do not weaken bounds.

### Task 5: Close

- [ ] Remove any temporary timing code (grep clean), rerun gates (backend Release build, frontend test-domain, contracts check, full frontend build), update `docs/quality/production-progress.md`, commit with explicit paths only, push, verify Vercel.
- [ ] Final whole-branch review (fresh reviewer, most capable model) → fix Critical/Important RED→GREEN.
