# Governed staff identity changes — ACC03 continuation

This executable continuation belongs to [the report access plan](2026-09-28-report-access.md), [all 97 report requirements](../../requirements/unit-toko-report-2026-09-28.md), and the existing active production goal. It does not replace the remaining catalog, payment, business, recovery or launch work.

The accepted baseline is e586380: current person resolution, v8 storage and shared approval checks passed 715 backend tests. This plan covers the next governed writer boundary. Real staff source, named reviewers, bootstrap authorization and evidence-validity rules remain [D01 decisions](../../quality/report-decisions.md). No ordinary administrator, provider profile, matching email or test fixture supplies that authority.

Local service checkpoint: PG02–PG04 now have unavailable-by-default trust, v9 review storage, complete structural target loading and independently reviewed suspension/replay behavior. [Verification and remaining boundaries](../../quality/person-governance-acceptance-2026-09-28.md) distinguish this service work from PG05 API/UI delivery and live authority. PG06–PG07 and the complete parent goal remain open. Subsequent [PG05 local workflow evidence](../../quality/person-review-workflow-acceptance-2026-09-28.md) now covers selection, previews, private API/UI and receipt routing; PG05R local recovery is also accepted in [its verification record](../../quality/person-review-recovery-acceptance-2026-09-28.md); production trust and parent outcomes remain open.

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


### PG05 implementation map and prerequisites

The following implementation map has now been delivered as a local workflow checkpoint; [verification and limits](../../quality/person-review-workflow-acceptance-2026-09-28.md) record the actual files and results. Discovery and named selection precede the screen. Receipt lookup and exact in-memory retry are delivered; subsequent [PG05R acceptance](../../quality/person-review-recovery-acceptance-2026-09-28.md) now adds body-free resolution after reload.

1. Add Modules/Identity/PersonReviewContracts.cs and Platform/Identity/PersonReviewWorkflow.cs with a bounded proposal queue, named target search, consequence preview and safe detail/action projections. Add a private server-owned selection proof in PersonReviewSelectionTrust.cs. It must resolve approved visibility before any search/count/page or profile join, bind the exact actor/query/current policy, and retain transaction/lifetime fences through the final read. Ordinary directory permission is not staff-governance visibility. Do not invent system-wide reviewer appointments while D01 is unresolved.
2. Keep structural target inspection separate from positive person authority, so expired/disputed targets remain inspectable. Preview includes all affected aliases and their permitted names, account/session consequences and exact reviewed revisions. Actor, query, policy, closure or semantic changes invalidate the selection. Extend proposal creation to consume a server-held preview reference bound to those exact semantics and expiry inside the same transaction; the existing private groupId/version command alone cannot prove the user saw changed source, account or profile details. Bind that reference into the idempotency request. Raw source references, subjects and hashes stay private.
3. Add Api/Identity/PersonReviewEndpoints.cs and safe DTOs under /api/v1/identity/administration/person-reviews; map them in Program.cs and register the service beside enabled identity services in IdentityRuntime.cs. Preserve the unavailable public trust constructor. Follow IdentityAdministrationEndpoints.cs and AccessRequestMetadata for current actor, origin/CSRF, fresh MFA, reason, quoted If-Match and idempotency. Browser IDs and revisions use string contracts, never unwrapped domain long values.
4. Add the three person.review permission definitions to AccessEndpoints.cs AdministrativeActions projection only; do not add grants, role powers or adjudicator appointments. Add the page to Auth0Options.AllowedReturnPaths and frontend identity-workflow.ts return paths. The server already derives step-up operations from the permission catalog.
5. Add /administration/person-reviews/page.tsx, a focused screen and typed client under frontend/src/modules/identity; extend AdministrationNavigation.tsx, permission labels and identityDestinations(). Display original request, decision metadata, consequences and currently permitted actions. Clear prior data on actor/policy/selection changes. When trust is unavailable, explain in Indonesian that staff identity review has not been activated; successful sign-in/MFA does not activate it.
6. Update operation-journal.ts and OperationRecovery.tsx together. The original recovery sent every entry to access receipt/resolve; person.review operations now have their own receipt adapter. Dispatch by operation namespace; preserve the original actor, key and exact request. A missing receipt means unconfirmed, not failed, and cannot clear an uncertain entry. Define and test any explicit resolution contract before enabling a corresponding clear/retry action.
7. Generate OpenAPI/client contracts with the existing exporter and generator. Add real HTTP/Mongo cases for visibility before totals, hidden targets, safe joins, stale preview, policy/session expiry, CSRF/expected actor, large IDs and repeated commands. Browser tests cover actor switches, stale responses, unavailable versus denied states, exact retry bodies, recovery routing and missing-receipt retention. Inspect desktop/mobile/keyboard use and label mocked browser evidence separately.

