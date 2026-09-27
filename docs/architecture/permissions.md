# Unit Toko access model

Status: T04 implementation and integration review in progress, 28 September 2026. The production API still rejects unverified actors. Provider login, invitations and sessions belong to T03; the existing demo roles cannot authenticate to this API.

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
| Close accounting period | Submitter, approver and closer are three distinct people |
| Approve reopening | Approver differs from requester and original closer; revision advances |
| Approve access/membership | Approver differs from proposer and beneficiary |

Missing history and stale revisions fail closed. Actual business endpoints and their regression cases must integrate these policies when their owning modules are ported; this catalog alone does not fix the legacy endpoints.

## Delegation and revocation

Possessing a permission does not allow giving it to someone else. Grant approval requires `access.approve`, matching scope and an explicit list of delegable permissions in one eligible approver grant. Requested permissions and further delegation both fit inside that list. Membership approval requires separately granted `membership.approve` for the proposed scope. A system administrator can prepare a proposal but cannot activate it by being its author.

Immediate revocation/suspension is separately permissioned and audited. Reactivation cannot be a generic status toggle that bypasses review. Initial named business approvers and any emergency recovery procedure require a reviewed operational process before launch; there is no production test-login or automatic superuser.

## Enforcement boundary

`ActorContext` carries a positive immutable user ID, verified provider issuer/subject, current grant version and verified factor/authentication time. Database resolution binds all identity fields. Roles, emails, arbitrary headers and legacy cookies do not supply this actor. MFA is always required by the catalog; sensitive access/recovery/refund actions require a recent verified factor event. T03 must supply and test that assurance through the actual session and origin arrangement.

Queries apply scope and active-resource checks before count, ordering and pagination. Direct ID lookup, exports, private attachment download and event delivery must use the same policy. Upcoming business modules must provide server-owned scope fields and may not pass a user-chosen collection or arbitrary filter into the authorization store. Query tests include malformed stored scope/ownership fields because Mongo scalar equality can otherwise match array elements.

Authorized writes read policy, update an actor fence matching active identity/grant version, and touch relevant scope fences inside the same Mongo transaction as the business effect. Access changes update the same identity fence/version. A concurrent revocation, membership suspension or scope closure therefore conflicts with a stale in-flight write. Transaction retries reload policy.

Idempotency replay also reevaluates current authorization before returning a prior response. A revoked session cannot recover privileged data by retrying an old key. Body hashes are derived on the server from the command, target/version and audit reason, not supplied by the browser.

## API and administration UI

`/api/v1/access` contains current access, the template catalog, subject access, proposals, server permission preview and approved grant/membership changes. All responses, including authentication/binding failures, use `Cache-Control: no-store`. IDs are positive decimal Int64 strings; versions remain strings. Scope kinds are named strings. Unknown JSON fields and numeric IDs/enum values are rejected.

Mutation requests require `Idempotency-Key`; commands against an existing version also require one canonical quoted `If-Match`. The generated API contract declares these headers. The browser retains a key for an unchanged uncertain request and refreshes/reviews stale state. Per-resource allowed actions come from the server and are only display hints: every command is reevaluated authoritatively.

Administration forms show scope, role/finance packs, effective dates, permission preview and audit reason. Proposal approval is a separate action by an eligible person. T03 supplies invitations and human profile/directory context; current access references must never be replaced with invented names. T14 retains the final complete-workflow accessibility and browser acceptance gate.

## Persistence and remaining gates

Migration history advances sequentially from v1 to v2 while foundation and authorization document formats remain at schema version 1. The authorization upgrade preserves stronger existing validators, checks incompatible records before applying stricter schemas and never invents approved memberships. Migration requires an exclusive deployment window; Mongo DDL is not part of the business transaction.

Evidence and unresolved work are recorded in `docs/quality/production-progress.md` and `production-acceptance.md`. T04 acceptance does not imply production identity, migrated users, ported business workflows, live integrations or an accepted pilot. Required later checks include provider/CSRF/session behavior (T03), each module's scoped routes/files/events, real migration/import mapping (T15), and named operational approvers plus pilot approval (T16).
