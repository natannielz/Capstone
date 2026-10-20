# Phase D3: Integrate the Remaining Feature Candidates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the authored-but-unintegrated Phase D features into main, in dependency order, each compiled, tested and reviewed:
- invoice release approval;
- the order timeline (U05);
- delivery-document OCR (M02);
- courier task handover;
- work-queue monitoring (Q10/F12 seeds);
- access review and branch moves (R11).

**Architecture:** Each candidate in `tmp/*-candidate` was cut from an older main (111–117). Integration rules:
- Copy the candidate's new files.
- Apply only its narrow deltas to shared files. Main's newer changes stay, including R02 line revisions, seals 114–117, the D2 discard (118), Phase B/C UI and the C2 work sources.
- Give each migration the next free version against its actual predecessor.
- A candidate test that fails gets a root cause; product defects are fixed test-first.

**Tech Stack:** .NET 10, MongoDB 8.2 rs0, Next.js 16, the runner `tmp/production/run-full-migration-checks.ps1`.

**Spec:** Program spec Phase D row (`docs/superpowers/specs/2026-10-19-production-completion-program.md`). Register rows U05, M02, F10, F12, Q10 and R11. Candidate handoffs are `tmp/invoice-admission-candidate/HANDOFF.md`, `tmp/u05-timeline-candidate/STATUS.md` and `tmp/u05-integration-wip/README.md`.

## Global Constraints

- The migration registry stays contiguous (`steps == 1..CurrentVersion`). No placeholder or marker migrations.
- A candidate never replaces a whole shared file. Narrow deltas only, and each is checked by diff against the candidate after it is applied.
- New permissions are added to role templates only through the approved D02 matrix. Super Admin gets no business permission (`ApprovedRuleConformanceTests`).
- Bahasa Indonesia copy, `format.ts`, `StatusBadge`, the `workspace-kit` patterns.

## Review Focus

1. **Migration renumbering:** a candidate migration that checked "history.Count == N" against its old number must check its actual new predecessor. Test: each migration's own test at the new number.
2. **Invoice release maker/checker:** the proposer cannot approve. Approval requires current, independent person evidence. Test: `InvoiceReleaseRuleTests` (candidate).
3. **Timeline privacy:** another buyer's order ID is denied on every section. Test: `OrderTimelineQueryTests` (candidate).
4. **OCR suggestions stay suggestions:** a delivery-document OCR quantity never becomes receipt consent. Test: the M02 candidate's service tests.
5. **Courier handover:** an abandoned task cannot be completed by the previous courier after the handover. Test: the courier candidate's tests.

---

### Task 1: Invoice release approval (`tmp/invoice-admission-candidate`)

- [ ] Apply the nine shared-file deltas listed in `SHARED_BASELINES.json` as narrow hunks, then copy the 20 new files.
- [ ] Register `InvoiceReleaseMigration` as the next free version, currently 119.
- [ ] Build. Run `InvoiceRelease*Tests` and the migration test. Regenerate contracts. Frontend client test, tsc, lint.

### Task 2: U05 order timeline (`tmp/u05-integration-wip`)

- [ ] Reapply `tracked.patch` and the 21 files. Add the two missing test `using` lines.
- [ ] Run `OrderTimeline*Tests`. Mount `OrderTimelinePanel` in main's `OrderHistoryWorkspace.tsx`, which already has ListTable and EmptyState.

### Task 3: M02 delivery-document OCR (`tmp/m02-main114-validation-candidate`, newest)

- [ ] Diff against main, apply deltas, renumber its migrations (it used 112–115) to the next free versions, run its native, client and browser checks.

### Task 4: Courier task handover (`tmp/courier-handover-candidate`)

- [ ] Same procedure. It reserved 118, which now belongs to D2, so it takes the next free version.

### Task 5: Work-queue monitoring (`tmp/work-queue-monitoring-candidate`)

- [ ] Compare with the C2 `WorkSummaryService`. Keep C2's `IWorkSource` contract and take only the monitoring and reminder pieces that are not already in main.

### Task 6: R11 access review and branch moves

- [ ] Design spike: quarterly access-review campaign, where each grant needs an accountable reviewer's attestation and lapses are revoked through the existing grant-revoke command. Branch move is a membership transfer with history. Then a plan and TDD.

### Task 7: Close

- [ ] Register rows, ledger, final review, commit, push.

---

## Execution notes (20 October 2026)

- **Task 1, invoice release:** applied the nine shared deltas as narrow three-way merges (candidate base: main 9480cb4 and R02 draft4 for billing) and copied 27 new files. Registered as migration **119**. Compile fixes were namespace imports only. Ruling: the binding gate now treats a database as release-installed when its history reaches 119, bounded by today's `CurrentVersion`. The candidate pinned "exactly 116", which would break at the next migration. If wrong, the cost is one comparison. Ruling: the candidate rule test compared `ImmutableArray` by reference; it now compares the sequences. Ruling: the fixture orders through production admission with `reviewedSafetyBufferBaseUnits: 0`, as main's admission tests do, because the default reviewed buffer holds the single package back by design.
- **Task 2, U05 timeline:** applied `tracked.patch` and the 21 files. U05's `InvoiceReleaseTestData` superset replaces the invoice candidate's copy (renumbered to 119). Added the three missing test `using` lines. Mounted `OrderTimelinePanel` in `OrderHistoryWorkspace`; dates use `format.ts`.
- **Task 4, courier handover:** exact baselines gave clean three-way merges for 18 of 21 shared files. The registry, navigation and destination lines were applied by hand. Registered as migration **120**. Ruling: `ServerSession.TransactionNumber` (absent in driver 3.x) became `WrappedCoreSession.CurrentTransaction?.TransactionNumber`, as `ShipmentDispatchFiniteSource` does. Ruling: the return-path allowlist bound rose from 64 to 96 to admit `/billing/invoice-releases` and `/fulfillment/courier-assignments` on both frontend and backend.
- **Task 3, M02:** deferred. The newest composition's README says the printed Surat Jalan table parser is incomplete and no native/browser run exists. Integrating it would ship a known-incomplete OCR path.
- **Task 5, work-queue monitoring:** deferred. The candidate (174 files) registers adapters for R11 access campaigns that do not exist yet, and needs its own migration after 120.
- **Task 6, R11:** design spike written for owner review in `docs/superpowers/specs/2026-10-20-r11-access-review-and-branch-moves-design.md`. It ends with five policy decisions; implementation waits for them.
