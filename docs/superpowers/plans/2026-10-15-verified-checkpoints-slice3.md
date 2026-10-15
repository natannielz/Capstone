# Verified Checkpoints Slice 3 — Order-Progress Seals (phase 1d)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the per-read full replay of an order's decision/substitution/dispatch history (`OrderSubmissionService.DecisionGraphCoreAsync`), which every order, substitution-target and dispatch read performs, so scoped reads approach the ≤500 ms target (measured on main after slice 2: dispatch list 5.0 s, substitution targets 5.2 s, order list 2.4 s, substitution preview 8.7 s).

**Architecture:** Unlike slices 1–2, order progress is a growing aggregate. A seal therefore binds a *checkpoint*: the order row hash, the exact rows of the history prefix up to decision revision R and dispatch sequence D (decision rows, substitution decisions, shipment rows, each by `_id` with hash), the reservation rows' state at that checkpoint, and the derived replay state (`facts`: revision, spending state, accepted/rejected suborders, cancelled, dispatch sequence, last time, current catalog/terms hash, spending requester, per-reservation remaining/released/dispatched). Verification reads the live history; rows that are part of the sealed prefix must match exactly (else refuse + alert — tamper); rows beyond the prefix (higher revision / sequence) are replayed incrementally from the sealed `facts` with the existing per-row checks; reservation state is re-derived and compared to live rows exactly as today. After a successful replay in an owning operation the seal advances to the new checkpoint. An absent seal runs today's full replay, then seals.

**Tech Stack:** C#/.NET 10, MongoDB 8.2 rs0, xUnit native tests via `tmp/production/run-full-migration-checks.ps1`, R02 browser gate.

**Spec:** `docs/superpowers/specs/2026-10-12-verified-checkpoints-design.md` (phase 1d row of §3.2; §3.3 read path; §3.4 write path).

## Global Constraints

- Fail closed: a prefix row that differs, a missing prefix row, or a forged seal refuses (`order_unavailable`) and raises `SealIntegrityAlerts`; never re-replay around it. Rows beyond the prefix are not a refusal — they are verified incrementally.
- Only owning unit-of-work operations write or advance seals; caller-supplied sessions never write.
- Seal storage is Ordering-owned (`orderProgressSeals`, migration 117); the shared primitive gains only an optional MAC-covered `facts` document.
- Never weaken the 60 s transaction deadline or the 30 s browser action bound; existing tamper tests keep passing.

## Review Focus

1. A new decision, substitution or dispatch after sealing: incremental replay from `facts` must produce exactly what the full replay produces (equivalence test over the R02 service flow).
2. Reservation state is mutable: it is never trusted from the seal — re-derived from `facts` + incremental rows and compared to live rows, as the full replay does.
3. Concurrent advancing: two operations advancing the same order's seal → write conflict → retry; never two seals for one order.
4. A tamper inside the prefix (edit/delete/insert a decision or shipment at ≤ R/D) refuses with an alert; insertion of a row at a prefix revision is detected by the prefix count.
5. `facts` drift: if replay code changes semantics, old `facts` would be wrong → seals carry a `factsVersion`; a mismatch is treated as absent (full replay, re-seal), never trusted.

---

### Task 1: `facts`-bearing seals and migration 117
- [ ] RED: `ProvenanceSealTests` — a seal with `facts` verifies; editing any `facts` field breaks the MAC; seals without `facts` (existing kinds) are unchanged. `OrderProgressSealMigrationTests` — v117 creates strict empty `orderProgressSeals` (validator admits `facts`, `factsVersion`), reapply preserves.
- [ ] GREEN: `ProvenanceSeal.Create(..., BsonDocument? facts = null)` (payload includes `facts` only when present); `Ordering/OrderProgressSealSchemas.cs`; `Mongo/Migrations/OrderProgressSealMigration.cs` (v117).

### Task 2: Checkpoint seal and incremental replay
- [ ] RED (`Ordering/OrderProgressSealTests.cs`, R02 fixture `OrderSubstitutionTestData`): owning read seals; second read does not run the full replay (counter `OrderProgressSeals.FullReplays`); a later decision/dispatch advances via incremental replay with progress equal to a fresh full replay; prefix tamper (decision row edit, shipment row delete) refuses with alert; forged MAC refuses; `factsVersion` mismatch → full replay + re-seal; caller session never writes.
- [ ] GREEN: refactor `DecisionGraphCoreAsync` into `ReplayAsync(state, rowsFrom)` with an explicit `ReplayState` record (the current locals), so full and incremental replays share one code path; `OrderProgressSeals.VerifyAsync/AdvanceAsync`.

### Task 3: Latency acceptance and close
- [ ] Browser (main, temporary local timing only): order list, substitution targets and dispatch list each ≥50 % faster than slice-2 numbers; record p50/p95 over the run.
- [ ] Remove temporary code; gates (seal/migration/R02/browser suites; frontend unchanged); ledger; commit explicit paths; push; Vercel; final review (most capable model), fix Critical/Important RED→GREEN.
