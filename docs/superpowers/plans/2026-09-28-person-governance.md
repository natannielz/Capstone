# Governed staff identity changes — ACC03 continuation

This executable continuation belongs to [the report access plan](2026-09-28-report-access.md), [all 97 report requirements](../../requirements/unit-toko-report-2026-09-28.md), and the existing active production goal. It does not replace the remaining catalog, payment, business, recovery or launch work.

The accepted baseline is e586380: current person resolution, v8 storage and shared approval checks passed 715 backend tests. This plan covers the next governed writer boundary. Real staff source, named reviewers, bootstrap authorization and evidence-validity rules remain [D01 decisions](../../quality/report-decisions.md). No ordinary administrator, provider profile, matching email or test fixture supplies that authority.

## Deliverables and order

| Step | Concrete output | Acceptance |
| --- | --- | --- |
| PG01 | All-account/session invalidation primitive | Exact identity revisions and provider/status preservation; atomic rollback; session creation/rotation races |
| PG02 | Server trust contracts and v9 persistence design | Unavailable production default; policy/source/proposal pins; immutable history; compatibility review before migration |
| PG03 | Complete structural target loader | Exact complete closure even when target evidence expired or disputed; no authority granted by loading |
| PG04 | Governed withdrawal of one complete equivalence group | Propose, independent review, atomic suspension/invalidation/history and safe result recovery |
| PG05 | Private read/API/review UI integration | Current appointed access, safe projections, CSRF/MFA, understandable consequences and failure states |
| PG06 | Registration/binding/renewal/merge and invitation extension | Exact reviewed source evidence, original snapshots, admission proof, complete mutation fences and exceptions |
| PG07 | Specialized administration, finance matrix and template integration | Preserve historical grant policy; current-person independence; reviewed finance matrix/version migration |

PG04 is a first complete writer slice, not completion of ACC03. Unsupported split, last-binding reassignment, large source revisions and bootstrap remain explicit follow-on work. They cannot disappear from the parent scope.

## PG01 — Invalidate all affected accounts

Owner files: Platform/Identity/PersonAccountInvalidation.cs and Tests/Authorization/PersonAccountInvalidationTests.cs.

The caller supplies the complete sorted unique set of affected account IDs, including inactive aliases, within one existing Mongo transaction. The helper validates all identities and matching sessions before mutation; advances grantVersion, securityVersion and fence; preserves issuer, subject and status; revokes outstanding sessions through the existing session store; and returns only safe before/after revisions. Missing legacy securityVersion means revision 1, advanced to 2.

The caller remains responsible for authorization, complete graph discovery and common-head fences, idempotency, immutable history, final validity checks and transaction rollback on any later failure. This primitive neither establishes person equivalence nor exposes a public governance workflow.

Local prerequisite accepted: 21/21 focused tests passed (12 new cases and nine existing session cases), with an independent review finding no actionable defects. See [exact evidence and boundaries](../../quality/person-invalidation-acceptance-2026-09-28.md). No governance endpoint or new authority source is delivered by this helper.

Required cases: missing/malformed later identity or session, exact BSON types, overflow, sorted/bounded selection, preserved inactive states, already-revoked session preservation, caught preflight failure without partial writes, downstream rollback, and both orderings of concurrent session issue/step-up. Run focused tests and existing session compatibility tests; record exact artifacts.

## PG02 — Freeze trust and storage before writing authority

Keep public commands typed and small: propose a particular operation against IDs/revisions, then approve/reject/withdraw that stored proposal by ID/version and command metadata. Public commands cannot carry verified=true, reviewer appointment, arbitrary BSON patches or client-produced evidence digests.

Private server proofs must bind the exact transaction attempt, action, proposal/version, request digest, affected closure, source and policy revisions, and lifetime. Authority to adjudicate and evidence supporting a change are separate proofs. The production trust implementation is unavailable until approved configuration exists; synthetic implementations remain in tests.

Review a forward v9 design with three private collections:

- personGovernancePolicies: durable versioned policy head and fence, status, validity and reviewed manifest reference/digest. The manifest governs adjudicators, actions, scope and evidence rules; its real contents require D01.
- personReviewProposals: immutable typed request and request digest, expected heads or explicit absence, original participants/snapshots, exact policy/source/observation pins and bounded status transition.
- personReviewHistory: append-only transition evidence and safe before/after references, unique proposal/transition revision. Original private source evidence stays restricted.

Freeze each exact discriminator, BSON type, length, chronology, index, counter and cross-reference before implementing migration. Preflight all new collections before DDL; preserve stronger validators and v1–v8 history; seed no authority. Do not treat existing opaque v8 approvalId values as proposal IDs in the new namespace. Newly reviewed actions need explicit provenance kind/namespace in their own history. Any authority-establishing writer must refuse unreconciled provenance until an approved reconciliation artifact exists.

An external HTTP response is not atomic with Mongo. Authority-establishing operations need an approved immutable snapshot controlled by a Mongo-fenced head, or a source-issued finite lease with defined revocation semantics. Bind exact observations to the proposal and revalidate admissibility inside every retry. A changed observation needs renewed review. No freshness duration is invented here.

## PG03 — Load the complete target without authorizing it

