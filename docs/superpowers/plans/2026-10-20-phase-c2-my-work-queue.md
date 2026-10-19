# Phase C2: "Pekerjaan saya" Cross-Owner Work Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One "Pekerjaan saya" screen shows each kind of work waiting for the signed-in person, across owners. Items are ordered by how long they have waited and each links to its workspace. This closes register item F12 and gives the U01 shell its home.

**Architecture:**
- Each domain owns a small `IWorkSource` that calls its own existing, authorised queue service (no cross-module reads) and returns at most one summary item.
- A `WorkSummaryService` in `Platform/Work` runs the registered sources one after another. A source that refuses with `AuthorizationDeniedException` simply contributes nothing.
- The API exposes `POST /api/v1/work/summary/query`. The frontend shows the summary at the top of `/identity/account`, which the sidebar already labels "Pekerjaan saya".

**Tech Stack:** .NET 10 minimal APIs, existing domain services, the generated OpenAPI client, and Next.js 16 / React 19 with the C1 workspace kit.

**Spec:** `docs/superpowers/specs/2026-10-19-production-completion-program.md` (Phase C row, F12). Design: `reports/Unit Toko UI UX improvements.md` → "Tables and queues: … urgency-first sorting".

## Global Constraints

- Module ownership: each source lives in its domain folder and uses only that domain's service. Nothing reads another owner's collections.
- The summary reveals counts and the oldest age only, with no business payloads. Every source reuses the same authorisation as the workspace it links to.
- The page size stays the owner's own (for example 25). The count shows `25+` when `HasMore` is set, and never runs an extra unbounded query.
- Sources run sequentially to protect the 60 s transaction deadline. A slow source does not hide the others: per-source failures are swallowed into "Belum dapat dimuat".
- Copy is Bahasa Indonesia ("menunggu sejak 2 jam", "25+ pengajuan").
- Stage explicit paths only.

## Review Focus

1. **Actor without a permission:** a source that throws `AuthorizationDeniedException` is omitted silently, and the actor never learns the queue exists. Covered by test `denied source is omitted` (Task 1).
2. **Source failing for another reason** (timeout, transient Mongo error): it is reported as an unavailable item and must not fail the whole summary. Covered by test `faulted source becomes unavailable, others still shown` (Task 1).
3. **Empty queues:** items with count 0 are omitted, and the UI shows "Tidak ada pekerjaan yang menunggu." Covered by Tasks 1 and 3.
4. **Page boundary:** a source with exactly 25 items and `HasMore` false shows "25", not "25+". Covered by Task 2 per source.
5. **Stale UI after acting:** returning to the page refetches the summary, so a handled item disappears. Covered by Task 3 (refetch on mount and on `pageshow`).

---

### Task 1: Work summary contract and service

**Files:**
- Create: `backend/src/UnitToko.Platform/Work/WorkSummary.cs`, containing `WorkItem`, `IWorkSource` and `WorkSummaryService`
- Test: `backend/tests/UnitToko.Tests/Work/WorkSummaryServiceTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record WorkItem(string Key, string Area, string Label, string Href, int Count, bool HasMore, DateTimeOffset? OldestAt, bool Unavailable = false);`
  - `public interface IWorkSource { string Key { get; } Task<WorkItem?> ReadAsync(ActorContext actor, CancellationToken ct); }`
  - `public sealed class WorkSummaryService(IEnumerable<IWorkSource> sources) { Task<IReadOnlyList<WorkItem>> ReadAsync(ActorContext actor, CancellationToken ct); }`. Items with Count > 0 or Unavailable are kept. Order is unavailable last, then `OldestAt` ascending (nulls last), then `Key`.

- [ ] **Step 1:** Write tests with fake sources:
  - `denied source is omitted`
  - `faulted source becomes unavailable, others still shown`, using a source that throws `TimeoutException`, so the item is `Unavailable: true` with its label
  - `zero-count items are omitted`
  - `items are ordered by oldest waiting first`
  - `cancellation propagates`: an `OperationCanceledException` from the caller's token is rethrown, not swallowed
- [ ] **Step 2:** Run with filter `FullyQualifiedName~WorkSummaryServiceTests` and expect FAIL (types missing).
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run again and expect PASS. Commit.

