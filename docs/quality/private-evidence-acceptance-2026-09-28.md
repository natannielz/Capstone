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

## Private local storage continuation — reviewed and integrated

Storage commit c5792c0 adds five internal adapter/contract files and two test files. The adapter uses create-only part/ready/original files, actual bounded streaming hashes, complete EOF, flush/close/reopen verification, exact expected-generation recovery and a held read-only verified stream. Observed Windows root/file ownership and ACL checks reject unsupported or unsafe configurations. Default storage and non-Windows activation remain unavailable; no API or production registration is added. See the [storage runbook](../runbooks/private-evidence-storage.md) for the caller obligations and operating limits.

The isolated test record contains 38 expected missing-implementation failures, then targeted failing regressions for broad operator principals (7), an arbitrary source-stream exception after partial data (1), and additional ACL/execute-only access (5 failed, 7 passed). Those concrete defects were corrected. Final storage-only verification recorded 54 passed, zero failed and one explicit non-Windows-host check skipped on Windows (55 total). Independent source review accepted the repaired protocol. Its result is local capability evidence, not production activation.

After bringing the isolated checkout to a8f53ac, the integrated storage/domain/Catalog-target/transaction-cleanup run passed 113, failed zero, skipped one, total 114, in 32 seconds. The skipped unsupported-host case requires a non-Windows runtime; it is not a silently successful test. Root checked all seven file digests before integration. Two extra blank lines at EOF were then removed to pass git diff --check; behavior did not change. Artifacts are storage-final55-green.trx and storage-integrated.trx under tmp/production/private-evidence-results/ in the isolated evidence worktree. Outcome counts come from individual test results because the TRX notExecuted summary counter does not reflect the explicit skip.

Real Windows tests cover protected private files, ACL denials, ancestor/leaf junctions, exclusive held handles, concurrent publication, partial or corrupt recovery, actual byte/deadline bounds and interrupted input. File and dangling symlink tests could not run with the host's privileges. Linux acceptance, process-kill/power-loss behavior and OS flush/close fault injection were not established. Managed path checks assume trusted service/admin processes preserve the namespace; file flush is not a Mongo/filesystem atomic commit or directory-metadata power-loss guarantee. Cancellation of arbitrary streams is cooperative.

Durable reservations, quota and writer claims, known-commit recovery, quarantine metadata/jobs, decoder/scanner verdicts, exact associations, authorized byte delivery and tombstone cleanup remain required. No payment has become approvable through these adapters. PAY01.3 through PAY01.6 and full production acceptance stay open.

Publication verification: [GitHub run 36389843924](https://github.com/natannielz/Capstone/actions/runs/36389843924) passed at a8f53ac: 1,408 backend tests passed, zero failed/skipped, in 21m45s; build zero warnings/errors, all 5 Auth0 Action cases and both contract checks passed. Frontend types, lint, 453+6+7 tests and production build passed with nine existing lint warnings. This accepts the Catalog-target and cleanup checkpoint. The later storage commit c5792c0 remains covered by its local integration until its own complete publication workflow finishes.

## Complete storage publication workflow

[GitHub run 36392159441](https://github.com/natannielz/Capstone/actions/runs/36392159441) completed successfully at f1671f355803bed5ef604f925f93b6e254a0ba8e. Backend build had zero warnings/errors. The Linux integration suite passed 1,410, failed zero and skipped 31 Windows-only test methods (1,441 total) in 24m22s. Windows theories are represented as skipped methods on Linux, so this count is not the number of Windows data rows. Default unavailable-storage and unsupported-host behavior ran on Linux; no Linux private-store activation is claimed. All five Auth0 Action tests and both API/client contract checks passed. Frontend typecheck, lint, 453+6+7 tests and production build passed; nine existing lint warnings remain.

The complete 232,321-byte workflow log is retained at tmp/production/github-publication-results/run-36392159441-full.log, SHA-256 0c32eeeb5089d722fc04691639a0445ce40bc7b0c2d83c796029ed4f092dfa21. This supersedes the earlier pending publication status for storage. It does not cover subsequent upload-admission or queue changes, and does not resolve the stated storage-host/operating limitations.