Separate structural loading from PersonRegistryService's positive-authority resolver. A group that expired, was disputed or lost source validity must still be structurally inspectable for withdrawal.

Discover the complete current old/new group, person and account closure, including inactive aliases and overlapping relationships. Validate exact types, pointers and membership; never trust only a submitted member list or truncate beyond the 256-member transaction bound. Missing, malformed or ambiguous closure requires a repair/maintenance path, not an incomplete mutation.

The target loader grants no verification or reviewer power. Requester/approver authority is resolved independently through current server-owned person and policy proofs. Detect any affected account/person overlap with those participants under the eventual approved independence policy. The reviewer must be a different person from the proposer and affected beneficiaries. Whether an affected person may propose their own withdrawal belongs to that policy; do not invent eligibility or require their deliberately withdrawn authority to remain positive after transition.

## PG04 — First writer: reviewed group withdrawal

1. Authenticate the requester and resolve the appointed policy for this exact operation. A new suspension targets a current active or disputed group only. An already suspended group permits authorized replay/history retrieval; a superseded group requires a new proposal against its current replacement. Load the structural target and capture its complete semantic revision/closure. Persist a pending proposal, original participant proof references and proposed-transition history atomically.
2. Approval accepts only proposal ID/version and command metadata. Load the stored request; resolve current reviewer and original requester authority; enforce independent people and the approved target conflict rules. Check the original requester identity/appointment/person and recorded authority revisions without fabricating an ActorContext or a new MFA event for that absent requester; the acting reviewer needs current fresh MFA. No generic access.approve or system-admin shortcut.
3. Re-load the target and compare every pinned semantic head and complete closure. Fence the policy, actor/session/person proofs and all relevant common heads so concurrent account insertion, merge or source withdrawal forces reauthorization.
4. Change the target group's status/version/fence. Preserve its original v8 equivalence provenance fields; the new withdrawal decision belongs in separate immutable review history.
5. In the same transaction, invoke PG01 for every affected account; write the terminal proposal, private history and safe idempotent result. A failure anywhere rolls everything back.
6. After business writes AND the idempotency result update, recheck current policy, reviewer/person validity, permission/MFA/session lifetime and transaction ownership. Add an explicit owner finalization hook or a dedicated guarded executor: a check only inside businessWrite misses later awaited receipt writes.
7. Same-body command replay must enforce the command's current authority and exact original outcome contract. A separate safe result-recovery read requires current governance-read access. Operation receipts and recovery stay bound to the original actor, operation and key; broader history requires separate current policy authorization. Neither path should demand that the deliberately suspended target still have positive verified-person authority; neither repeats suspension, invalidation or history.

Required denial/race cases: same account/person request and approval; missing/expired/withdrawn policy; absent or stale proposal; changed closure; late account insertion; changed reviewer equivalence; permission/session expiry during the final receipt write; malformed target; source change; caught validation failure; unknown commit; changed idempotency body; concurrent approvals; and both orderings of a sensitive business transaction racing withdrawal.

Use expired/disputed but structurally complete target fixtures to prove safe withdrawal remains possible. A future approved restoration is a new independently reviewed operation, never an automatic status toggle.

## PG05 — Private workflow delivery and acceptance

Add review list/detail, explicit propose/approve/reject/withdraw operations and safe outcome recovery only after the service boundary is accepted. Current policy controls counts, pages, details and audit reads. Creating a proposal never grants private-read access. Return the affected account names/counts and consequences that the reviewer is allowed to see, never raw HR subjects, source references, hashes or arbitrary database documents.

Review UI shows original request, exact reviewed revision, independent decision, reason and account/session consequences. Preserve Indonesian wording, keyboard focus, mobile layout and distinct pending, denied, stale and uncertain outcomes. All writes retain existing origin/CSRF/expected-actor/MFA protections.

Run real Mongo and non-intercepted API tests, generated-contract checks, browser/accessibility acceptance and independent review. Record synthetic source/policy fixtures separately from live acceptance. Do not enable real source/reviewer authority or claim ACC03 complete from these tests.

## PG06–PG07 — Preserve the remaining parent obligations

Registration, additional bindings, renewal and equivalence merge require trusted source observations and original private person snapshots. Source identity keys are stable; immutable evidence is never rewritten. A merge cannot make an original approver part of the approved target without explicit independent reconciliation. Last-binding moves/splits need a representable retirement/correction design; source changes above the transaction bound need fenced admission/maintenance and durable invalidation batches.

The first source and first adjudicators require an out-of-band reviewed bootstrap artifact and import procedure. Invitations need intended-person evidence bound to invitation/version/reserved account ID before that identity exists; never create a fake active identity to satisfy the existing resolver. Admission must atomically materialize and recheck reviewed bindings without inventing an approver session.

Specialized access/membership/historical approval paths need their own current authorization and original participant history. Preserve historical-only policy when the target branch is suspended. Finance conflict preview/activation must evaluate the full same-person authority set across overlapping scopes and periods against an approved versioned matrix. Template upgrades remain explicit reviewed migrations of grants.

The report's payment evidence, packaging, stock, accounting, recovery, operational readiness and branch pilot acceptance remain required. This continuation supplies their access prerequisite; it does not reduce the production goal.
