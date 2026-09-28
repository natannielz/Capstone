# Stock Count Browser Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Retain and safely reconcile a stock-count submission after an uncertain response without creating another observation or disclosing its contents in browser storage.

**Architecture:** Extend the existing references-only operation journal for exactly inventory.stock-count.record. Its dedicated adapter accepts only a committed immutable observation receipt and never falls back to access recovery or offers closure. The future count screen supplies the authenticated adapter; this slice creates no route, navigation link, API, or production activation.

**Tech Stack:** TypeScript, existing MutationLedger and OperationJournal, Node 24 test runner.

**Spec:** docs/requirements/stock-count-observations.md, particularly Command and replay and the separate API/UI acceptance gates.

## Global Constraints

- Persist only actorId, operation, key and createdAt.
- Record and recovery require current stock.count on the stored warehouse; history is separately stock.read.
- Observation IDs are canonical positive Int64 strings; observation version is exactly "1".
- Missing, rejected, malformed or stale responses cannot clear a pending reference.
- No close, abandon, automatic resubmission or invented successful server outcome.
- Preserve existing Catalog, identity, organization, access and person-review recovery behavior.
- This implements an approved part of the active production goal; existing execution authorization remains in effect.

## Review Focus

- Zero observed packages must survive a lost response without being treated as no request.
- A denied retry after uncertain commit must preserve the original key.
- Another actor or operation using the same key must retain its own reference.
- A valid result arriving after view replacement must not erase the journal.
- Unknown inventory operations, malformed IDs and mutable observation versions must fail closed.

### Task 1: Protect the stock-count reference and dedicated receipt boundary

**Files:** create frontend/tests/stock-count-recovery.test.ts; modify frontend/src/modules/administration/operation-journal.ts.

**Interfaces:**
- Consumes existing submitTrackedRequest, OperationJournal, MutationLedger, reconcileOperationRecovery and operationRecoveryCapabilities.
- Produces optional adapter key "inventory.stock-count" whose lookup returns StockCountRecoveryReceipt | null; receipt is { state: "committed", result: { id: string, version: "1" }, recordedAt: string }.
- Exactly inventory.stock-count.record maps to this adapter. Absence fails with recovery_adapter_unavailable, attempted closure with recovery_close_unsupported.

- [x] Write tests exercising a real journal and ledger, including this interrupted zero-count submission:
  ~~~ts
  await assert.rejects(submitTrackedRequest(ledger, journal, actorId, "count",
    "inventory.stock-count.record", { packages: 0, reason: "Private count reason" },
    async () => { throw new AccessFailure(503, "commit_outcome_unknown"); }));
  assert.equal(journal.list(actorId)[0].key, "count-once");
  ~~~
- [x] Run the new tests against the existing journal and record the expected unsupported-operation failures.
- [x] Extend the closed operation pattern with the exact name; add the typed dedicated adapter. Validate committed state, canonical observation ID, exact version "1" and recordedAt before clearing. Share the existing immutable receipt branch with Catalog while preserving Catalog's positive revision semantics.
- [x] Exercise null/permission/network failures, malformed receipts, denied closure, unsupported inventory names and actor/view replacement. Assert retained references as well as errors.
- [x] Run the new tests and existing recovery regressions; address only actual failures.

### Task 2: Verify integration and retain precise scope

**Files:** this plan; existing frontend source above.

- [x] Run node scripts/test-domain.mjs with pinned Node 24 to include the real frontend suite, future-date repository regression and preserved behavior.
- [x] Run frontend typecheck and lint; record preexisting warnings separately.
- [x] Have the independent reviewer inspect the exact source/test diff, including preservation of non-Inventory paths.
- [x] Publish the reviewed change with its test evidence alongside the next completed feature boundary. Keep overall stock-count API/UI and production goal open.

The staff screen still needs scoped named warehouse/package choices, safe HTTP projections, explicit zero-inclusive package entry, expected-actor/CSRF enforcement, protected login returns and browser verification. Those are the next implementation boundary; this recovery component must not create links to an unimplemented screen.

## Verification record — 28 September 2026

- Before implementation: six expected failures for the missing operation; the unsupported-operation guard passed.
- Focused final: 7 passed, 0 failed.
- Full frontend command: 460 passed; future-date repository: 6 passed; preserved-behavior golden: 7 passed.
- Typecheck and import boundaries passed. Lint passed with the existing nine warnings in unrelated legacy components/tests.
- Independent source review found no actionable issues. API/screen integration remains separate under docs/requirements/stock-count-staff-workflow.md.
- A first implementation edit had a syntax error caused by multiline replacement construction; repaired before the successful focused/full verification. An initial lint invocation used a nonexistent path; the actual package-script commands subsequently passed.

Published in 2068905dc48b7e5d63837c693696e80e6b082604. Its later full GitHub workflow exposed a separate bounded-retry assumption in the Inventory concurrency test; see the staff API acceptance record. The browser recovery component is not the completed staff screen.
