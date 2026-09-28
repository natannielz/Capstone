# Inventory quantities and stock authority contract

Status: locally verified pure domain rules for CAT02. Persistence, transactional stock movements, valuation, usable inventory screens and independent review are still required. These rules are not registered as API routes and do not activate a second stock writer.

## Implemented quantity rules

StockLocation uses positive organization, shop, warehouse and channel IDs and converts to the existing exact warehouse GrantScope. The service must still load the actual topology and authorize the requested scope. Channel names such as OMI and S-Mart are display data, not identifiers.

StockBalance counts packages of one exact SKU, immutable definition and lot. OnHand includes Held and Expired; those two classifications are disjoint. Reserved belongs only to the remaining eligible stock:

    available packages = OnHand - Held - Expired - Reserved

Validation subtracts each bucket in order, so malformed near-Int64 totals cannot overflow an intermediate sum. Negative counts, bucket overcommit, missing identities and nonpositive revisions fail. Pure receive, reserve, release, dispatch, hold, release-hold, expire and held-to-expired transitions retain the balance key and advance the expected revision once. Dispatch requires previously reserved quantities. Moving held packages into expired packages preserves physical stock and does not subtract their availability twice.

The transaction owner supplies positive derived Int64 package totals. User-facing commands separately enforce their bounded integer quantity or step limit. One million conversion steps from a 24-piece carton can yield 24 million loose packages; applying the user step ceiling to that derived result would reject a valid conversion. Arithmetic remains checked through Int64 exhaustion. These pure transitions do not implement receipt deduplication, actor authority, physical expiry, cost or database concurrency.

## Conversion and historical definitions

PackConversionPolicy accepts the explicitly directed conversion definition and both StockPackageDefinition values. Each package includes the SKU, its current immutable definition and the immutable definition actually attached to the stock lot. This deliberately strengthens the plan's illustrative signature: a definitions-only function cannot check the owning organization or physical-item key.

Catalog's existing validation checks the current SKU/definition and the stored definition. The old definition must belong to that SKU, precede its current version/time and preserve material facts: package kind, base unit, contents and intrinsic attributes. The same definition ID cannot carry different facts. A descriptive label/image/order-rule revision may leave older stock usable; its original definition remains attached to the lot.

Conversion additionally requires the same organization, exact physical-item key, base unit and intrinsic attributes. Source and destination SKU/definition IDs must match the approved direction. Positive explicit per-step ratios and bounded user steps produce checked Int64 source packages, destination packages and base units. Both sides must conserve exactly the same base-unit total. A shared word such as Dus supplies no quantity. Reverse packing requires a distinct directed definition.

The eventual service must verify actual persisted catalog relationships and current approval, actor permission, source/destination location, lot/expiry provenance, permitted package condition and exact cost-layer conservation. Arithmetic success alone cannot prove a box is sealed or approve a reverse conversion. No cost policy or rounding rule is invented in this stage.

## Source-authority decision

SourceStockPolicy binds the exact requested location to a physical pool and pool revision, a policy revision, authority mode, explicit maximum observation age, safety buffer and external namespace where relevant. StockAuthorityReview references the exact policy and physical-pool revisions plus a review record. SourceStockObservation references the same versions and carries its own revision, observed/reconciled times and source batch reference.

StockAuthorityPolicy checks those values against the expected policy, pool and observation revisions at explicit server UTC time. It returns one of:

| Disposition | Meaning |
| --- | --- |
| Unavailable | Authority is missing, unconfirmed, unreviewed, mismatched, stale or unreconciled. The reason identifies the blocking condition. |
| WebLedger | Reviewed exclusive web allocation or shared-web-ledger control may proceed to authoritative ledger availability/reservation checks. This is not a quantity or reservation. |
| ExternalHoldRequired | An external authority has a reviewed hold protocol, but an actual bound, acknowledged and unexpired external reservation is still required. A fresh file alone grants no promise. |

Missing or mismatched observations do not produce fabricated zero quantities or leak their timestamps through the decision. Correctly bound stale observations retain their dates so a future authorized projection can explain their age. Future or inconsistent observation/reconciliation times deny. Freshness is a half-open interval: the observation is unusable at the exact maximum-age deadline. A later reconciliation time never refreshes an old observation.

MaximumAge is supplied policy data, positive and expressed in whole milliseconds to match persisted timestamp precision. No default age, buffer, external namespace, protocol or actual pool ownership has been selected for OMI or S-Mart. Noncanonical identities, times, text and enum values fail validation.

