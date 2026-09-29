# Catalog price migration rehearsal

Migration 20 installs independently reviewed package prices. It leaves applied steps 1–19 unchanged. API availability is separate from staff/browser readiness, buyer entitlement, real commercial policy approval and production acceptance.

## Before applying

1. Select the reviewed release and approved destination configuration. Keep credentials in the established secret mechanism.
2. Pause and drain application, worker and integration writers for the entire preflight and installation. The renewable migration lease excludes competing migrators; it does not stop business writers.
3. Verify a coordinated backup and restore rehearsal. Preserve catalog categories, families, SKU/definition history, proposals, decisions, price versions/heads, exact audits, complete command receipts, original person-approval evidence and allocator counters together with stock/accounting and other business records.
4. Capture migration receipts, collection options/indexes and the business baseline. Do not create missing approvals, delete incompatible prices, lower counters or reinterpret an old package to make preflight pass.

## Apply and verify

From `backend`, with the approved deployment configuration:

    dotnet run --project src/UnitToko.Worker -- --migrate

Preflight checks every proposal, decision, price and head against the actual catalog graph, immutable historical definition, prior price, exact audit and whole command receipt. It checks audits/receipts in reverse to detect missing command records. Current suspension of an old approver does not invalidate legitimate historical data. Foundation-era prices lacking the reviewed provenance fail rather than acquiring a manufactured approval.

Before target mutations, the migration checks collection options, canonical identity indexes, TTL/retention hazards, compatible uniqueness, duplicate decisions/versions and Int64 allocator bounds. Compatible stronger validators and safe indexes are retained. Pricing preserves catalog-owned uniqueness constraints. Target writes use majority acknowledgement.

The four collections are `catalogPriceProposals`, `catalogPriceDecisions`, `priceVersions` and `catalogPriceHeads`. Decisions have unique `proposalId`; prices have unique `(skuId, version)`; heads use the SKU primary key. Proposal/decision SKU lookup indexes support bounded history. Money is BSON Decimal128, with no floating-point conversion; IDs/revisions stay Int64 and travel as lossless JSON strings. Counters only advance.

After success, verify steps 1–19 remain byte-for-byte unchanged, step 20 is applied and readiness succeeds. Compare the recorded business baseline. Repeating the same reviewed migrator must leave target state unchanged. On an isolated staging account, exercise package selection, proposal, a different verified person's approval, immutable history and original-key recovery. Missing price is null; explicit zero requires approval. No buyer availability or stock writer is activated by this migration.

## Failure and interrupted installation

Preserve diagnostics and original data when preflight rejects history or storage. Readiness stays closed while a step is applying. Use a reviewed repair or coordinated restore; never mark migration history applied by hand or remove evidence to bypass the check.

DDL is resumable, not one transaction. Keep writers paused after interruption and retry the same reviewed release once its lease is released or expired. Complete preflight runs again and retains compatible partial installation and the original start record. Do not remove a live competing lease.

Application rollback is not a schema downgrade. Reconcile compatibility and business records before reopening traffic. The local synthetic upgrade/interruption tests are evidence for this code; actual provider restore, one-writer cutover and operator/business acceptance remain M6 gates.

## Workflow controls

`pricing.manage` permits preparation and original-account withdrawal; `pricing.approve` permits independent approval/rejection and requires fresh MFA. Neither permission implies catalog editing or buyer price access. The API applies the same scope checks to selectors, detail, history and recovery. A proposal binds the exact SKU revision, definition and prior price; stale approvals fail. Price, decision, head, audit, receipt and outbox commit together. Recovery rechecks current authority and the original participants and cannot submit a replacement command.