Only the approved trust adapter can supply visibility and appointments. Default production discovery must remain unavailable; synthetic fixtures may exercise the local contracts without inventing D01 policy or granting real users authority.

## PG05R — Permanent no-effect resolution after reload (locally accepted)

Receipt lookup alone cannot prove that an absent receipt will never arrive. The accepted local backend now supplies an explicitly authorized permanent closure on the original request key. Unconfirmed, denied and uncertain responses remain locked. [Verification and limits](../../quality/person-review-recovery-acceptance-2026-09-28.md) preserve the local/synthetic boundary; live trust activation remains open.

- [x] Add a typed person-operation result: committed, closed_no_effect or not_confirmed. Use the existing unique actor/operation/key receipt identity for a strict versioned permanent closure marker; do not fabricate a proposal/history transition or use a new key. Preserve committed result validation and exact target-specific read authority.
- [x] For closure without a committed result, require current actor, governance-read and original operation permission/MFA/person authority plus an explicitly appointed private receiptResolve capability bound to that operation/key. Default trust remains unavailable. Receipt-only authority never grants names, target search or proposal detail.
- [x] Serialize tombstone insertion with original command receipt insertion in the same transaction protocol. Every command entry point must recognize a valid closure before body-hash/history handling and permanently refuse another effect. Recheck authority after the last await; malformed mixed receipts fail closed. Add migration/preflight only if shared receipt schema needs additive protection; never rewrite applied migrations.
- [x] Expose an explicit protected resolution route accepting the original operation/key and bounded reason, with no reconstructed private body. Add a person-specific optional close adapter. Clear the browser journal only after a validated committed or closed result; null, denial, timeout and ambiguous commit remain unresolved.
- [x] Prove both race orderings, a delayed old command, unknown command/closure commits, committed preservation after target suspension/preview expiry, cross-actor/key denial, final-await expiry rollback and reload/actor-switch browser behavior. Review the complete contract before enabling the UI action.

Ownership: Platform/Identity/PersonReviewOperationRecovery.cs and strict receipt helper; coordinated hooks in PersonWithdrawalService/Reads/Trust, PersonReviewContracts, private endpoints/DTOs and generated contracts. Frontend owns person-review client/workflow/screen and the optional journal/recovery adapter. This subsequent checkpoint is locally implemented and verified separately from PG05; it creates no real reviewer authority or production acceptance.

## PG06–PG07 — Preserve the remaining parent obligations

Registration, additional bindings, renewal and equivalence merge require trusted source observations and original private person snapshots. Source identity keys are stable; immutable evidence is never rewritten. A merge cannot make an original approver part of the approved target without explicit independent reconciliation. Last-binding moves/splits need a representable retirement/correction design; source changes above the transaction bound need fenced admission/maintenance and durable invalidation batches.

The first source and first adjudicators require an out-of-band reviewed bootstrap artifact and import procedure. Invitations need intended-person evidence bound to invitation/version/reserved account ID before that identity exists; never create a fake active identity to satisfy the existing resolver. Admission must atomically materialize and recheck reviewed bindings without inventing an approver session.

Specialized access/membership/historical approval paths need their own current authorization and original participant history. Preserve historical-only policy when the target branch is suspended. Finance conflict preview/activation must evaluate the full same-person authority set across overlapping scopes and periods against an approved versioned matrix. Template upgrades remain explicit reviewed migrations of grants.

The report's payment evidence, packaging, stock, accounting, recovery, operational readiness and branch pilot acceptance remain required. This continuation supplies their access prerequisite; it does not reduce the production goal.
