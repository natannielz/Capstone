# Unit Toko access model

Status: local T03/T04 runtime and administration foundations are implemented; ACC02 adds explicit business permissions and optional role suggestions. Production identity/provider/business acceptance remains open. The API requires verified actors; existing demo roles do not authenticate to it.

## Six role templates

Roles are reviewed starting points for explicit grants. A grant records its template version, materialized permissions, exact scope, beneficiary, requester, approver, reason and validity period. Editing a template never edits an existing permission snapshot. Future template versions require an explicit reviewed migration; the first implementation accepts version 1.

| Template | Responsibility | Boundary |
| --- | --- | --- |
| Buyer | Catalog, orders, receipts, invoices and cases for assigned purchasing work | Own/assigned records in the stated organization/branch/division; wider visibility requires an explicit marker |
| Store Staff | Receiving, stock counts, packing, dispatch and procurement preparation | Stated fulfillment organization, shop or warehouse, and channel |
| Store Head | Store administration, procurement/stock approvals and courier assignment | Stated fulfillment location/channel; independent approval still applies |
| Courier | Assigned shipments, delivery attempts/evidence and expense requests | Assignment is mandatory even within an approved location |
| Finance | Permissions from explicitly selected finance packs | Empty by default; each pack is separately granted for a location/channel |
| System Admin | Invitations, access proposals, technical settings, recovery initiation and access audit | Administrative metadata; no transaction approval or financial contents merely from this role |

Finance packs:

| Pack | Responsibilities |
| --- | --- |
| Reporting | Source reconciliation, report preparation, period submission, reopening request |
| Billing | Invoice issue, payment recording/allocation, credit/refund requests, disbursement recording |
| Approval | Credit/refund, funding/expense and period approval, reopening approval |
| Accounting | Reconciliation verification, journal posting/reversal, period close, disbursement verification |

`payment.verify` is an additional, explicit permission; it is absent from every default finance pack. Technical administration does not imply business delegation authority. Legacy `laporan`, `penagihan`, `pimpinan` and `akuntansi` map only to their corresponding pack, never to the union. A legacy mapping is a proposal, not an approved account. Public demo customers receive no automatic production membership.

## Scope and membership

Buying organizations and fulfillment organizations are different kinds of node. Branches belong to buying organizations; divisions belong to branches. Shops belong to fulfillment organizations; warehouses belong to shops; source channels belong to fulfillment organizations. Parent relationships and active status are checked, not inferred from a supplied ID.

The scope kind states breadth explicitly. Missing IDs never mean global access. An ordinary Buyer is limited to own or assigned records. `branch.pic` permits the exact approved branch/division; `organization.wide` is required for organization-wide purchasing visibility. OMI and S-Mart channels remain separate scope dimensions. A complete grant matches all its dimensions; multiple grants are OR alternatives. Combining one grant's branch with another grant's channel is forbidden.

Membership and grant approval are distinct. Provider login alone creates neither. Each grant must fit an active approved membership and its effective/expiry/revocation window. A suspended account, unavailable branch or invalid hierarchy denies access. All newly added or widened grants currently require a separate authorized approver; no named approvers or initial business scopes are seeded by application startup.

## Immutable maker/checker rules

Commands load the resource's immutable actor IDs and current revision from server-owned history. Changing a role or logging in again does not change who performed earlier work.

| Action | Required independence |
| --- | --- |
| Verify payment, disbursement or reconciliation | Verifier differs from recorder |
| Approve credit, refund, funding or expense | Approver differs from requester |
| Approve stock adjustment | Approver differs from both counter and proposer |
| Record disbursement | Recorder differs from the prior approver |
| Approve accounting period | Approver differs from submitter |
| Close accounting period | Submitter, approver and closer have three distinct immutable account IDs; trusted person-level equivalence remains ACC03 |
| Approve reopening | Approver differs from requester and original closer; revision advances |
| Approve access/membership | Approver differs from proposer and beneficiary |

