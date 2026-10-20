# R11: Access review campaigns and branch moves — design spike

**Status:** design for owner review. Nothing here is implemented. Phase D3 Task 6 asked for a design spike before a plan and TDD. Five policy decisions at the end belong to the owner.

**Register row:** R11 "Join/move/leave dan access review": membership lifecycle and campaign attestations; dormant or revoked accounts must not persist; an accountable reviewer for each grant.

## What exists today

- Grants (`authorizationGrants`, `PermissionGrant`) hang off a membership, carry a scope, version, effective/expiry dates and status. They are proposed and approved maker/checker (`AccessGrantCommands`) and revoked by `RevokeGrantAsync`, which takes `access.revoke` on the grant's scope, version-checks, bumps the identity's grant version, and writes `access.grant.revoked` history.
- Memberships (`AccessMembershipCommands`) are approved and can be suspended. Identities and scopes can be suspended (`AccessSuspensionCommands`).
- Every request recompiles authority from the live snapshot, so a revoked grant stops working on the next request. Sessions are revoked on identity suspension.
- What is missing: no periodic re-attestation, no accountable reviewer per grant, no notion of an unattested (lapsed) grant, and no first-class "move to another branch". Today a move means ending one membership and approving another by hand, with no link between the two.

## Design

### 1. Access review campaign

A campaign covers one organisation scope and one period. Opening it freezes the population: one review item for every grant that is active at that moment within the scope.

- `accessReviewCampaigns`: `{ _id, scope, period, opensAt, dueAt, status: open|closed, openedBy, closedBy?, reason, itemCount, schemaVersion }`.
- `accessReviewItems`: `{ _id, campaignId, grantId, grantVersion, membershipId, subjectId, scope, permissionsHash, reviewerId, decision: pending|keep|revoke|changed|lapsed, decidedBy?, decidedAt?, reason?, personEvidence?, revocationId? }`.
- Opening and closing use the same command conventions as every other access change: idempotency key, reason, expected version, audit/outbox effects and original-receipt recovery.

### 2. Accountable reviewer and attestation

Each item names its reviewer when the campaign opens. The reviewer must be a different person from the subject (person evidence, as in maker/checker), and must hold `access.review.attest` on the item's scope.

- **Keep:** the reviewer attests that the access is still needed, with a reason.
- **Revoke:** the reviewer asks for removal. The item records the decision, and the revocation runs through the existing `RevokeGrantAsync` semantics in the same transaction, with an idempotency key derived from campaign and item. The grant history therefore shows the ordinary `access.grant.revoked` event, linked to the review item.
- **Changed:** if the grant's version changes after the campaign opens (edited, re-approved or revoked elsewhere), the item is marked `changed`. An attestation of the old version is refused, so nobody can confirm access they never saw.

### 3. Lapses

Closing a campaign after `dueAt` turns every `pending` item into `lapsed`, and applies the owner's lapse rule (decision 3) through the existing revoke command, one item at a time and recoverably. Closing needs `access.review.manage` and a person other than the one who opened the campaign.

### 4. Dormant and revoked accounts

Each item shows the subject's last provider-verified sign-in age, so reviewers can spot dormant accounts. A dormant threshold (decision 4) flags those items. Suspended or revoked identities create no items, because they have no active grants by construction. A test pins that.

### 5. Branch move (join / move / leave)

- **Join:** unchanged; the existing membership proposal and approval.
- **Move:** a maker/checker `membership transfer` command, recorded in `membershipTransfers: { _id, userId, fromMembershipId, toScope, effectiveAt, requestedBy, approvedBy, reason, endedGrantIds[], newMembershipId }`. On approval, in one transaction: end the old membership, revoke every grant bound to it (existing revoke semantics), create the new membership at the target scope, and link both in the transfer record. Grants do not follow the person automatically; the new branch grants access through the normal flow (decision 5).
- **Leave:** end the membership and revoke its grants. If the identity has no remaining active membership, it is suspended, which also revokes its sessions.

### 6. Surfaces

- `/administration/access-reviews`: the reviewer's own queue (pending items with subject, scope, permissions, last sign-in and the keep/revoke decision) and the campaign overview for managers (progress, changed, lapsed, close).
- The work queue (C2 `IWorkSource`) gains "Tinjauan akses menunggu" for the signed-in reviewer, counted and aged like the other sources.
- Transfer appears on the existing access administration page beside membership approval.

### 7. Permissions and migration

- Permissions: `access.review.read`, `access.review.attest`, `access.review.manage`, and `access.membership.transfer`. They are granted only through the approved D02 matrix; Super Admin gets none (`ApprovedRuleConformanceTests`).
- Storage: one migration adds the three collections with strict validators and unique indexes: one item per (campaign, grant), and one open campaign per (scope, period).

## Failure modes to test first

1. A reviewer attests their own access, or access belonging to another account of the same person → refused.
2. A grant edited after opening → item `changed`; the old attestation is refused.
3. A revoke decision whose transaction fails → neither the item nor the grant changes; the original key recovers the outcome.
4. Closing twice, or concurrently with a late attestation → each item ends in exactly one terminal state.
5. A move whose approval races with a grant change on the old membership → one wins; no grant survives on an ended membership.

## Decisions for the owner

1. **Cadence:** quarterly for all grants, or more often for finance and approval permissions?
2. **Accountable reviewer:** the head of the subject's branch or unit, the organisation's access administrator, or a named reviewer per scope?
3. **Lapse action:** revoke the grant (strict), or suspend it pending review (reversible)?
4. **Dormant threshold:** for example 90 days without a verified sign-in. Flag only, or include it in the lapse rule?
5. **Move semantics:** grants never follow the person (least privilege, the default above), or the old grants are offered as a pre-filled proposal at the new branch for independent approval?
