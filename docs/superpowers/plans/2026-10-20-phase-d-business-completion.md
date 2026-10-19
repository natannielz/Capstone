# Phase D: Business Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the business gaps that still block acceptance:
- the full buyer journey in a real browser;
- the approved D02 transient-data purge;
- the field workflows (scanning and blind stock count);
- the remaining "Muat …" clicks on payment review;
- conformance proof for the approved D01–D08 rules.

**Architecture:** This adds no new domain owners. The purge is a Worker job owned by each transient collection's domain, with the shared schedule in `Platform/Retention`. Scanning is one frontend hook (camera BarcodeDetector → `barcode-detector` WASM fallback is out of scope until owner download approval → keyboard wedge → manual entry), used by the existing receiving, picking and count workspaces. Journey acceptance reuses the existing browser test harness.

**Tech Stack:** .NET 10 Worker, MongoDB 8.2, Next.js 16 / React 19, and the existing Playwright browser harness (backend `*BrowserTests` + `frontend/scripts/verify-*-browser.mjs`).

**Spec:** `docs/superpowers/specs/2026-10-19-production-completion-program.md` (Phase D row). Approved rules: `docs/quality/report-decisions.md` ("D01–D08 proposals approved (16 October 2026)"). Field UX: `reports/Unit Toko UI UX improvements.md` → "Field workflows must assume Android phones, weak signal and gloves".

## Global Constraints

- Only transient items are ever purged: drafts never issued, rejected or duplicate uploads, abandoned imports. A soft delete stays restorable for 30 days, then the item is purged.
  - Issued records (invoices, faktur data, Surat Jalan, payment proofs, journals, statements, counts) are never deleted by this job. Every purge rule names its collection and predicate and is test-proven not to match issued records.
  - Audit events and seals are never purged.
- Never weaken the 60 s transaction deadline, the 30 s browser bound, or existing integrity/tamper tests.
- No new npm or NuGet dependency without owner approval (downloads). The camera path uses the native `BarcodeDetector` where it exists; otherwise there is a keyboard-wedge/manual fallback.
- Bahasa Indonesia "Anda". Field targets are ≥48 px. Respect reduced motion.
- Run tests in batches (16 GB machine).

## Review Focus

1. **A purge predicate that matches an issued record** because of a missing status condition. Every rule gets a negative test using a real issued fixture.
2. **A clock-skewed or never-set `deletedAt`** must not purge early. Rows without a valid `deletedAt` are never purged.
3. **Scanner hook:** a double-fire from a hardware wedge (Enter repeated within 100 ms) must not count twice.
4. **Blind count:** the expected quantity must not leak through the API response, page source or accessibility tree before submission.
5. **Payment panel auto-load:** a late reply after switching submission must never render into the newly selected one.

---

### Task 1: Approved-rule conformance tests (D01/D02)

**Files:** `backend/tests/UnitToko.Tests/Identity/ApprovedRuleConformanceTests.cs`

- [ ] Pin these existing guarantees so they can't regress:
  - The `system-admin` template contains no business permission. Every permission in it is categorised Administrative.
  - Access and membership approval reject the proposer (existing service path).
  - Disbursement recording rejects the funding approver's canonical person.
  - A Surat Jalan number belongs to the dispatch and issuance never allocates a new one.
- [ ] Expected: tests pass on first run. They pin behaviour that already exists. For each, record the production line that would make it fail.

### Task 2: Transient-data purge (approved D02 record classes)

**Files:**
- Create: `backend/src/UnitToko.Platform/Retention/TransientPurge.cs` (rule contract + schedule) and one rule per owning domain for the collections it owns that hold transient items. Candidates, each to be confirmed by reading its owner:
  - unsubmitted order drafts (`ordering/drafts`);
  - unsubmitted payment drafts;
  - rejected or duplicate private evidence uploads that never became a record;
  - abandoned import staging.
- Create: `backend/src/UnitToko.Worker/TransientPurgeWorker.cs` (daily, bounded batch)
- Test: `backend/tests/UnitToko.Tests/Retention/TransientPurgeTests.cs`

- [ ] Write tests for each rule:
  - it purges a soft-deleted transient row older than 30 days;
  - it keeps one younger than 30 days;
  - it keeps an issued or submitted record with an old `deletedAt` (negative);
  - it keeps a row without `deletedAt`;
  - the batch is bounded;
  - an audit event records counts per rule.
- [ ] Implement, register the Worker, and add the runbook section to `docs/runbooks/local-development.md` → "Retention".

### Task 3: Payment review panels load on open

**Files:** `PaymentReviewActionPanel.tsx`, `PaymentDuplicatePanel.tsx`, `PaymentVerificationPanel.tsx`, `frontend/scripts/verify-payment-verification-browser.mjs`

- [ ] Each panel runs its existing guarded `load` once on mount. Panels are keyed by submission id and revision, so a remount discards late replies. The button becomes "Muat ulang …".
- [ ] Update the verify script to wait for the auto-loaded content instead of clicking the first load. Run `PaymentVerificationBrowserTests` after its baseline classification (Phase A).

### Task 4: Scanner hook and blind stock count

**Files:**
- Create: `frontend/src/components/workspace-kit/use-scanner.ts`, `frontend/tests/use-scanner.test.ts`
- Modify: `frontend/src/modules/inventory/StockCountWorkspace.tsx`
- Modify: the receiving and picking scan screens that already exist (`verify-operational-scan-*` flows)

- [ ] Test the pure `scanBuffer` reducer:
  - keystrokes within 50 ms followed by Enter form one code;
  - a repeat of the same code within 100 ms is ignored;
  - manual input is passed through;
  - a GTIN check digit is validated using the existing identifier rules.
- [ ] Implement `useScanner({ onCode, camera })`. Camera mode uses `BarcodeDetector` when `'BarcodeDetector' in window` and offers manual entry otherwise. Feedback is a short beep plus a visual flash; vibration only when the API exists.
- [ ] Stock count defaults to blind counting: the expected quantity is not requested until the count is submitted for supervisor approval (Review Focus 4). Check that against the existing count API.

### Task 5: Full buyer journey acceptance (V06/V07/F03)

- [ ] On the full candidate, run `OrderSubmissionBrowserTests`, `ApprovedOrderDispatchBrowserTests`, `InvoiceBrowserTests` and `PaymentVerificationBrowserTests` in one batch, then `OrderSubstitutionBrowserTests`. Each failure gets root-cause analysis (`superpowers:systematic-debugging`) and a test-first fix.
- [ ] Record per-request latency through a temporary timing wrapper that is removed before commit, and compare against the targets.

### Task 6: Close Phase D

- [ ] Register rows, ledger, final review (Critical/Important RED→GREEN), commit and push.
