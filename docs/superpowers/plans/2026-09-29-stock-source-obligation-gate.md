# Stock-source obligation gate implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Prevent an incomplete or stale operational-obligation check from declaring a canonical physical pool drained.

**Architecture:** Add one private transaction-aware gate beside InventoryLedger. It requires a complete fixed set of trusted owning-module readers, validates their exact pool binding and completeness, and conditionally advances the actual canonical pool fence in the same transaction. It is a prerequisite for accepted control/writer handoff; it does not accept control, activate a pool, promise stock or register synthetic readers.

**Tech Stack:** Existing C# / .NET 10, MongoDB driver, xUnit and local transactional Mongo replica set. No new package.

**Spec:** docs/architecture/stock-authority-contract.md; docs/superpowers/plans/2026-09-28-report-catalog-packaging.md CAT02; docs/requirements/unit-toko-report-2026-09-28.md F03/F04/D04.

## Global Constraints

- Preserve the complete original goal and all 97 report requirements. This prerequisite does not close CAT02.
- Use one canonical physical pool across all channels, including historically mapped disabled channels. A per-channel query cannot establish drain.
- Missing producer, missing collection, incomplete migration, incomplete reconciliation or unknown count is never zero obligations.
- Every owning workflow that creates/changes an obligation must also fence the same actual pool. An empty query cannot protect against phantom insertion by itself.
- Keep source readers internal, same database/client/session, exact positive Int64 identities/revisions and nonnegative counts. No request-supplied counts.
- All failure paths abort the caller transaction. Do not commit, retry, install schemas, activate a writer or change quantities inside the gate.
- No business decisions, real source capabilities or successful external acceptance are inferred. D01/D02/D04/D07/D08 remain open.
- Existing migrations 1–19 remain untouched; this stage creates no production collection or API.
- Independent review is pending agent capacity. Author review must be identified accurately.

## Review Focus

1. Missing one owning domain looks like zero pending work: test every domain omission and incomplete answer.
2. Disabled aliases hide obligations: gate requests the canonical pool without an enabled-channel filter; a fixture obligation attributed to another channel still blocks.
3. A reader substitutes a different pool/version or an earlier result: exact expected binding and bounded positive source revision are checked.
4. A producer inserts after the gate read: controlled Mongo race proves the shared pool fence aborts the older transaction.
5. A failure after earlier caller effects leaks partial work: missing coverage, positive obligations, cancellation, malformed storage/results and downstream failure all leave effects uncommitted.

## Task 1: Add the complete, transaction-bound drain prerequisite

**Files:**
- Create backend/src/UnitToko.Platform/Inventory/StockSourceObligationGate.cs
- Create backend/tests/UnitToko.Tests/Inventory/StockSourceObligationGateTests.cs
- Create backend/tests/UnitToko.Tests/Inventory/StockSourceObligationRaceTests.cs
- Modify docs/architecture/stock-authority-contract.md
- Modify docs/quality/production-progress.md
- Create docs/quality/stock-source-obligation-gate-verification-2026-09-29.md

**Consumes:**
- InventoryGraph.RequirePool(BsonDocument?), InventoryGraph.Location(BsonDocument)
- InventoryRepository.RequireSession(IClientSessionHandle?), AbortAsync(IClientSessionHandle?)
- StockSourceSetupSchemas.Validator("inventoryPools"), Key("inventoryPools")
- StockLocation and ActorContext validation; existing MongoUnitOfWork owns commit/retry.

**Produces:**

    internal enum StockSourceObligationDomain
    {
        Orders, Reservations, Fulfillment, Procurement, AfterSales, SourceReconciliation, InventoryOperations
    }
    internal sealed record StockSourceObligationExpectation(
        long PoolId, StockLocation Location, long PoolRevision, long PoolFence);
    internal sealed record StockSourceObligationResult(
        StockSourceObligationDomain Domain, StockSourceObligationExpectation Pool,
        long ProducerRevision, bool Complete, long OpenCount);
    internal interface IStockSourceObligationReader
    {
        StockSourceObligationDomain Domain { get; }
        IMongoDatabase Database { get; }
        Task<StockSourceObligationResult> ReadAndFenceAsync(
            IClientSessionHandle tx, ActorContext actor,
            StockSourceObligationExpectation pool, CancellationToken ct);
    }
    internal sealed record StockSourceDrainCheck(
        StockSourceObligationExpectation Pool,
        ImmutableArray<StockSourceObligationResult> Results);
    internal sealed class StockSourceObligationGate
    {
        internal StockSourceObligationGate(
            IMongoDatabase database, IEnumerable<IStockSourceObligationReader> readers);
        internal Task<StockSourceDrainCheck> RequireDrainedAsync(
            IClientSessionHandle tx, ActorContext actor,
            StockSourceObligationExpectation expected, CancellationToken ct = default);
    }

