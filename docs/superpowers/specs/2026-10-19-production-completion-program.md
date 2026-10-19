# Production completion program

Status: approved by the owner on 2026-10-19 ("Approve, run all"). Phases A–E run in order without a stop per phase; each phase is reported when it ends. Sources: the 97-item acceptance register in [production-progress.md](../../quality/production-progress.md), the approved D01–D08 proposals in [report-decisions.md](../../quality/report-decisions.md), and the UI/UX research report (`reports/Unit Toko UI UX improvements.md`, five-phase roadmap).

## Where the application stands

The register has 97 acceptance items. Implementation is complete for 5 items, partial for 87 and unproven for 5. Local verification has the same shape. No item has staging or production evidence, because hosting (D07) is not yet decided. Most of the 87 partial items fall into a few gaps:

- the full order-to-dispatch browser journey still fails (F03, V06, V07, U04, Q05);
- workspaces look and behave inconsistently (U01–U04, U06, U08, Q02);
- several cross-module features are still candidates or unproven (F12, U05, M02, R11);
- operations hardening is missing (Q09, Q10, Q12, read latency);
- the rest need real-world inputs (D01–D08).

## Phases

Each phase gets its own plan under `docs/superpowers/plans/`. It runs test-first, ends with one review by the most capable model (Critical and Important findings are fixed RED→GREEN), and is committed and pushed. Phases A–E need no external input. Phase F does.

| Phase | Scope | Register items | Exit evidence |
| --- | --- | --- | --- |
| A — Close slice 3 | Classify the regression, fix real regressions, remove TEMP-TIMING, measure latency, commit/push, final review | F03, Q06 | Regression classified against the `ce1a0ac` baseline; review findings fixed |
| B — UI foundation | <ul><li>Playwright screenshot and axe safety net</li><li>Move the 24 legacy stylesheets into a low-priority cascade layer and remove `!important`</li><li>Semantic OKLCH tokens that meet WCAG 2.2 AA contrast; one `next/font` family with tabular figures</li><li>One formatting module for IDR (`Rp1.250.000`), dates and WIB</li><li>One status dictionary per record type (code, label, tone, icon, who acts next), replacing styles keyed on label text</li></ul> | Q02, U08, U06 (part) | Screenshot diffs reviewed; axe has zero serious violations on the core screens; contrast pairs ≥4.5:1 (text) and ≥3:1 (focus, UI) |
| C — Shell and shared patterns | <ul><li>Role-based home screens and domain sidebar, with a Ctrl+K palette as an accelerator</li><li>Shared table contract: density, saved views, batch bar</li><li>Review panel that is itself the confirmation, with a visible self-approval guard and a stale-content warning</li><li>Outcome states: empty, loading, denied, offline, conflict, and "Belum pasti" with "Periksa hasil" (original key)</li><li>Form rules: validate on blur, rupiah as integers</li><li>"Pekerjaan saya", one queue across owners ordered by deadline</li></ul> | U01–U04, F12, U02 | Every workspace uses the shell and patterns; the shared queue covers payment, order, dispatch and return work |
| D — Business completion | <ul><li>Full order-to-dispatch-to-invoice-to-payment browser journey green</li><li>Order timeline across modules (U05 candidate)</li><li>Delivery-document OCR (M02 candidate)</li><li>Join/move/leave and access review (R11)</li><li>Approved rules:<ul><li>Super Admin who administers but cannot transact or hard-delete</li><li>Record-class soft delete with 30-day purge for transient items and 10-year archive for issued records</li><li>Void instead of delete</li><li>Kepala Toko review of the SPH before Staf Toko pays OMI</li><li>Numbered Surat Jalan</li></ul></li><li>Field workflows:<ul><li>Screenshot-first proof upload with OCR-safe compression</li><li>OCR review screen with word boxes and a combined confidence score</li><li>One scanner hook (camera, keyboard wedge, manual) with the scan-increment-validate model</li><li>Delivery proof bound to identity and the Surat Jalan</li><li>Blind stock count with separate approval</li></ul></li></ul> | V06, V07, F03, F09, F10, F12, U05, M02, M05, P02, P03, R11, I08 (extend) | Browser journeys pass on the full candidate; new rules each have native, client and browser tests |
| E — Production hardening | <ul><li>Read-path latency toward p95 ≤500 ms locally on the hot reads</li><li>Monitoring and operator diagnostics (Q10)</li><li>Restore drill (Q09)</li><li>Reproducible release build/CI without legacy (Q12)</li><li>Retire stale test debt: migration-version pins and fixture drift</li><li>OWASP ASVS 5.0 L1 pass with MFA on privileged roles</li><li>Whole-suite green</li></ul> | Q05, Q06, Q09, Q10, Q12, S04 | Full native, client and browser suites green; restore drill timed; latency report |
| F — External (blocked) | Hosting and staging, Auth0 tenant, real master data and migration, pilot and cutover, domain tunnel | D01–D08 gates; all staging and production columns | Owner inputs listed in the decision ledger |

## Rules carried through every phase

- Stage explicit paths only. Never stage the user's untracked `docs/proposal-completion-plan.md`.
- Never weaken the 60 s transaction deadline or the 30 s browser bound.
- Existing tamper and integrity tests keep passing.
- Bahasa Indonesia "Anda" register, EYD V formatting, WCAG 2.2 AA.
- Synthetic fixtures never become real approvals.
- Run long test suites in batches. The local machine has 16 GB of RAM, and an unbatched run crashed the test host and Mongo.