Missing history and stale revisions fail closed. Actual business endpoints and their regression cases must integrate these policies when their owning modules are ported; this catalog alone does not fix the legacy endpoints.

## Delegation and revocation

Possessing a permission does not allow giving it to someone else. Grant approval requires `access.approve`, matching scope and an explicit list of delegable permissions in one eligible approver grant. Requested permissions and further delegation both fit inside that list. Membership approval requires separately granted `membership.approve` for the proposed scope. A system administrator can prepare a proposal but cannot activate it by being its author.

Immediate revocation/suspension is separately permissioned and audited. Reactivation cannot be a generic status toggle that bypasses review. Initial named business approvers and any emergency recovery procedure require a reviewed operational process before launch; there is no production test-login or automatic superuser.

## Organization lifecycle

Organization display metadata lives in `organizationProfiles`, keyed by its existing `authorizationScopes` ID. A missing profile is shown as missing metadata, never filled with a guessed company, branch or account name. Parent and node kind remain immutable; correcting an incorrect hierarchy requires a separately reviewed migration rather than editing a display form.

Creation, profile updates and reactivation are proposals. They carry the intended kind/parent/code/name, expected scope/profile versions, requester and reason. Pending creation does not create an active scope. `organization.approve` is a separate explicit permission; submitting a proposal does not confer approval authority. Another eligible approver must review the current proposal and state. Rejection and requester withdrawal are terminal, versioned decisions with actor, reason and time.

Reactivation includes a reviewed impact snapshot: affected scopes/accounts, active-status memberships/grants, and a revision of the underlying records. Impact includes permissions that cover the reopened scope from an ancestor, such as organization-wide access when a branch reopens. These counts describe potential impact, not a claim that every account can immediately sign in. An approver must differ from the requester and every affected beneficiary. Approval recomputes the revision and rejects stale impact; it must update affected identity versions/fences so an earlier session cannot resume business access merely because an ancestor was reopened. Approval, scope/profile state, proposal resolution, audit and idempotency result belong to the same transaction.

## Decision recovery and access-read audit

Access proposals distinguish rejection from withdrawal while preserving the existing storage status contract. Withdrawal is stored as rejected with an explicit withdrawal resolution. Decision metadata is written together, and current authority and expected revision are checked even when the requester closes their own proposal.

Sensitive access reads carry a bounded purpose such as access review, account support, security investigation or operation recovery. Query scoping applies before count/order/page selection. Read audit records contain the actor/version, operation, purpose, correlation ID, safe selection identifiers, returned count and relevant scopes. They do not store a repeated command body, raw idempotency key or financial contents. The read-audit collection has no public reader endpoint, avoiding recursive audit exposure.

Historical access events use an explicit typed scope when available. Legacy immutable history stays unchanged; a reader may derive scope only from recognized server-owned grant/membership snapshots and must fail closed for unrecognized scope. Access audit permission never grants unrelated business-document visibility.

Uncertain mutations can be checked using a purpose-limited receipt reference bound to actor, operation and key. The browser stores only that reference and time, not a stale access payload or reason. A missing receipt cannot simply be labelled safe to retry: closing it must atomically occupy the same idempotency identity as the original command. This ensures either the command or the closure wins, with no delayed execution after a successful closure. Receipt lookup/replay still enforces current authorization and does not expose another actor's result.

## Enforcement boundary

`ActorContext` carries a positive immutable user ID, verified provider issuer/subject, current grant version and verified factor/authentication time. Database resolution binds all identity fields. Roles, emails, arbitrary headers and legacy cookies do not supply this actor. MFA is always required by the catalog; sensitive access/recovery/refund actions require a recent verified factor event. T03 must supply and test that assurance through the actual session and origin arrangement.