The required domains are operational blocking work: uncompleted accepted orders; reservations/external holds; picking/dispatch/delivery work; pending receiving; returns/replacements; import/export/reconciliation exceptions and unacknowledged source work; conversions, adjustments/count reviews and unsettled inventory movements. Open invoices and unrelated historical records are not implicitly deleted or required to have zero financial balance. Each producer owns its eventual precise lifecycle predicate and must prove complete coverage before registration. This gate provides no production reader until that adapter is implemented and verified.

Results are evidence only within the current owning transaction, not durable drain certificates. Returned Pool.PoolFence is the successfully incremented fence. The owner must persist any accepted transition and its reviewed facts in the same transaction; a later transaction repeats the full check.

- [x] Write failing tests using the real local Mongo fixture and synthetic producer rows confined to test code. Literal success expectations: seven zero results, pool fence 0 becomes 1, pool stays inactive, no stock/financial effects. Missing each domain, unknown/duplicate domain and foreign database/client fail. Positive counts (including Int64.MaxValue) and incomplete results abort earlier caller inserts; counts are not summed.
- [x] Run the new slice and record the missing-type/behavior failure before implementation:

      .tools/dotnet10/dotnet.exe test backend/tests/UnitToko.Tests/UnitToko.Tests.csproj --filter FullyQualifiedName~StockSourceObligation

- [x] Implement validation and the fenced transaction operation:

      repository.RequireSession(tx);
      // Require every enum domain exactly once before querying.
      InventoryGraph.RequirePool(pool);
      // Match _id/location/revision/fence and require active == false.
      // Invoke each trusted reader sequentially within tx.
      // Require exact Domain/Pool, ProducerRevision > 0, Complete == true, OpenCount == 0.
      // Do not add counts, skip missing readers or query only enabled aliases.
      var changed = await pools.UpdateOneAsync(
          tx, pool, Builders<BsonDocument>.Update.Inc("fence", 1L),
          cancellationToken: ct);
      if (changed.ModifiedCount != 1) throw new PersistenceConflictException(
          "stock_source_obligations_changed", "The reviewed stock source changed");
      // Return the copied immutable results and expected with PoolFence + 1.
      // Catch every exception, await InventoryRepository.AbortAsync(tx), then rethrow.

  Validate expected before storage access; reject max fence before increment. Check exact strict pool schema and safe unique key using the existing repository helper. Reader contracts require validated storage, authoritative actual source state and source-specific fences. All obligation-producing commands must contend on this same pool, including creates with no existing source row.

- [x] Test active/missing/malformed pool, wrong location/revision/fence, exhausted fence, missing/weakened storage, malformed/negative/wrong-domain/wrong-pool/wrong-version reader answers, absent transaction, foreign session and cancellation. In every in-transaction failure assert caller insert rollback and unchanged pool.
- [x] Test a controlled second transaction inserting a synthetic obligation while updating the actual pool fence. Resume the first check; its pool write must conflict, its caller insert must roll back, and the winning obligation must remain. A newly loaded check must see that outstanding work. Test downstream owner failure rolls back a successful check's pool and producer fences.
- [x] Run the full new slice plus the affected actual InventoryGraph, InventoryLedger and suspension race tests. Read TRX results, not just exit status. Independent full CI runs after publication without canceling the prior run.
- [ ] Document exact evidence and limits; author-review changes and inspect the owned diff. Commit only the six listed implementation/test/report files plus this plan, preserving unrelated work, and push the exact checkpoint to the authorized GitHub branch. Prepare its Vercel source archive; do not claim deployment while owning-account access is absent.

## Following producer work

Accepted control/handoff must load the existing complete policy/configuration/suspension provenance, independently reviewed immutable execution evidence, all current person/scope/MFA fences and the complete obligation gate in one transaction. It then needs a forward migration, API/receipt recovery, staff confirmation and real-browser verification. Actual owning-module readers and their lifecycle predicates are required before source activation; no empty adapter or missing collection can pass. Observations/reconciliation/external holds and canonical cost/stock consumers remain required after acceptance.

## Execution decisions

The user's existing autonomous goal authorizes this implementation and publication. No repeated routine plan approval is needed. This stage intentionally builds the prerequisite before an activation producer can consume it; it is not an operational handoff acceptance claim. Shared-pool conditional writes follow MongoDB's transaction guidance: https://www.mongodb.com/docs/manual/core/transactions-production-consideration/#in-progress-transactions-and-stale-reads .

Execution evidence: 172 affected passes, then 51 final gate passes; 174 distinct cases with zero final failures/skips. Author review added two red/green cases for a reader ending/replacing the original transaction. Independent review remains pending capacity. See the verification report and private publication continuation for exact checkpoint/run status.
