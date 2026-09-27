# Governed historical reads

Status: implemented ACC02 producer and administration boundary; verification is recorded separately in the historical-access acceptance report. Actual order, invoice, shipment, file, export and event endpoints remain owning-module work. No production reviewer or live grant is created by this implementation.

## Authority and grant lifecycle

The six public role templates are unchanged. A specialized permission grant uses the internal marker `historical-read`, version 1, and explicitly selected `history.read` and/or `report.read`. It is not a seventh role, has no delegable permissions and is rejected by ordinary `ScopePolicy` grant compilation. Ordinary transaction, settlement, reactivation and financial powers remain separate.

The beneficiary must have a currently active, independently approved BuyingOrganization membership for the exact parent. A suspended branch membership cannot substitute for that membership. The organization must remain active. A target must be a canonical BuyingBranch or BuyingDivision whose branch and any named division are active or suspended, with positive current versions and matching parents; pending/revoked targets deny. Grant lifetime cannot exceed the parent membership.

`AccessAdministrationService` previews and proposes the typed historical request. The existing grant proposal lifecycle independently approves, rejects, withdraws and revokes it. Approval requires a different account from requester and beneficiary, fresh ordinary approval authority and one complete grant that delegates every requested permission. Split delegation across grants cannot be combined. The stored request must match the proposal envelope's identity, beneficiary, requester, reason and scope. Normalized permissions and the historical marker participate in idempotency; current authorization is still checked for a replay.

The three new browser routes are:

- `POST /api/v1/access/historical-targets/query`: exact beneficiary and approved parent membership; scoped count and pagination, safe unit names/codes only.
- `POST /api/v1/access/historical-grants/preview`: explicit permissions, target and validity.
- `POST /api/v1/access/historical-grants/proposals`: reviewed grant plus bounded reason and idempotency key.

Existing actor, origin/antiforgery, string-ID, version and receipt requirements apply. Target lookup needs current access-proposal authority; it does not give a global organization directory. Only the current recipient's eligible organization descendants count toward pagination. The administration screen requires a current server preview, resets changed drafts, offers no financial or delegation controls and preserves immutable same-key retry plus body-free recovery references.

## Read contract

`HistoricalReadPolicy.Compile` produces complete grant/membership alternatives. Accepted permission-purpose pairs are exactly `history.read / historical-review` and `report.read / reporting`. Requests always name a branch or division; there is no inferred all-organization historical query. A branch grant can cover a named contained division. A division grant cannot cover its parent branch.

A branch query includes valid active/suspended divisions beneath it. Each child remains part of the exact target evidence. A division query matches only that division. Canonical buying organization/branch/division fields establish ownership. Valid secondary fulfillment dimensions neither grant buying authority nor require the fulfillment owner to remain active; malformed, orphaned or nonpositive dimensions deny.

`HistoricalReadStore` accepts immutable server registrations for `orders`, `invoices` and `shipments`. No browser-controlled collection, projection or Mongo predicate is accepted. The same typed buying-scope filter applies before search, count, pagination, internal ID and canonical UUID-v4 lookup. Ambiguous eligible public identifiers deny. Page limits are 1–100, offset 0–10,000, search is escaped literal text of at most 128 characters and current authority is bounded to at most 256 unique grants/memberships.

The projection contains only `_id`, `publicId` and `displayName`. Those display fields must be approved safe metadata supplied by the owning module. History permission does not disclose financial evidence, bank data, margins or raw documents. The image/evidence/export/event methods return metadata references only; they do not deliver content or mint reusable capabilities.

## Transaction and audit boundary

Metadata selection and its audit execute in the same Mongo snapshot transaction. Conditional writes fence the current identity/session, complete grants and memberships, active organization authority and exact branch/division kind, parent, status and version. Expiry is re-evaluated during the operation. A concurrent change must conflict, retry against current authority or deny; an aborted read returns no result and commits no business-read audit.

Migration v7 adds strict `businessReadEvents` with no seeded grants and no rewrites to earlier migrations. Audit records include actor/revision, source, permission/purpose, operation, exact target, grant and membership IDs, bounded selection metadata, result count, correlation and timestamp. They omit raw search text, document content and evidence. The business read source registry cannot read this audit collection recursively.

The migration preflights records, collection options, retention, indexes and sequence high-water before mutation. It preserves stricter existing validators, resumes with majority DDL and rejects incompatible TTL/capped/time-series/collation/index definitions. Retention remains a separately approved operational policy; this migration does not expire audit evidence.

## Owning-module integration requirements

1. Preserve immutable canonical buying ownership for order/invoice/shipment history. If business records may move between buying scopes, serialize the move with these reads using an explicit revision boundary before accepting that module. Authorization fences alone do not lock arbitrary resource-row changes.
2. Use module-owned safe display metadata and appropriate indexes. Performance/load acceptance belongs to the actual business endpoint and dataset.
3. Reauthorize at real file, evidence, export or event delivery. A prior metadata reference, enumeration or grant snapshot is not continuing authority. Sensitive fields need the owning field/document policy in addition to history access.
4. Test equivalent count/search/list/detail/delivery isolation, account/session/authority revocation, target changes and audit rollback through the actual module endpoints. Producer fixtures cannot satisfy nonexistent routes.
5. Keep every write on the ordinary current-active authorization path. Historical reading does not enable new transactions, reservation changes, invoice issue, payment approval or suspended-branch settlement.

Business choices remain in [report decisions](../quality/report-decisions.md): named historical reviewers, access approvers and any future historical settlement policy. Trusted person independence is ACC03; current independent-account checks do not prove two accounts belong to different people.
