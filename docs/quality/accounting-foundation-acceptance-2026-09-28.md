# Accounting foundation — local verification, 28 September 2026

Status: local accounting verification and independent source review passed. The complete backend run passed 1,346 cases and exposed three outdated migration allowlists; their precise corrections then passed. The complete publication workflow at 172e5d5 subsequently passed, including all 1,384 combined backend cases. This is the accounting substage of T05A. Full T05A, PAY01 and T12 remain open.

## Delivered behavior

The C# posting writer joins an already authorized business command's Mongo transaction. It loads a registered owning-module source adapter, pins the authoritative source revision, resolves the active configured book/chart/accounts, and writes the journal, period history, accounting audit and outbox together. Tests compose it with the real AuthorizationStore and a persisted synthetic business resource. No genuine procurement or payment integration is claimed.

Operational books are partitioned by organization, shop, channel and currency. Warehouse, buyer, actor and grant shape cannot create another accounting fence. Missing or inactive configuration denies. Entries preserve immutable source, dimensions, date, chart/account snapshots and balanced decimal lines. Exact historical reversal creates a separate linked entry and preserves the original.

A duplicate source aborts the whole supplied transaction, even when its owner catches the error after attempting another business effect. Same-content and changed-content duplicates have distinct typed conflicts. The outer authorized command still returns its original receipt for an exact request replay. Required history/audit/outbox failures also roll back business effects; uncertain commits recover the original receipt.

One book fence coordinates posting and internal period closure. Closed-through protects earlier months; backdated entries invalidate later review snapshots. Internal close verifies stored approval history and a current cumulative journal digest. It is storage infrastructure, without a public approval route, person-independence proof or completed reconciliation workflow.

Cumulative snapshots stream indexed journal batches rather than loading all history into memory. Their totals use checked decimal capacity, separate from the per-journal Money cap. The 70-row multi-batch test proves bounded cursor use and stable ordering; realistic throughput and transaction-duration acceptance remain later work.

## Migration and review

Migration v12 adds strict books, immutable chart versions, journals, periods, period history and accounting audit validation. Preflight checks data, relationship graphs, canonical fingerprints, reversal links, stage/audit provenance, exact supported index semantics and allocator high-water marks before target changes. Unknown or malformed historical data fails without invented backfill. Applied v1–v11 remain unchanged; retained additional validators and the supported legacy source index are preserved. Interrupted application resumes after lease expiry; counters never decrease or reuse IDs.

Independent review findings were repaired with recorded regressions: currency newline acceptance, reversal null guards, missing fingerprint recomputation, illegal first-close history, missing/forged audits, extreme BSON dates, and a forged period audit attached to submitted history. Root reviewed the indexed incremental snapshot fix. Final scoped source reviews found no remaining concrete defect in the reviewed boundaries.

## Evidence

Artifacts are under tmp/production/accounting-foundation-results/ in the managed production worktree.

| Verification | Result |
| --- | --- |
| Focused accounting suite, accounting-focused-green.trx | 62 passed, 0 failed/skipped, about 1m13s |
| Strengthened revocation composition, revocation-composition-final.trx | 1 passed; this reruns one of the same 62 cases |
| Full backend suite, backend-full.trx | 1,346 passed, 3 failed, 0 skipped / 1,349 total; 31m53s. All 62 accounting cases passed. |
| Corrected current-chain migration fixtures, historical-chain-v12-green.trx | 3 passed, 0 failed/skipped, 7s; full solution compilation succeeded |
| Independent runtime/migration and root snapshot review | Scoped source review passed after repairs |
| Owned source manifest, owned-files-sha256.json | 24 accounting/registry/test C# files; root verified every digest |

The suite covers real transaction rollback, source/configuration denial, duplicate actors/keys, exact reversal, close/post ordering, source revision races, revoked authority, final session expiry, uncertain commit, strict BSON and migration failure/resumption. The focused run initially failed two fixture assumptions: an administrator's legitimate revocation receipt was counted, and simulated node recovery prevented immediate lease release. The fixes scope the denied command's receipt and advance the synthetic clock past lease expiry. Financial assertions remain intact.

The full-run failures were CatalogVisibilityMigrationTests.Interrupted_v6_retains_stronger_rules_and_alternate_indexes_then_resumes_registered_chain_with_majority_ddl, HistoricalReadMigrationTests.Interrupted_v7_preserves_stronger_validator_alternate_index_and_resumes_with_majority_ddl, and ProviderLifecycleMigrationTests.Interrupted_v5_preserves_stronger_rules_resumes_only_v5_and_all_ddl_is_majority. These fixtures resume the entire current chain and their DDL allowlists ended before v12. Only the six exact accounting collection names were appended; independent review confirmed majority, stronger-validator, alternate-index, history and allocator assertions remain unchanged, as do applied migrations v1–v11. The targeted correction is separate evidence and does not turn the original full run green.

The earlier GitHub workflow at 6ddf1e6 passed 1,287 baseline backend tests, but predates this implementation and is not accounting acceptance evidence.

## Required continuation

No production source adapter, public accounting route, chart/period provisioning, finance grant, real-person approval, live data migration or production activation was added. Genuine procurement composition, private evidence/OCR, invoice/payment integration, source reconciliation, manual correction/reopen, reporting/UI, capacity and restore rehearsals, hosting and the real branch pilot remain required. All 97 report production acceptance rows and the complete goal remain open.

References: [accounting execution plan](../superpowers/plans/2026-09-28-accounting-foundation.md), [production plan](../superpowers/plans/2026-09-27-unit-toko-production.md), [report requirements](../requirements/unit-toko-report-2026-09-28.md), [private evidence contracts](../requirements/private-evidence-contracts.md).
Publication acceptance: [GitHub run 36387677756](https://github.com/natannielz/Capstone/actions/runs/36387677756) passed at 172e5d5feb3a5e555dafca209f6df8ebb0fd98b1. Backend 1,384/1,384 passed, zero failures/skips, 20m47s; build zero warnings/errors; Auth0 Action 5/5; both compiled/generated contract checks passed. Frontend 453+6+7 passed; types, lint and production build passed (nine existing lint warnings, zero errors). Exact full log is tmp/production/github-publication-results/run-36387677756-full.log. This covers accounting and the 35-case evidence prerequisite, not subsequent Catalog upload/store changes.