Construction of these records is not evidence of approval. Only a future authorized service may load them from current persisted review/policy/observation records and fence those exact records inside the business transaction. No HTTP endpoint accepts a review record from a client. SourceAuthorityDisposition deliberately does not expose CanPromise: a pure authority check has not checked stock, reservations, expiry, buffer, price or an external acknowledgment.

## Physical-pool integration requirements

- Policy administration must prove current maker/checker/person authority, retain exact approval history and atomically fence updates. Actual people, owners and business policy remain the D04/OPS02 decisions.
- Each overlapping OMI/S-Mart pool must have one canonical physical balance and writer across every channel. SharedWebLedger is a declared control, not proof that this persistence exists. The future repository must resolve the pool before deriving its storage key; it must not create independently spendable copies by channel.
- Exclusively allocated web pools need accepted allocation/reconciliation evidence. External pools need the actual capability and acknowledgment protocol demonstrated with the source owner.
- Availability aggregates eligible compatible lots once, reserves against the same physical pool and applies the approved safety buffer once in base units before package conversion. Unknown authority must remain an explicit confirmation-needed state, not a numeric zero.
- Quote and order submission consume the same current policy, pool and observation versions. The final transaction rechecks the clock and all relevant authorization/version fences after awaited work.
- Source imports deduplicate exact external event keys and hashes. An acknowledged web-origin echo reconciles its original effect without dispatching physical stock or recognizing money again.
- Receiving, reservation/release, conversion, dispatch, expiry and approved count adjustment persist balanced movements, cost provenance, audit, receipt and outbox atomically. The existing immutable count observation is not an opening balance or adjustment.
- Ledger migrations must preserve the existing foundation collections and history. Do not rewrite applied migrations or infer opening stock/costs from demo records.

## Decisions and next work

The following actual decisions are still unavailable: physical pool overlap/allocation, stock writer per pool, source tenant and identifiers, feed interval/age ceiling, safety buffer, hold protocol, discrepancy owner/handoff, opening quantities/costs and finance-approved valuation. Synthetic tests exercise policy shapes only.

Continue CAT02 with a forward schema/graph preflight, authoritative pool/lot ownership, reviewed source-policy persistence, transaction-aware inventory operations and cost-layer handling. Then connect the usable stock/conversion/reservation/availability workflows and their concurrency/browser checks. OPS02 owns real source acceptance; OPS06 owns the coordinated one-writer switch and reconciliation. The current tested primitives do not complete CAT02 or F04.

## Private Mongo balance storage checkpoint

InventoryRepository now implements the private persistence layer under the active caller transaction. InventoryBson requires exact typed balance records; InventorySchemas defines the strict draft storage validator and physical-pool uniqueness. The production migration registry remains 14. The repository neither installs schema nor exposes an API.

The storage key is physicalPoolId plus skuId, definitionId and lotId. Request channel is not part of that uniqueness key. The stored location is the pool's canonical inventory owner; a later authorized service must resolve a request channel through accepted pool mappings before selecting that owner. The helper does not prove that a supplied pool or lot actually exists.

Create requires revision one. Read decodes the complete persisted record. Apply freezes a batch of 1–100 distinct balance references, loads each actual row, compares its pool/key/expected revision, calculates a closed-set quantity transition and conditionally replaces the complete previous image. Int64 IDs and quantities remain BSON Int64. An exact active same-client transaction, strict required schema and safe unique index are mandatory.

Any failure aborts the caller's entire transaction, including previous changes outside the inventory helper. Cancellation, a stale later entry, an invalid stored row or a missing/weakened storage constraint cannot be caught and followed by committing the earlier half of the operation. A caller failure after successful updates also rolls back through the existing unit of work. A component never returns an idempotent replay success; whole-command receipts remain the owning service's responsibility.

The metadata guard permits the required validator only as a mandatory constraint, rejects disabled validation and an optional OR escape, and rejects missing/weaker canonical uniqueness, extra restrictive unique indexes and TTL indexes. Schema/index DDL belongs to a reviewed deployment that stops writers. These checks do not replace a forward migration's complete existing-data/graph preflight, database privileges or operational isolation.

Real-Mongo tests now verify the mechanics, including two transactions forced to read the same final package before either reserves it. They install the draft schema explicitly in generated local test databases only. This remains a persistence component, not the complete stock ledger: actual pool/lot/source/catalog ownership, current authorization, immutable movements and source deduplication, valuation/accounting, audit/outbox/receipts, migration registration and staff/API workflows are still required in the same owning command.

The final lot model must also bind each real lot to its immutable SKU/definition and enforce its allowed balance cardinality. The draft full-key index alone cannot prove that binding or prevent fabricated references to another pool/SKU/definition. That graph and its final uniqueness constraints belong in the forward migration and owning service before activation; repository tests deliberately do not claim them.
