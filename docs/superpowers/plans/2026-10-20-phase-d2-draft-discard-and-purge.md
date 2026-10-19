# Phase D2: Discard, Restore and 30-day Purge of Order Drafts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved D02 record-class rule for the first transient class, unsubmitted order drafts.
- "Buang draf" hides a draft.
- "Pulihkan" brings it back within 30 days.
- After 30 days a Worker job deletes the draft for good, keeping its audit trail.

**Architecture:**
- Ordering adds a new collection, `orderDraftDiscards`, through migration 118. It holds one row per discarded draft (`_id` = draftId) with discardedAt, discardedBy, auditId and requestKey. Heads and versions are untouched, so the head/version verification invariants stay intact.
- `OrderDraftService` gains `DiscardAsync` and `RestoreAsync`, which are idempotent, authorised as `order.submit` on the draft's context, and limited to its creator.
- `ListAsync` omits discarded drafts, `ListDiscardedAsync` shows them, and `SaveAsync` refuses a discarded draft.
- `OrderDraftPurge` deletes drafts discarded for more than 30 days: head, versions, the drafts' `ordering.draft.save` receipts, `ordering.draft.saved:*` outbox rows and the discard row. Audit events are kept. The Worker runs it daily in bounded batches.

**Tech Stack:** .NET 10, MongoDB 8.2 replica set, the existing migration framework (`CollectionMigrations`), minimal APIs, and the Next.js drafts workspace.

**Spec:** `docs/quality/report-decisions.md` → "D01–D08 proposals approved (16 October 2026)", D02 row:
- transient items soft-delete, restorable for 30 days, then purged automatically;
- audit trail kept;
- issued records are never deleted.

## Global Constraints

- Only drafts that were never submitted are affected. Order submission does not reference draft rows (verified: no module outside Ordering/OrderDraft reads `orderDrafts` or `orderDraftVersions`).
- Audit events are never deleted. A discard and a restore each write an audit event, and the purge writes one summary audit event per batch with counts.
- Purge predicate: a discard row with `discardedAt < now − 30 days`. Rows without a valid `discardedAt` are never purged. Batches are bounded at 200 drafts and each draft is deleted in its own transaction.
- Migration 118 is forward-only and creates only the new collection plus its validator and indexes. The 60 s transaction deadline is unchanged.
- Copy:
  - "Buang draf"
  - "Draf dibuang. Anda dapat memulihkannya sampai {tanggal}."
  - "Pulihkan draf"
  - "Draf yang dibuang dihapus permanen 30 hari setelah dibuang."

## Review Focus

1. **Discard during an in-flight save:** a save racing a discard must either fail with a conflict or happen before the discard. It must never leave a saved draft that is discarded without the user knowing. Test: `save_after_discard_is_refused`.
2. **Restore after day 30 but before the purge ran:** refused with "masa pemulihan berakhir". Test: `restore_after_window_is_refused`.
3. **Another user** may not discard, restore or see the discarded list. Test: `other_actor_cannot_discard_or_restore`.
4. **Purge leaves no dangling rows and keeps audit:** after the purge the head, versions, receipts, outbox rows and discard row are gone and the audit events remain. Test: `purge_removes_draft_rows_and_keeps_audit`.
5. **Idempotent discard:** replaying the same key returns the original result, and a different key on an already-discarded draft returns `order_draft_discarded`. Test: `discard_is_idempotent_by_key`.

---

### Task 1: Migration 118 and storage

**Files:**
- `backend/src/UnitToko.Platform/Mongo/Migrations/OrderDraftDiscardMigration.cs`
- `backend/src/UnitToko.Platform/Ordering/OrderDraftDiscardSchemas.cs`
- `CollectionMigrations.cs` (CurrentVersion 118)
- `backend/tests/UnitToko.Tests/Ordering/OrderDraftDiscardMigrationTests.cs`

- [ ] Test: after migrating, `orderDraftDiscards` exists with a strict validator (`_id` long, `schemaVersion` 1, `discardedBy` long, `discardedAt` date, `auditId` long, `requestKey` string, `bodyHash` string, `reason` string) and a unique index on (`discardedBy`, `requestKey`). The migration is a no-op on rerun, and the history records 118. Run it and expect FAIL; implement; expect PASS.

### Task 2: Discard, restore, list and save refusal

**Files:**
- `OrderDraftService.cs`, plus `OrderDraftService.Discard.cs` for the commands
- `backend/src/UnitToko.Api/Ordering/OrderDraftEndpoints.cs`, adding:
  - `POST /api/v1/ordering/drafts/{id}/discard`
  - `POST /api/v1/ordering/drafts/{id}/restore`
  - `POST /api/v1/ordering/drafts/discarded/query`
- Test: `backend/tests/UnitToko.Tests/Ordering/OrderDraftDiscardTests.cs`

- [ ] Write tests for Review Focus 1, 2, 3 and 5:
  - a discarded draft is absent from `ListAsync` and present in `ListDiscardedAsync`, with a `restorableUntil` 30 days after discard;
  - restore returns it to `ListAsync`.
- [ ] Expect FAIL, implement, expect PASS. Regenerate the contracts.

### Task 3: Purge job

**Files:** `backend/src/UnitToko.Platform/Ordering/OrderDraftPurge.cs`, `backend/src/UnitToko.Worker/OrderDraftPurgeWorker.cs` and its registration, `backend/tests/UnitToko.Tests/Ordering/OrderDraftPurgeTests.cs`

- [ ] Write tests for Review Focus 4: a draft discarded 31 days ago with an injected clock is purged; one discarded 29 days ago is kept; a draft that was never discarded is kept; the batch bound is respected. Expect FAIL, implement, expect PASS.

### Task 4: Drafts workspace UI

**Files:** `frontend/src/modules/ordering/OrderDraftWorkspace.tsx`, `order-draft-client.ts`, tests

- [ ] "Buang draf" opens a `ReviewPanel` confirmation. After discarding, the screen states the restore deadline. A "Draf yang dibuang" section lists discarded drafts, each with "Pulihkan draf". A client test parses the new responses.

### Task 5: Close

- [ ] Runbook "Retention" section, register row, ledger, final review, commit and push.
