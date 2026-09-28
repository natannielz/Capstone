# Private evidence prerequisite — 28 September 2026

Status: pure domain prerequisite locally verified, independently reviewed and published in GitHub checkpoint 172e5d5. PAY01 remains open; no upload, storage, scanner, decoder, evidence migration, endpoint, association or worker activation is supplied by this prerequisite.

EvidenceRef validates immutable positive lineage/version identity and canonical lowercase SHA-256. It explicitly differs from the separately allocated future Mongo version-row ID. EvidenceTargetRef admits only explicit Catalog family/SKU target identities; ownership and current permission remain the owning module's responsibility. Configured image limits enforce actual positive byte/dimension/pixel limits and one frame/page, using Int64 pixel multiplication. Missing or invalid limits deny construction.

The exact initial payment media predicate requires present, validated, clean JPEG/PNG evidence. It does not verify that funds arrived or confer any upload, read, association or approval authority. Full decoding, scanner provenance, exact stored generation, transactional pins and current owning-target authority remain runtime requirements.

| Evidence | Result |
| --- | --- |
| domain-red.trx, isolated evidence checkout | 35 expected failures against explicit missing-behavior stubs |
| domain-green.trx, isolated evidence checkout | 35 passed, 0 failed/skipped, 64ms |
| Independent source review | Scoped pass on the two-file domain/test boundary; no independent rerun claimed |
| domain-integrated-green.trx, production checkout | 35 passed, 0 failed/skipped, 51ms, after compiling the combined accounting/evidence solution |
| GitHub publication | Domain commit 8d747f9 integrated at 172e5d5; [complete workflow](https://github.com/natannielz/Capstone/actions/runs/36387677756) passed: 1,384 backend cases, frontend tests/build and contract checks |

Artifacts live under tmp/production/private-evidence-results/ in the indicated managed worktrees; the integrated build log is tmp/production/accounting-foundation-results/evidence-integrated.log. Focused tests do not establish a whole-suite green run.

Next implementation follows the [reviewed contracts](../requirements/private-evidence-contracts.md) and [PAY01 plan](../superpowers/plans/2026-09-28-report-payments-ocr.md): durable authorized reservations, real immutable storage and uncertain-outcome recovery, quarantine, bounded local decoding/scanning, exact Catalog associations, current authorization at byte delivery and coordinated tombstone cleanup. No source path, supplied hash, fake clean default or object existence alone may become trusted evidence. Full PAY01, all 97 report production acceptance rows and the complete production goal remain open.

## Catalog upload target continuation — local verification passed

A narrow same-session Catalog helper now reuses the existing graph, ownership and final authority/session checks for upload metadata. It accepts active unpublished families/SKUs, validates the owning database/client and optional expected revision, and provides safe immutable server-loaded facts. It commits nothing itself. Callback or final-fence failures abort all caller effects even if the caller catches the error; current-definition changes are fenced through the mutable SKU head, without adding mutable fields to immutable definitions.

The expanded 22-case target suite passed, including concurrent definition replacement, actual session revocation, last-await expiry, cancellation, malformed graphs and rollback. Independent review found and reproduced an outer transaction-cleanup edge: a deliberately disposed failed handle masked the original error. Two regressions failed before the fix, then passed with exact exception identity, transient retry and one committed effect. Final scoped source review passes. The broader affected Catalog, accounting, persistence/conflict and new-case regression passed 145 cases, zero failures/skips, in 3m16s (target-regression.trx). This continuation is locally accepted and committed as e170077; publication uses the existing production branch and a new complete workflow. The successful earlier 172e5d5 workflow does not cover these files.

The helper has no endpoint, storage I/O, upload reservation, audit/receipt producer, association or buyer-read capability. Documents must compose it inside its actual durable authorized command. This is additional PAY01 implementation progress, with every remaining runtime and production gate preserved.
