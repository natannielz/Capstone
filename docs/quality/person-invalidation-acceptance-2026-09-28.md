# Person account invalidation prerequisite — 28 September 2026

Status: locally accepted prerequisite for ACC03.2–.3. The governed review workflow and production acceptance remain open. Baseline before this change: e586380.

## Behavior

PersonAccountInvalidation accepts a caller-owned active Mongo transaction and a complete sorted unique set of 1–256 positive account IDs. It validates all selected identities and matching sessions before the first write, including exact BSON types, positive/nonnegative revisions, valid status/provider fields and increment overflow. Missing legacy securityVersion is explicitly treated as 1 and advanced to 2.

Each selected identity advances grantVersion, securityVersion and fence while preserving provider issuer/subject and account status. Current and rotated sessions are revoked using the existing session store; previously revoked sessions remain unchanged. Returned records contain only account IDs and before/after authority revisions.

This helper is deliberately not independently idempotent: the owning governed command invokes it once on its new-effect path, under the same transaction as proposal/history/idempotency writes. Receipt replay must not repeat invalidation. Complete closure discovery, common-head fencing, current reviewer/source authority, audit history and final post-write validity checks belong to the owning workflow.

## Verification

Pinned local .NET 10 SDK and real disposable Mongo replica-set databases were used. Synthetic provider/account fixtures are not live identities or approved staff-source evidence.

- Initial behavior RED: one selected test failed at a temporary NotImplementedException.
- Initial focused GREEN: 10/10 passed.
- Final focused compatibility: **21/21 passed**, zero failed/skipped, **55 seconds**. This includes 12 new invalidation cases, five SessionStoreTests and four SessionAuthorizationFenceTests.
- Cases cover inactive aliases, provider/status preservation, legacy epochs, missing/malformed later accounts and sessions, numeric/array BSON coercion, overflow, sorted/bounded input, existing revoked sessions, downstream rollback, and both orderings of session creation/step-up racing invalidation.
- Two tests catch later preflight failure inside the caller transaction, commit an unrelated sentinel and prove no account/session partial writes.
- Independent source review found no actionable defects. The final test counters were verified from TRX.

Artifacts (ignored): tmp/production/person-governance-results/invalidation-red.trx, invalidation-green.trx and invalidation-compatibility.trx, with sibling logs. Final focused filter: FullyQualifiedName~PersonAccountInvalidationTests|FullyQualifiedName~SessionStoreTests|FullyQualifiedName~SessionAuthorizationFenceTests.

No existing application call path, API contract, frontend or migration changed. The previous 715-case full backend result remains baseline evidence; it was not rerun or represented as a current 727-case result. Focused verification is appropriate for this new, not-yet-connected primitive.

## Report and next-stage review

An independent source-to-plan audit confirmed all 97 unique report IDs, all 26 mapped task IDs and the original goal's link to the managed-worktree continuation. It identified two narrow wording omissions, now restored in the register and OPS03: unusual quantity anomalies (M07) and suspicious-access alerts (S09), with concrete acceptance cases. Required rule-based baselines remain mandatory; later models remain conditional.

[The next governed writer plan](../superpowers/plans/2026-09-28-person-governance.md) records structural withdrawal loading, unavailable production trust, exact reviewed proposals, immutable history, preserved legacy provenance, safe replay, invitation/bootstrap dependencies and specialized administration integration.

No approved personnel source, reviewer appointment, live authority, API/UI review flow, provider activation or production outcome was created. All 97 report rows remain open for production acceptance and the full goal remains active.