Queries apply scope and active-resource checks before count, ordering and pagination. Direct ID lookup, exports, private attachment download and event delivery must use the same policy. Upcoming business modules must provide server-owned scope fields and may not pass a user-chosen collection or arbitrary filter into the authorization store. Query tests include malformed stored scope/ownership fields because Mongo scalar equality can otherwise match array elements.

Authorized writes read policy, update an actor fence matching active identity/grant version, and touch relevant scope fences inside the same Mongo transaction as the business effect. Access changes update the same identity fence/version. A concurrent revocation, membership suspension or scope closure therefore conflicts with a stale in-flight write. Transaction retries reload policy.

Idempotency replay also reevaluates current authorization before returning a prior response. A revoked session cannot recover privileged data by retrying an old key. Body hashes are derived on the server from the command, target/version and audit reason, not supplied by the browser.

## API and administration UI

`/api/v1/access` contains current access, the template catalog, subject access, proposals, server permission preview and approved grant/membership changes. All responses, including authentication/binding failures, use `Cache-Control: no-store`. IDs are positive decimal Int64 strings; versions remain strings. Scope kinds are named strings. Unknown JSON fields and numeric IDs/enum values are rejected.

Mutation requests require `Idempotency-Key`; commands against an existing version also require one canonical quoted `If-Match`. Mutations and receipt recovery require `X-Expected-Actor-Id`, which is checked against the verified actor to reject stale tabs after an account switch; this header never authenticates a user. The generated API contract declares these headers. The browser retains a key for an unchanged uncertain request and refreshes/reviews stale state. Per-resource allowed actions come from the server and are only display hints: every command is reevaluated authoritatively.

Administration forms show scope, role/finance packs, effective dates, permission preview and audit reason. Proposal approval is a separate action by an eligible person. T03 supplies invitations and human profile/directory context; current access references must never be replaced with invented names. T14 retains the final complete-workflow accessibility and browser acceptance gate.

## Persistence and remaining gates

Migration history currently advances sequentially through v6 while foundation and authorization document formats remain at schema version 1; v4/v5 add identity and provider-lifecycle collections, and v6 adds explicit catalog entitlements and assortment ownership. Applied v1/v2 steps are frozen. Lifecycle additions preserve stronger existing validators, check incompatible rows and duplicate scoped codes before stricter schemas, and never invent approved memberships or named scopes. Organization profiles use a unique kind/parent/normalized-code key. Migration raises relevant ID sequence high-water marks without lowering larger counters or backfilling display names. Migration requires an exclusive deployment window; Mongo DDL is not part of the business transaction.

Evidence and unresolved work are recorded in `docs/quality/production-progress.md` and `production-acceptance.md`. T04 acceptance does not imply production identity, migrated users, ported business workflows, live integrations or an accepted pilot. Required later checks include provider/CSRF/session behavior (T03), each module's scoped routes/files/events, real migration/import mapping (T15), and named operational approvers plus pilot approval (T16).

## Explicit business permission additions

Buyer spending approval, store operational acceptance, substitution proposal/consent, stock conversion, reservation release and supplier change approval have distinct permission names. They are available for explicit reviewed additions; v1 role snapshots retain their materialized powers. The server publishes role suggestions separately, and the forms require a deliberate selection before including one in the reviewed request. Read-only history/report names do not introduce inactive-resource or confidential-field exceptions. Exact scopes, immutable evidence, role suggestions and remaining catalog/history contracts are recorded in [business access contracts](business-access-contracts.md).

## Shared catalog visibility

Catalog access uses explicit approved assortment entitlements in addition to current scoped catalog.read authority. It does not assign every SKU to a buyer or relax private order ownership. Buying and explicitly assigned fulfillment audiences retain complete grant/member/owner evidence. The [catalog visibility contract](catalog-visibility.md) defines publication tuples, metadata-only projections, revocation fences and the required CAT01 publication protocol. Actual product routes, authorized pricing and delivery adapters remain integration gates.
