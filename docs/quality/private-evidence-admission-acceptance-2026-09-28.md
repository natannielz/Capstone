# Catalog image upload admission — 28 September 2026

Status: independently reviewed and integrated locally; exact-source combined verification passed. This accepts a bounded internal reservation-to-quarantine implementation, not payment approval, eligible catalog images, an upload endpoint or production activation.

## Behavior delivered

EvidenceService binds each reservation to the real Catalog family/SKU owner and original user/issuer/subject. Current permission, identity, session, target and policy remain checked through the final transaction work. A same-key retry retains the same four independent IDs and one original attempt. Capacity retains three maximum-size copies plus one reservation through failed, interrupted and uncertain outcomes; nothing releases capacity implicitly.

The raw upload has one deadline fixed before its authorized pre-read and retained through Mongo retries. Known committed claims precede filesystem staging. A complete-EOF sealed result must be durably frozen before publication. The exact original remains held and verified while a fresh authorized transaction records quarantine, audit, deterministic completion receipt and one validation-request queue message. Interrupted recovery has no replacement input, new generation, overwrite or inferred EOF success. Storage I/O stays outside transaction retries.

Migration v13 preflights all eight targets, strict BSON shapes, indexes/options, identity relationships, reserve/completion receipts, lifecycle audit history and retained capacity before target changes. Its replay requires a publication claim before quarantine, checks claim epochs and final claim kind, and accepts legitimate reclaimed histories. Applied production migrations v1–v12 remain unchanged. Four historical test fixtures were adjusted only to remove v12-and-later history when deliberately rerunning v12 or to include the exact eight new DDL targets.

## Verification and review

The isolated final affected run passed **241 tests, zero failed, one explicit Windows-side unsupported-host skip**, 242 total, in 5m16s. All 110 admission cases passed. It covers Documents, Catalog upload boundaries, accounting migration compatibility and interrupted v5/v6/v7 chain fixtures. This is filtered local evidence; it is not a whole-suite or live service result.

Retained failing regressions proved missing original reserve provenance, an advancing-clock completion timestamp mismatch, a deadline extended by slow pre-read/retry, fractional/beyond-reservation publication deadlines, missing publication-claim history, wrong claim epoch and erased final publication claim fields. Each is covered by the final passing run. Earlier fixture mistakes and intermediate failures remain in the handoff rather than being represented as successful acceptance.

The independent reviewer accepted the corrected implementation with no remaining scoped findings. Root and reviewer independently matched all 25 source/test/contract files to the handoff manifest, and root verified the same hashes after selective integration. The primary solution compiled with zero warnings/errors; the first integration build exposed a stale call in the separate, uncommitted Inventory refactor, removed by its owner before the successful build. No admission source changed for that correction.

| Evidence | SHA-256 |
| --- | --- |
| Isolated source-manifest.json | ba25b9e588c2fa26b55a119fea40963fbe90da32ba53952cf9f10358734a663b |
| Isolated final affected TRX | 9987575b4b162f53386faf08acabef80724b7a9406c04f2b1f103118345fab08 |
| EvidenceMigrationGraph.cs | 46058bfa599d1526f421fc8006779b06d29b99f5141062d08b121c01f5ab6181 |
| EvidenceAdmissionGraphTests.cs | de47f0d64c7b5e984763ae3662daae10be4f93de7acd8d7857fd2521d50f4012 |

The isolated checkout is unit-toko-private-evidence/Capstone, with handoff/manifest under .superpowers/sdd/2026-09-28-private-evidence-admission/ and test artifacts under tmp/production/private-evidence-admission-results/. Primary integration logs are under the same tmp-relative directory in unit-toko-production/Capstone. The combined primary run additionally exercises the accepted bounded Outbox and stock-count Catalog adapters against the new v13 validators; its completed result is recorded below.

## Remaining gates

The storage adapter remains internal and unavailable by default; observed host registration and explicit policy are required. No credentials, policy, live grants, HTTP route, worker consumer, scanner, decoder, byte delivery, association/pin, replacement, deletion or quota release was activated. Quarantine retains unavailable validation results. Windows tests do not prove Linux positive storage support, hostile-operator resistance, hard process deadlines or power-loss durability.

Full PAY01, local OCR, mandatory payment image review, independent human approval, bank reconciliation, all other report requirements and the actual branch pilot remain open. See the [admission contract](../requirements/private-evidence-admission.md), [operating guidance](../runbooks/private-evidence-admission.md), and [complete payment plan](../superpowers/plans/2026-09-28-report-payments-ocr.md).

## Primary integration findings and exact-source rerun

The first primary combined run, retained as integrated-admission-outbox-red.trx, ended with **171 passed, 125 failed, one explicit platform skip**, 297 total, 2m55s. Of the failures, 107 refused storage with storage_acl_unsafe because the primary checkout's ancestors granted untrusted identities Modify rights; 18 generic Outbox fixtures used evidence.validation.requested with non-evidence payloads and were correctly rejected by v13. Neither failure class was hidden or treated as success.

Read-only ACL comparison confirmed that the previously accepted isolated parent lacked those untrusted ancestor mutation rights. No ACL or production storage check was weakened. A fresh temporary tree at unit-toko-private-evidence/Capstone/tmp/av1 contains a SHA-256-verified copy of all 344 intended backend source files, excluding the separate uncommitted Inventory implementation. It compiled with zero warnings/errors. Its source manifest binds the tested files to the primary working source.

The generic queue tests now use unit-toko.test.validation.requested; the one-line fixture correction preserves all queue assertions and was independently accepted. A new reviewed real producer-to-queue test creates its event through actual reservation/upload/quarantine, exercises claim, renewal, retry, reclaim and completion, denies a stale acknowledgement and confirms unchanged exact evidence, retained capacity and unavailable validation results. It does not activate a validator. The test's source SHA-256 is bff7ed32f00120b34bb5f3b0904b6324cf63b9c44d90823d5d34a3f36580d484; corrected generic queue fixture SHA-256 is 1b4c41fdd5b8444d03f68a8927133ba2e70798c46bd11f6abc0e83c711128aba. The exact-source rerun has now passed as recorded below.

## Final integrated verification

The protected exact-source run passed **297 tests, zero failed, one explicit unsupported-host skip**, 298 total, in 6m14s. The real producer-to-queue test passed. All 344 snapshot source hashes matched the intended primary backend source after integration; manifest SHA-256: c772860c16e9b3c3deaf7ae2b90f43e19c31ccbfb8abe49545141051919f22c1. Snapshot and primary comparisons excluded the separately uncommitted Inventory files and all unrelated user work.

The final TRX is retained in the primary tmp/production/private-evidence-admission-results/integrated-admission-outbox-final.trx (467554 bytes; SHA-256 dac2864d192475fcd24d681ca78776c30b68bacccb42fae8a32e935902889af2), alongside the source manifest and protected snapshot build/test logs. Root observed the terminal exit code zero. This supersedes the first failed primary integration run without hiding its failure causes. A fresh complete GitHub workflow must verify the new publication commit; d29bd09's earlier successful workflow does not cover this new admission slice.
