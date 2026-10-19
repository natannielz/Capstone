# Phase E: Production Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A whole-suite-green candidate with:
- measured hot-read latency;
- operator health signals;
- a timed restore drill;
- a reproducible release build;
- an OWASP ASVS 5.0 Level 1 pass with MFA on privileged roles.

**Architecture:** No new business features. Every failing test gets a recorded root cause and one of three outcomes:
1. fixed RED→GREEN;
2. rewritten so it tests the same guarantee at the current schema;
3. retired with a ledgered ruling, only when the guarantee no longer applies to any deployable state. Example: an upgrade path from a schema no environment can still hold.

Health and restore tooling live beside the existing runbooks and worker.

**Tech Stack:** .NET 10 (xUnit), MongoDB 8.2 replica set (`.tools` runtime), Next.js 16, Playwright verify scripts, GitHub Actions (`.github/workflows/quality.yml`, `release-validation.yml`).

**Spec:** `docs/superpowers/specs/2026-10-19-production-completion-program.md`, Phase E row. Register rows Q05, Q06, Q09, Q10, Q12 and S04 in `docs/quality/production-progress.md`.

## Global Constraints

- Never weaken the 60 s transaction deadline, the 30 s browser bound, or any tamper or integrity assertion.
- Run long suites in batches (16 GB RAM). Use `tmp/production/run-full-migration-checks.ps1`, which provides the OCR, evidence and browser environment. A direct `dotnet test` without it fails environment-dependent fixtures, and those failures are not product results.
- No real data, secrets or hosted services. A restore drill uses synthetic databases only.
- A retired test needs a ruling naming the guarantee, why no deployable state can reach it, and what covers the current path.

## Review Focus

1. **A retired migration test hides a live upgrade path.** Every retired rewind test names the current-path test that covers the same collection. Check: the `retired-tests.md` table has no empty "covered by" cell.
2. **A health check leaks business data.** Health output carries counts, ages and states only, never names, amounts or IDs. Test: `health_report_contains_no_business_payload`.
3. **A restore that "succeeds" with missing evidence objects.** The drill verifies counts and hashes for Mongo collections and evidence storage, and fails on any mismatch. Test: `restore_drill_detects_a_missing_object`.
4. **Latency measured on a cold or overloaded machine.** Latency runs only on an idle machine and records CPU load. p95 comes from at least 20 samples per route, with warm-up excluded.
5. **An MFA bypass for privileged roles.** A system-admin or approver session without MFA is refused on privileged commands. Test: `privileged_command_requires_multi_factor`.

---

### Task 1: Classify and retire stale test debt (backend)

**Files:**
- `docs/quality/retired-tests.md` (new)
- The affected test files

**Debt:**
- the migration-rewind helpers (`RestoreV71Async`, the 77–80 pins in `ProcurementQualityAcceptanceMigrationTests` and `ProcurementQualityValuationMigrationTests`, and their callers);
- the `ReturnLotBrowserTests` version-95 pin;
- the 35 baseline failures from `tmp/production/slice2-baseline-recheck39.log`.

- [ ] For each failing test, record the test name, the root cause from its log, and its outcome (fix / rewrite / retire) in `retired-tests.md`.
- [ ] Rewind tests: make the history and version checks version-agnostic where the test's guarantee still holds at the current schema. Otherwise retire with a ruling. Run each changed class through the runner. Expected: PASS.
- [ ] Real defects found: fix with a failing test first.

### Task 2: Frontend legacy test debt

**Files:** the 11 failing legacy tests (`old entrypoints` ×2, `v7 …` ×8, `v15 catalog …`). Reuse `tmp/frontend-legacy-test-isolation-candidate` after diffing it against main.

- [ ] Classify each failure: deleted source, esbuild build import, or a real regression. Fix or retire with a ruling.
- [ ] Expected result: full frontend suite 0 failures.

### Task 3: Browser sweep

- [ ] Run every `*BrowserTests` class in batches through the runner. Each failure gets a root cause and a fix, test-first where it is a product defect.
- [ ] This covers Phase D Task 5: order → dispatch → invoice → payment journey, and substitution.
- [ ] Re-capture the visual baseline (360/768/1440) and compare.

### Task 4: Hot-read latency

**Files:**
- `tmp/production/latency-sampler.mjs` (temporary, not committed)
- `docs/quality/latency-report-2026-10.md`

- [ ] On an idle machine, sample at least 20 warm requests each for order view, payment review queue, dispatch preview, Pekerjaan saya and the drafts list. Report p50 and p95 against ≤500 ms (reads) and ≤1 s (writes).
- [ ] For each route over target: profile, fix test-first (keeping integrity assertions), re-measure.

### Task 5: Operator health (Q10)

**Files:**
- `backend/src/UnitToko.Platform/Operations/HealthReport.cs`
- `backend/src/UnitToko.Api/Operations/HealthEndpoints.cs`, exposing `POST /api/v1/operations/health/query` with the `technical.configure` permission
- `frontend/src/modules/operations/HealthWorkspace.tsx`
- Tests
- `docs/runbooks/operations-health.md`

Signals:
- outbox pending/failed count and oldest age;
- OCR jobs queued/failed and oldest age;
- draft purge last run;
- migration current/target;
- Mongo primary and replication lag.

- [ ] Write tests: counts and ages only (Review Focus 2); an actor without `technical.configure` is denied. Expect FAIL, implement, expect PASS.

### Task 6: Restore drill (Q09)

**Files:**
- `backend/tools/restore-drill.ps1`
- `docs/runbooks/restore-drill.md`
- Test: `backend/tests/UnitToko.Tests/Operations/RestoreDrillTests.cs`

- [ ] Write the test for Review Focus 3, using a synthetic DB and evidence root: a missing object makes verification fail. Implement the verifier as hashes per collection and evidence object.
- [ ] Run the drill end to end against a synthetic database: `mongodump` → restore into a fresh DB → verify. Record the timing.

### Task 7: Reproducible release build and CI (Q12, Q05)

- [ ] Release build from a clean checkout with pinned dotnet and node and no legacy toolchain. Reuse `tmp/next-tooling-retirement-candidate` after diffing.
- [ ] `quality.yml` runs: backend build, unit (non-Mongo) tests, frontend tsc, lint and unit tests, and the contracts check.

### Task 8: ASVS 5.0 L1 and MFA (S04)

**Files:** `docs/quality/asvs-l1-2026-10.md`; tests per gap

- [ ] Walk ASVS 5.0 L1 against the code: auth, session, access control, validation, cryptography, errors, data protection, config. Record each requirement as pass or gap with evidence.
- [ ] Fix each gap test-first, including Review Focus 5.

### Task 9: Close

- [ ] Full backend (batched), frontend and browser suites green. Register rows, ledger, final review, commit and push.
