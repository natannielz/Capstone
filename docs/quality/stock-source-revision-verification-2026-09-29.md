# Stock-source revision verification — 29 September 2026

Base commit: fb1b6c8af012e79f014f0d6c547354768ea34ffa. This checkpoint implements the transactional settings-revision service. It does not activate a source or complete CAT02.

## Behavior

Existing physical pools can receive an immutable proposed settings change. The proposal stores the actual current settings and expected pool version with the new explicit settings. Physical pool ID, canonical owner location and organization-wide physical key remain unchanged; moving physical inventory or relabeling its identity is a separate governed operation.

A current, independently verified person with stock.source.approve and fresh MFA approves or rejects the proposal. Only its maker can withdraw it. Approval requires the pool to be inactive and its version/settings to match the proposal's base. The command advances the pool version and appends the approved revision, decision, audit and idempotency receipt in one Mongo transaction. It never changes quantities, lots, reservations, movements or journals. No setup role defaults or finance grants were widened.

The original creation proposal/approval/registration remains immutable. Revision one derives from that audited creation; later settings derive from the latest approved revision. Strict BSON records, compound pool/version uniqueness, maximum version and indexed count detect missing sequence rows. Current and immediately preceding revisions require their actual proposal/decision/audit, matching base settings and chronological ordering. The original creation is always checked. This runtime check is not a full re-audit of every historical record; the forward migration must validate the complete historical chain.

Commands use the existing session, person, grant, membership and hierarchy write fences. Competing approvals cannot both apply one base version. A backward clock cannot create a proposal earlier than its base approval. Replaying the same operation key returns its original result, including after later source revisions, but still requires current authorization. A changed request cannot reuse the key. No receipt-only recovery route is introduced here.

## Verification

The new 48-case suite passed with zero failures or skips against the guarded local Mongo replica set. Eight are pure settings-rule cases; 40 exercise actual Mongo storage and workflows. The real concurrency test holds both transactions after their proposal scope reads and before shared authorization write fences; it observes two arrivals and exactly one successful approval. Separate cases revoke a grant or expire MFA after the actual approval audit insert and prove rollback of pool state, revision, decision, audit and receipt.

Other cases cover three successive source versions, stable historical replay, rejected/withdrawn terminal decisions, same-account and same-person review denial, missing person authority, foreign channels, other-proposer withdrawal, stale proposal/version, active-source handoff denial, broken current/original/predecessor provenance, missing sequence entries, direct database rejection of seven invalid proposal shapes, weakened storage and actual audit-write failure.

Observed development sequence:

- Missing revision operation: one expected failing behavior test.
- First candidate: 25 passes and one fixture failure because the foreign test channel had not been seeded. The fixture was corrected without changing authorization behavior.
- Timestamp/foreign/concurrency slice: two passes and one real failure because a backdated proposal was accepted.
- Predecessor-history slice: three real failures because missing/altered predecessor history was accepted.
- Corrected full revision suite: 48 passes, no failures/skips, approximately 3m08s.

All 104 affected existing setup, permission, graph and migration cases passed with zero failures/skips in approximately 5m43s. Together with the new 48 cases, this is 152 distinct passing tests. The two runs used the same corrected backend assembly; subsequent source edits only normalized final whitespace. All fixtures use synthetic identities, policy text and records; they do not prove acceptance of actual BNI/Koperasi source data or policies.

Artifacts are retained in tmp/production/stock-source-revision-results. The final source manifest and TRX hashes identify the tested files. Independent review remains pending the recorded reviewer capacity limit.

## Delivery boundary and remaining work

The three new collection contracts are draft-only. Registered migrations still end at version 15; the change only exposes seven existing schema builders internally, preserving their serialized validators. There is no runtime auto-DDL, new HTTP route, generated-client change, revision screen, receipt-only recovery path or live-source activation.

Next, implement a forward migration with complete original/revision/audit/receipt and retained-participant graph preflight, collection/index/counter safety and resumable majority DDL. It must preserve already-applied migrations, validate every earlier revision and every historical reference, and reject unexplained mutable-pool heads. Then add bounded pool/proposal reads, strict API/client contracts, recovery that cannot resubmit, and the staff review/history surface with real browser coverage.

Active-source suspension/draining/handoff and accepted reactivation, cross-channel aliases resolving one physical balance, actual source observations, governed lot authoring, operational balance/movement migration, receiving/conversion/reservation/cost workflows, load/restore and accepted pilot remain part of the full goal. The D01/D02/D04 and other actual business/operations gates have not been invented. GitHub publication is authorized; Vercel owning-account access is still pending.

## Final artifact hashes

- source-revision-full.trx: 422b7af40ac5a26d3d43cdb7882eb6ebaa4ca39d3bb49afe7148eeb38c154e70
- source-revision-affected.trx: fd8bb6ae414cb1385cf4b843b6d22bcbec38b424d6aeabd386342883a0f4cabf
