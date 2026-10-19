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
