# Business access contracts

Status: ACC02.1–.2 permission and optional-role-suggestion implementation; ACC02.3 catalog producer boundary locally accepted with [verification evidence](../quality/catalog-visibility-acceptance-2026-09-28.md). Governed historical-read producer and administration adapters are implemented; actual business/delivery integration, trusted person independence and production business ports remain separate acceptance gates. These are producer contracts, not evidence that the legacy routes already enforce the new model.

## Explicit business permissions

| Permission | Allowed scope | Required evidence |
| --- | --- | --- |
| `order.spend.approve` | Buying division, branch or organization | Current expected order revision and immutable requester; different approving account; fresh MFA |
| `order.accept` | Fulfillment shop or warehouse | Current expected order revision and immutable requester; operational acceptance does not approve buyer spending |
| `substitution.propose` | Fulfillment shop or warehouse | Owning order module records the exact proposed revision and immutable proposer |
| `substitution.accept` | Buyer-owned, buying division, branch or organization | Exact proposed revision, positive immutable requester/proposer, and current ownership/assignment or explicit wider buyer authority |
| `stock.convert` | Fulfillment shop or warehouse | Inventory owns package conversion and validates units, ratios, stock and idempotency |
| `reservation.release` | Fulfillment shop or warehouse | Inventory/order owner validates current reservation, permitted state and authoritative release reason |
| `supplier.change.propose` | Fulfillment shop or warehouse | Supplier module records exact proposed changes and immutable proposer |
| `supplier.change.approve` | Fulfillment shop or warehouse | Exact proposed revision, independent approving account and fresh MFA |
| `history.read`, `report.read` | Explicit buying or fulfillment scope | Read-only names; no administrative mutation, transaction approval, confidential-field access or inactive-resource exception follows from their registration |

The module owning a command loads its immutable requester/proposer and current revision from stored state inside the authorization transaction. A client-supplied actor, echoed version or suggested role does not establish those facts. Existing current-access, membership, complete-grant scope, session and transaction fences still apply. Person-level independence requires ACC03; two different accounts alone are not proof of different people.

The existing `order.approve` and `order.substitute` entries and v1 materialized grants retain their meanings. New commands require their new exact permission names, not aliases to old snapshots. The legacy `substitution.propose`/`substitution.decide` workflow is unchanged until its owning port is verified. Buyer budgets/thresholds/credit holds and receiving-bank changes require their own approved business rules and command contracts.

## Optional role suggestions

The catalog exposes suggestions separately from a role's materialized permissions. A suggestion is a form hint, not a default grant, a template-version migration or proof that the proposer may delegate it.

| Existing role | Suggested explicit additions |
| --- | --- |
| Buyer | `substitution.accept` |
| Store Staff | `substitution.propose`, `stock.convert`, `reservation.release`, `supplier.change.propose` |
| Store Head | `order.accept`, `supplier.change.approve` |
| Courier, Finance, System Admin | None |

Buyer spending approval is never a suggested/default addition. A named authorized approver receives it through the existing explicit request, preview, independent approval and delegation checks. Every new grant preserves the reviewed scope, validity and exact permission snapshot. Editing suggestions never changes that snapshot; the six v1 base templates remain unchanged. ACC03 supplies future reviewed template migrations.

## Catalog visibility: implemented producer boundary

Ordinary BuyerOwned matching remains appropriate for orders and is unchanged. The catalog compiler joins current `catalog.read` authority to an explicitly approved entitlement for the same actor and membership. The entitlement audience must fit the same eligible grant. Active assortment ownership and all required hierarchy nodes remain part of the complete evidence; an assortment ID alone is not authorization.

Buying audiences may share assortments across buyer organizations. Explicit fulfillment shop/warehouse audiences must additionally contain the assortment owner. No role, owner, system scope or courier scope receives implicit catalog access. Entitlement records preserve immutable requester/approver/reference/version/time provenance; person-level independence still belongs to ACC03.

The platform service applies one canonical assortment/owner-pair filter before search, count, pagination and internal/public lookup. Multi-assortment products return once, hidden products do not affect totals, malformed publication data stays unavailable, and ambiguous eligible UUIDs deny. Its projection contains only ID, public ID and display name. Identity/session, grant, membership, entitlement, assortment and hierarchy fences protect reads against concurrent changes.

Migration v6 adds strict entitlement/assortment collections without seeds or changes to existing SKU records. SKU publication uses complete `catalogListings` tuples. CAT01 must atomically bump every affected assortment version when publishing, withdrawing or moving a SKU; metadata and price versioning require their own owning-module rules. Read counters do not invalidate organization reactivation reviews, while semantic access changes still do.

Image/export/event methods currently return metadata references only. They are not file capabilities or delivery adapters. Actual product endpoints/screens, governed entitlement issuance, pricing policies, product indexes and delivery-time document authorization remain CAT01/PAY01 integration gates. The legacy catalog has not adopted this model. See the [catalog visibility and publication contract](catalog-visibility.md) for exact shapes and limits, and [report decisions](../quality/report-decisions.md) for pending business authority choices.

## Historical reads: implemented producer and administration boundary

Explicit historical grants retain active organization membership and current reader/session authority while allowing a named active/suspended branch or division. They reuse independently reviewed grant lifecycle and never enter the ordinary role/write policy. A branch read includes its valid descendants. One canonical filter precedes all metadata count/search/page/ID lookups; v7 persists a minimal transaction-bound business-read audit.

The scoped target selector and reviewed Indonesian administration flow expose proposal, approval and revocation through the existing actor/version/idempotency rules. Metadata references remain distinct from actual file/export/event delivery. Exact interfaces, limits, ownership invariants and remaining integration gates are in [governed historical reads](historical-reads.md). Verification is tracked in the access plan and production progress; these contracts do not claim real business routes or launch acceptance.

## Business and release gates

D02 still needs approved buyer approvers, assortment owners/issuers, pricing audiences, historical reviewers and any specific suspended-branch settlement rules. Settlement stays denied unless separately authorized and implemented; `history.read` never grants it. ACC03 still needs the staff/person evidence source, person-equivalence review and finance conflict classification. Every owning business module must integrate and test these contracts before production activation. See the [access implementation plan](../superpowers/plans/2026-09-28-report-access.md) and [97-item report matrix](../requirements/unit-toko-report-2026-09-28.md).