### Task 2: Domain sources and endpoint

**Files:**
- Create:
  - `backend/src/UnitToko.Platform/Billing/PaymentReviewWorkSource.cs`: `ListReviewQueueAsync(actor, null, new("work.summary", …))`. It counts entries with state `pending`, or `assigned` to the actor; Href `/billing/payment-reviews`; Area `finance`; label "Bukti pembayaran menunggu pemeriksaan".
  - `backend/src/UnitToko.Platform/Ordering/OrderReviewWorkSource.cs`: `ReviewListAsync` for each stage the actor can decide. Href `/ordering/reviews`; label "Pesanan menunggu keputusan".
  - `backend/src/UnitToko.Platform/Ordering/IncomingRequestWorkSource.cs`: `BranchOrderRequestService.ListAsync(actor, incoming: true, null)`. Href `/ordering/requests/incoming`; label "Permintaan cabang menunggu keputusan".
  - `backend/src/UnitToko.Platform/Fulfillment/DispatchWorkSource.cs`: `ShipmentDispatchService.ListAsync(actor, null)`, counting `CanDispatch`. Href `/fulfillment/dispatches`; label "Kiriman siap diberangkatkan".
  - `backend/src/UnitToko.Api/Work/WorkEndpoints.cs`: `POST /api/v1/work/summary/query` with body `{ purpose }`, private no-store headers, returning `WorkSummaryDto(items)`.
- Modify:
  - DI registration where domain services are registered (`AddScoped<IWorkSource, …>` per source, plus `WorkSummaryService`).
  - `backend/contracts/openapi.json` and the generated frontend types (`export-openapi.ps1` under pwsh 7, then `npm run generate --prefix backend/contracts`).
- Test: `backend/tests/UnitToko.Tests/Work/WorkSourcesTests.cs`. Use the existing payment-review test data (one submitted payment, a reviewer with `payment.review.read` + `invoice.read`) to check:
  - a count of 1 with OldestAt equal to submittedAt;
  - an actor without permission gets no item;
  - the page-boundary rule from Review Focus 4, via a fake queue page of 25 with `HasMore` false.

- [ ] **Step 1:** Write `WorkSourcesTests` (payment source plus page boundary). Expect FAIL.
- [ ] **Step 2:** Implement the sources, endpoint and registrations. Regenerate the contracts and run `export-openapi.ps1 -Check`.
- [ ] **Step 3:** Expect the tests to pass. Run the Ordering/Billing/Fulfillment regression batch for the touched services. Commit.

### Task 3: "Pekerjaan saya" on the account page

**Files:**
- Create: `frontend/src/modules/identity/work-summary-client.ts` (gateway via the generated client; zod-validated) and `frontend/src/modules/identity/WorkSummary.tsx`
- Modify: `frontend/src/app/identity/account/page.tsx` or `IdentityAccount.tsx`, rendering `WorkSummary` above `WorkspaceDirectory`
- Test: `frontend/tests/work-summary.test.ts`

**Interfaces:**
- Produces: `workAgeText(oldestAt: string | null, now: number): string`, giving "menunggu sejak 5 menit", "… 2 jam", "… 3 hari", or "" for null. Also `workCountText(count: number, hasMore: boolean): string`, giving "25+" or "3".

- [ ] **Step 1:** Write tests for `workAgeText` and `workCountText`, and for the client parser rejecting a malformed item.
- [ ] **Step 2:** Expect FAIL, then implement.
- [ ] **Step 3:** `WorkSummary` uses the `ListTable` columns Pekerjaan, Jumlah (end) and Menunggu, with the open label "Buka {label}" linking to href.
  - `OutcomeState` covers loading, empty-first ("Tidak ada pekerjaan yang menunggu."), offline and error.
  - It refetches on mount and on `pageshow`.
- [ ] **Step 4:** Run frontend tests, typecheck, lint and both builds. Commit.

### Task 4: Close C2

- [ ] Browser check through an existing browser test that signs in a reviewer: add a `/identity/account` screenshot step to `verify-payment-verification-browser.mjs` after a submission exists, and assert "Bukti pembayaran menunggu pemeriksaan".
- [ ] Update the F12/U01 rows, ledger, final review (Critical/Important RED→GREEN), commit and push.
