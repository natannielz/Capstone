# U05: Order Timeline Integration Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate the reviewed U05 candidate (`tmp/u05-timeline-candidate`) into main. A buyer opening an order in *Pesanan saya* then sees one timeline of that order's shipments, invoices, payment outcome and after-sales cases. Each section keeps its own owner's authorisation and verification.

**Architecture:**
- **Owner queries.** Each of the four owners gains `ListForOrderAsync(actor, orderId, beforeId)`. It routes through the owner's existing `ListAsync` core with one extra order predicate, so scope checks still run before pagination and every row is still verified. The owners are `ShipmentQueryService`, `InvoiceQueries`, `PaymentDraftService` and `AfterSalesCaseService`.
- **Endpoints.** Six read endpoints in `OrderTimelineEndpoints.cs` each also require the creator-bound order read.
- **UI.** The frontend `OrderTimelinePanel` sits inside the order detail.
- **No migration.**

**Tech Stack:** .NET 10, MongoDB aggregation, Next.js 16, `node:test`, Playwright verify script.

**Spec:** `docs/quality/production-progress.md`, row U05. Candidate notes are in `tmp/u05-timeline-candidate/STATUS.md` and `OWNER-SEAMS.md`.

## Global Constraints

- Apply only the U05 hunks to `InvoiceQueries.cs`, `ShipmentQueryService.cs`, `PaymentDraftService.cs`, `AfterSalesCaseService.cs` and `OrderSubmissionEndpoints.cs`. Main's newer `LineRevision` selection and DTO field stay as they are, because the candidate predates them.
- `OrderHistoryWorkspace.tsx` keeps main's C1/C3 `ListTable` and `EmptyState`. Only the timeline panel mount is added.
- A grouped invoice must not be read as covering every order on it, and a case must not appear for an order it does not report (`ReportedOrderFilter`).
- Private, no-store responses. Refresh or a change of account clears timeline data.

## Review Focus

1. **Another buyer's order ID:** every timeline endpoint denies it, even when the caller can read the referenced invoice through a grouping. Test: `OrderTimelineQueryTests` (candidate).
2. **Grouped invoice:** it appears under each of its orders, labelled with that order's lines only. Test: `OrderTimelineListTests` (candidate).
3. **Pagination witness:** the by-order filter runs before the 26-row limit, so `HasMore` is exact. Test: `OrderTimelineListTests`.
4. **Section failure:** one owner failing shows that section as unavailable, never as empty. Test: `order-timeline.test.ts` (candidate).
5. **Fences:** each section's read touches the same fences as the owner's list. Test: `OrderTimelineFenceTests` (candidate).

---

### Task 1: Owner queries and endpoints

- [ ] Copy the candidate's new files:
  - `OrderTimelineEndpoints.cs`
  - `OrderTimelineHistory*.cs`
  - `PaymentDraftService.Timeline*.cs`
  - `PaymentTimelineOutcome.cs`
  - `AfterSalesCaseService.Timeline.cs`
  - `CaseTimelineOutcome.cs`
  - the five `OrderTimeline*Tests` plus their test-data files
- [ ] Apply the four `ListForOrderAsync` hunks and `app.MapOrderTimelineEndpoints()` by hand.
- [ ] Build. Fix compile drift against main by adapting the new files, never by reverting main.
- [ ] Run `OrderTimeline*Tests` through the runner. Each failure gets a root cause; product defects are fixed test-first.
- [ ] Regenerate `openapi.json` and `schema.ts`.

### Task 2: Frontend panel

- [ ] Copy `order-timeline-client.ts`, `OrderTimelinePanel.tsx` and `tests/order-timeline.test.ts`. Mount the panel in the selected-order detail of main's `OrderHistoryWorkspace.tsx`.
- [ ] Format through `src/lib/format.ts` and show tones through `StatusBadge`.
- [ ] Run the client test, the full frontend suite, tsc and eslint.

### Task 3: Close

- [ ] Run `verify-order-timeline-browser.mjs` in the Phase E browser sweep. Then update the register row, the ledger and the final review, and commit.
