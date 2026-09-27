# Catalog visibility and publication contract

Status: ACC02.3 producer boundary locally accepted; see [verification evidence](../quality/catalog-visibility-acceptance-2026-09-28.md). The service provides the catalog-read boundary for Catalog and Documents consumers. CAT01 product publication, governed entitlement issuance, pricing audiences and actual file/export/event delivery remain required integrations.

## Current authority and separate ownership

`CatalogVisibilityPolicy.Compile` starts from the existing `catalog.read` permission. It joins one current actor, eligible grant and its active membership to an explicitly approved, effective, unrevoked entitlement. The entitlement audience must be contained in that same grant. A different membership or a wider grant from another alternative cannot fill in missing authority.

An entitlement names a server-owned active assortment and preserves requester, independent approver, approval reference/version, approval time and record version. Buying audiences may share a fulfillment assortment across buyer organizations. Fulfillment shop/warehouse audiences also require that their own scope contain the assortment owner. No administrator, owner or role acquires catalog visibility automatically.

The complete alternative retains entitlement, assortment, grant, membership and active audience/owner ancestor nodes. Assortment IDs alone are not authorization. The existing BuyerOwned order creator/assignment rule is unchanged.

## Persisted publication shape

Migration v6 introduces `catalogEntitlements` and `catalogAssortments`, with strict Int64 identifiers and canonical scope objects. It does not seed access or modify existing product records. Lifetime history is retained; historical inactive entitlements must not consume the bounded active-candidate allowance.

A SKU publishes through complete pairs:

```text
skus {
  _id: positive BSON Long,
  organizationId: positive BSON Long,
  catalogListings: [
    { assortmentId: positive BSON Long, ownerScope: canonical fulfillment scope }
  ],
  publicId?: canonical lowercase UUIDv4,
  displayName?: bounded plain display name
}
```

There are 1–128 listings, with unique assortment IDs; each owner belongs to the SKU's catalog organization. The same SKU can appear in different shop/warehouse assortments without duplicate product identities. Matching must use assortment and owner from the same array element. MongoDB's [element-match semantics](https://www.mongodb.com/docs/manual/reference/operator/query/elemmatch/) support this requirement. Expressions that inspect arrays guard their own inputs because [logical conjunction does not guarantee error short-circuiting](https://www.mongodb.com/docs/manual/reference/operator/query/and/).

CAT01's publication writer **must atomically increment the version of every affected assortment when adding, removing or moving a SKU listing**, in the same transaction as the publication change. This serializes publication withdrawal with catalog counts, pages and individual reads. Raw direct SKU edits outside this protocol do not meet the production contract. Product metadata, package definitions and prices still require CAT01's own version rules and indexes.

## Read and delivery boundary

`CatalogVisibilityStore` offers bounded query/search/count, internal-ID lookup, public-UUID lookup and image/export/event metadata-reference lookup. All use the same canonical filter before count and pagination; multiple matching listings return one SKU. Unknown/unavailable items return no metadata. Ambiguous eligible public references fail closed until CAT01 establishes database uniqueness.

The service returns only internal ID, public ID and display name. It does not return costs, price tiers, evidence files or bank information. Current authority is loaded in a Mongo snapshot transaction and fenced before return, including actor/session, applicable grants/memberships, entitlement, assortment and hierarchy. Effectivity is checked again after reading and fencing. Revocation conflicts retry against current state or deny.

An image/export/event metadata reference is **not** a reusable authorization token or file capability. Each eventual delivery adapter must call current authorization at delivery and enforce the owning document/immutable-version policy. These adapters, product screens and authorized pricing are separate integration acceptance; a test of this service does not prove they exist.

Read fences are operational counters. They do not alter the permission version or invalidate a reviewed organization reactivation by themselves; actual access/status/scope/version changes still invalidate that review.

## Business decisions and acceptance

[Report decisions](../quality/report-decisions.md) records the pending launch catalog/pricing audience and approval-owner questions. No entitlement issuer or business policy is inferred from `catalog.manage`, system administration, membership approval or a test fixture. Activation requires the approved issuance workflow and appropriate migration/publication indexes.

ACC02 historical-read permissions and suspended-branch grant lifecycle remain independent open work. See [business access contracts](business-access-contracts.md), [access plan](../superpowers/plans/2026-09-28-report-access.md) and [97-item register](../requirements/unit-toko-report-2026-09-28.md).
