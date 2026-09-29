# Stock operating-policy migration verification — 29 September 2026

Status: locally verified migration 19 checkpoint. All 926 final backend/browser checks passed with zero failures/skips, and both API contract checks passed unchanged. The full original production plan and all 97 report requirements remain incomplete.

## Delivered boundary

Migration 19 installs strict operating-policy proposal, decision and accepted-version collections, their two unique business keys and six lookup indexes. It first validates prerequisite source/settings/mapping/suspension storage and complete historical provenance, then every policy record, audit and whole-command receipt in both directions. Exact participants, original identities, configuration bindings, chronology, accepted version continuity and allocator bounds are required. Historical current-person binding loss or suspended access does not erase legitimate past records.

All target schema/index/counter writes follow complete preflight and use majority acknowledgement under the renewable migration lease. Compatible stronger validators and equivalent safe indexes survive. Unsafe storage, duplicate versions, missing or copied provenance, invalid counters and inconsistent history fail without target writes. Interrupted collection/index creation resumes with the original migration start record, while a competing live lease blocks changes.

Applied migrations 1–18 are unchanged. Forward-specific source and suspension graph copies partition new policy-family records into the new policy validator. The copies otherwise preserve the predecessor validation rules. Every excluded policy candidate is checked, including foreign-operation copies and older records disguised as policy records; this is not a namespace bypass.

## Reproduced defects and evidence

Artifacts remain in tmp/production/stock-source-operating-policy-migration-results.

- Five initial upgrade cases failed while the registry was still at 18, then passed after registration.
- Initial integrity and existing service validation passed 106 cases.
- Two unrelated policy-named-record cases reproduced overbroad migration classification. Field-only policy markers now require the corresponding poolId; explicit reserved operations and kinds remain checked.
- Two coherent accepted-history cases reproduced a runtime gap at equal timestamps: a later policy could lose a previously accepted channel or duplicate an unchanged definition. Runtime and migration now share AcceptedContinuity. Accepted version order proves continuity even when timestamps cannot prove commit order.
- Seven provenance cases passed after correction, including forged later approval times, legitimate settings/mapping/stop evolution and corruption beyond the first 64-row cursor batch.
- The complete migration suite passed all 63 cases, zero failures/skips, in 2 minutes 26 seconds. It covers web/external and approve/reject/withdraw histories; whole-body/result/participant/audit corruption; all referenced records; storage/index/allocator hazards; view/capped/time-series/collated collections; stronger validators; old source/mapping/stop provenance; both family directions; live leases; interrupted DDL; idempotent reruns; large exact quantities; and nonempty lots, stock and accounting preservation.

Migration TRX SHA-256: e8e5c345a8055facb4c4d938fa7d0ab0f7ca2f6c7a2bcc1a4fc012a0770fc684

Existing service fixtures now exercise registered storage rather than creating duplicate draft collections. Three older interrupted-chain assertions explicitly include all three new collections in both allowed DDL and positive installation checks; majority-write/history checks remain. Two suspension upgrade counts now use the current chain version while preserving their actual legacy-17 baseline.

Author review covers every owned application change, the isolated forward graph diffs, original scope and final verification artifacts. Independent review remains pending unavailable agent capacity.

Final affected regression passed 858 cases with zero failures/skips in 30 minutes 21 seconds. It covers all affected migration/source classes and independent-person/approval boundaries. All five existing real HTTPS/browser/Auth0-fixture/Mongo workflows then executed and passed with zero skips on schema 19 in about 3 minutes 21 seconds. Combined final verification is 63 + 858 + 5 = 926 passing checks. Both compiled server and generated client API contracts passed unchanged.

Affected regression TRX SHA-256: bc55be2ee5424e6edba6ead6b93f8361a40ba717b6e534c3c4e9e93acf290236

Browser TRX SHA-256: f2c4a3cbf1dc5a02a6a71f8943042f73b784a38222f1a4aae98dca9bf157f7aa

Published commit 922e3e40109718cdd7b4220cc049d75827044b48 received successful frontend verification (670 executions, type/lint/build) and all five browser checks in [GitHub run 36527018408](https://github.com/natannielz/Capstone/actions/runs/36527018408). Its backend job was cancelled after exceeding the configured one-hour maximum, as confirmed by GitHub's check annotation. The whole backend suite did not produce a final result, and subsequent Auth0 Action/server/client contract steps were skipped in that run. The prior full CI success remains attached to commit 3064541, not 922 or this new checkpoint.

The workflow now permits 90 minutes for the backend job and prints named test progress. The full unfiltered test set, other jobs, pinned dependencies and verification gates remain unchanged. No successful new full CI outcome is inferred from local checks. Exact source, result and artifact hashes are recorded in the local evidence manifests.

## Remaining gates

Frontend application source is unchanged by this checkpoint; its earlier published build evidence is identified above. GitHub publication must verify the exact reviewed commit and track its new full CI result; Vercel deployment remains separately pending account access.

Next add action-specific proposer/checker previews, separately scoped bounded history, strict API/client contracts, four-operation receipt journal, current-access/MFA/return-path discovery and accessible staff/browser integration. The [upgrade runbook](../runbooks/stock-source-operating-policy-migration.md) requires writer quiescence, coordinated backup and independent production restore acceptance.

A policy definition or registered schema does not establish accepted control/handoff, producer-complete drain, reactivation, trusted observations/reconciliation/external holds, canonical stock/cost transactions, remaining business workflows or an accepted pilot. CAT02/OPS02 and all original production/report obligations remain incomplete. Vercel owning-account access remains unavailable; no deployment, production backend activation, real-data cutover or financial transfer is claimed.
