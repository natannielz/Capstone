# Inventory quantities and stock authority contract

Status: locally verified domain rules and private Mongo balance/provenance checks for CAT02. Governed pool/lot/source creation, complete forward migration, transactional movements, valuation, usable ledger screens and independent review remain required. These components are not registered as API routes and do not activate a second stock writer.

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

The storage key is physicalPoolId plus skuId, definitionId and lotId. Request channel is not part of that uniqueness key. The stored location is the pool's canonical inventory owner; a later authorized service must resolve a request channel through accepted pool mappings before selecting that owner. The current helper also loads the actual pool and batch lineage as described below. It does not create those records or establish their business approval.

Create requires revision one. Read decodes the complete persisted record. Apply freezes a batch of 1–100 distinct balance references, loads each actual row, compares its pool/key/expected revision, calculates a closed-set quantity transition and conditionally replaces the complete previous image. Int64 IDs and quantities remain BSON Int64. An exact active same-client transaction, strict required schema and safe unique index are mandatory.

Any failure aborts the caller's entire transaction, including previous changes outside the inventory helper. Cancellation, a stale later entry, an invalid stored row or a missing/weakened storage constraint cannot be caught and followed by committing the earlier half of the operation. A caller failure after successful updates also rolls back through the existing unit of work. A component never returns an idempotent replay success; whole-command receipts remain the owning service's responsibility.

The metadata guard permits the required validator only as a mandatory constraint, rejects disabled validation and an optional OR escape, and rejects missing/weaker canonical uniqueness, extra restrictive unique indexes and TTL indexes. Schema/index DDL belongs to a reviewed deployment that stops writers. These checks do not replace a forward migration's complete existing-data/graph preflight, database privileges or operational isolation.

Real-Mongo tests now verify the mechanics, including two transactions forced to read the same final package before either reserves it. They install the draft schema explicitly in generated local test databases only. This remains a persistence component, not the complete stock ledger: governed pool/lot creation, actual source/catalog ownership, current authorization, immutable movements and source deduplication, valuation/accounting, audit/outbox/receipts, migration registration and staff/API workflows are still required in the same owning command.

The graph check below now binds an existing lot to its stored SKU/definition and rejects nonexistent or mismatched references. The draft full-key index alone does not establish that relationship. A governed lot writer, source uniqueness, allowed balance cardinality and complete historical-data preflight still belong in the forward migration and owning service before activation; structural graph tests do not claim those broader guarantees.

## Persisted pool and batch provenance checkpoint

Every private balance create, read and change now loads the actual inventoryPools and lots records inside the caller transaction. The pool has one exact canonical organization/shop/warehouse/channel owner. The batch binds one organization, SKU and immutable definition; a client cannot make an existing batch mean another package by supplying a different balance key. A missing, malformed or mismatched record aborts the entire caller transaction.

A root batch carries an openingReceipt or procurementReceipt origin and positive source-document/source-line identifiers. A packageConversion child carries a parent and root reference. The loader follows the actual ancestor chain, requiring the same organization, root and expiry date and nondecreasing recorded times. Missing ancestors, cycles, self-parent references and an expiry extension or removal fail. At most 64 records including the root are accepted; exceeding that technical bound is rejected, never silently truncated. Optional expiry dates remain null through the entire chain for items without expiry. Dates are canonical calendar dates, not a guessed expiry cutoff or eligibility decision.

Pool/lot rows require exact fields, positive BSON Int64 identities, a BSON Int32 schema version and the expected kind. Pool revisions/fences are typed Int64. Lot recorded times are representable BSON dates; unknown source kinds, fractional/coerced identifiers, duplicate fields and invalid dates fail. Count observations are not a supported opening-stock source.

A stock write additionally requires an active pool and conditionally increments its metadata fence using the complete server-loaded image. This makes a concurrent suspension/reconfiguration conflict with a stock transaction that read an older snapshot. A quantity failure or later stock leg rolls the fence back along with balances and earlier caller work. Historical reads can inspect a suspended or fence-exhausted pool without updating it. This internal read behavior does not waive the eventual API's authorization/audit requirements. Pool fencing deliberately serializes competing writers within a pool; realistic contention/load measurement and batching remain release requirements.

The graph loader validates structural provenance only. It does not authorize staff, create or approve pools/lots, load actual procurement/opening receipts, reserve source-event keys, verify a catalog conversion, decide package condition, apply expiry eligibility or conserve cost. Source-owning services must create immutable lot facts only after those checks, with unique origin constraints, movements, audit and command receipt in the same transaction. No authoring API, production source record, strict forward graph migration or runtime activation is introduced by this checkpoint. Actual business source/valuation decisions and independent review remain pending.

## Source-owned movement transaction checkpoint — 29 September 2026

InventoryLedger joins an existing same-client Mongo transaction. Its input is a canonical source reference, expected source revision, actor and bounded reason/correlation metadata. It does not accept a request-supplied list of stock quantities. Only explicitly configured IInventorySourceReader implementations for the same client and database can supply stock effects. No production reader is registered by this checkpoint.

A reader must load and conditionally write its actual approved source, verify the current actor/scope/source authority and return the permitted effects with their expected balance revisions and canonical pool/location/revision. The ledger checks the returned source identity/revision, freezes the effects, compares the actual pool, captures each actual balance before changing it, then inserts one append-only movement with the resulting images in the same transaction. One event contains 1–100 distinct balance changes within one physical pool; an owning workflow can compose multiple such events in its transaction when necessary.

The event key is namespace, source document ID, event kind and occurrence, using bounded ASCII codes and exact Int64 identities. A mandatory unique index prevents a second effect for that same event. Even an identical duplicate aborts the whole caller transaction: returning an old component result could otherwise commit work the caller performed before checking the duplicate. Whole-command idempotency receipts must be handled by the owning command before any effects. Cashier echoes under different external keys still require OPS02's reviewed correlation/reconciliation mapping; namespaced uniqueness alone does not solve cross-source duplication.

The movement retains source/pool revisions, actor/grant revision, UTC recorded time, reason/correlation and all before/after balance images. Its strict draft validator checks field types and the canonical source key. Mandatory query expressions also require unique balance legs, one canonical location, unchanged balance/pool/SKU/definition/lot identities, valid quantity buckets, exactly one revision increment and the exact receive/reserve/release/dispatch/hold/release-hold/expire/held-to-expired transition. Explicit bounds prevent overflow cases from being accepted through numeric promotion. These expressions validate the record's internal consistency; they do not establish actual source approval or catalog/cost conservation.

MongoDB documents combining JSON Schema with [query-expression validation](https://www.mongodb.com/docs/v8.0/core/schema-validation/specify-query-expression-rules/). The per-leg check maps each leg to a boolean and uses [$allElementsTrue](https://www.mongodb.com/docs/v8.2/reference/operator/aggregation/allElementsTrue/); it does not pass nested arrays as proof. Calendar date bounds use query predicates because JSON Schema's numeric minimum/maximum keywords do not accept BSON dates.

Any source rejection, stale record, cancellation, failed movement insert, duplicate event or later owning-command failure rolls back source/pool fences, all stock legs, the movement and earlier caller writes. The real PostJournal boundary is exercised as a downstream failing participant; this proves rollback composition, not a completed procurement or cost-posting workflow. ID allocation remains outside the business transaction, so aborted work may leave unused IDs.

The owning source services still owe genuine receipt/conversion validation, catalog/package/expiry/condition checks, financial valuation and posting, final authorization/source/clock fences, independent approvals where required, command receipts, audit/outbox and usable API/UI. Governed source/pool/lot creation, complete forward migration and preflight, immutable application/database access rules, independent review, production load tests and real source acceptance remain open. The migration registry stays at14 and the ledger has no activated API route or second stock writer.

## Governed new-pool setup checkpoint — 29 September 2026

The private StockSourceSetupService supplies a persisted new-pool proposal, independent approval/rejection, proposer withdrawal, exact-location reads and paged lists. It uses explicitly granted stock.source.read, stock.source.propose and stock.source.approve capabilities. Existing six role templates, finance packs and v1 defaults are unchanged. Approval/rejection uses current scope, MFA no older than five minutes, stored proposer history and the existing reviewed person registry; two accounts of one person cannot approve each other.

A proposal stores immutable explicit settings: canonical organization-wide physical key, exact owner location, label, authority/control mode, maximum observation age in milliseconds, Int64 safety buffer, external namespace/protocol where applicable, discrepancy-owner reference and handoff reference. These references describe the proposed configuration. They are not evidence that a physical pool, external reservation protocol or source connector has passed operational acceptance.

The proposal and its audit record commit together. One terminal decision per proposal is enforced by a unique database index. Approval atomically saves the decision, its audit record, an organization-wide physical-key registration and one inactive inventoryPools record. Rejection and withdrawal create no pool. No source-stock policy, accepted authority review, observation, quantity balance or inventory movement is created. The existing stock graph refuses writes to the inactive pool.

Whole-command receipts recover identical retries and reject changed bodies. A final completion callback runs for new work and replay; source setup uses it to recheck current assurance/time, live sessions and exact grant/membership/hierarchy snapshots before commit. Read/list operations use the same fenced scope boundary. Failed audit insertion, stale authority, conflicting decisions and duplicate physical keys roll back the entire command. Sequence allocations remain outside the transaction and may leave gaps.

Draft schemas cover the four new collections. Runtime methods require the exact strict validation contract and safe unique indexes; they never install or weaken database rules. Source mode/control/protocol combinations, canonical text/keys and BSON date ranges are validated as well as field types. MongoDB uses [PCRE2 regular expressions](https://www.mongodb.com/docs/v8.0/reference/operator/query/regex/); the draft uses the [absolute end anchor](https://pcre2project.github.io/pcre2/doc/pcre2syntax/#anchors-and-simple-assertions) so a final newline cannot create a second spelling of a physical key.

This checkpoint is a tested preparation service, not a launched feature. It has no HTTP route, DI registration, forward migration or staff screen. Registered migration count remains 14. Shared-channel aliases, changes/suspension/reactivation of an existing pool, governed policy/observation and lot authoring, final migration/preflight and indexes for operational load remain separate work. Activation requires actual D04 source/pool decisions and OPS02/OPS06 acceptance; free-text review references never activate a pool. Trusted-person source acceptance, business valuation/cost policy, independent review, load/restore/pilot and Vercel owning-account access remain open.

## Source-setup migration and API checkpoint — 29 September 2026

This checkpoint supersedes the preparation-only status above. The migration registry is now 15 and the source-setup service is registered behind seven protected HTTP endpoints. Exact decimal-string DTOs and generated client contracts preserve Int64 IDs/settings. Separate propose/read/approve grants and current actor, scope, fresh-MFA and person checks remain in force. Decision authorization checks permission and stored scope before private history; missing and inaccessible IDs produce the same denial.

Receipt recovery uses server-owned history and requires the receipt to exist inside the replay transaction. If it disappears after the initial read, the command fails without creating a replacement proposal, decision, audit or receipt. A missing lookup is reported as not_confirmed and must not be treated as permission to submit a fresh key.

Migration 15 preflights existing source data, unique keys, counter ranges and the full immutable provenance graph before target DDL/counter changes. It installs strict source/pool setup storage and query indexes, preserves stronger rules, raises counters only as needed and resumes interrupted DDL under the existing deployment lease. Pause/drain writers and verify a coordinated backup before any actual deployment. No lot, balance, movement or source-activation migration is supplied here.

The approved pool remains inactive. Operational source acceptance, staff UI, authorized warehouse choices, existing-pool governance, live source observations/lots, costs and owning workflows remain required. Verification and unresolved gates are recorded in [the API/migration evidence](../quality/stock-source-api-verification-2026-09-29.md).

## Source-setup staff and warehouse-choice checkpoint — 29 September 2026

The source workflow now has eight protected API routes and a usable staff page at /inventory/sources. The additional warehouse query maps explicit propose/history/review purposes to their separate permissions, expands active shop assignments into exact warehouse/channel resources, loads canonical reviewed organization names and fences scope/grant/membership/session/time before returning its audited result. Organization profile changes advance their matching scope version in the same transaction; the choices fence those captured nodes and retry or deny after a concurrent change.

The frontend keeps source configuration explicit, preserves exact numeric strings, freezes review before submission and clearly displays that approval leaves the pool inactive. Read permission is separate from proposal/approval. Source reviewers may discover their MFA verification entry after freshness expires, but all actual operations still enforce the relevant assurance.

Recovery stores references only, never request settings/reasons or person evidence. The owning source adapter checks the original actor/operation/key under a deliberately selected current location. Unknown outcome retains its reference; neither missing receipts nor a reload create a fresh request or close an old one. Navigation, stale response/session guards and independent-person decisions are exercised through real application/API/Mongo browser flows.

This supersedes the preceding no-staff-UI/no-choice status, without changing activation or business acceptance gates. See [the workspace evidence](../quality/stock-source-workspace-verification-2026-09-29.md). Existing-pool governance, observations/lots, operational migration, costs and owning workflows remain required.

## Governed settings revision service checkpoint — 29 September 2026

StockSourceSetupService now supports immutable existing-pool revision proposals, independent approval/rejection, maker withdrawal, current-settings reads and exact-location history reads. The input reuses complete explicit setup settings. Physical pool identity, canonical owner location and physical key cannot change; an unchanged proposal is rejected. Revision one resolves through the original audited setup; later versions resolve through immutable stockSourceRevisions uniquely keyed by pool and pool revision.

Approval requires an inactive pool and exact expected base settings/version. It advances the canonical pool revision/fence and saves revision, terminal decision, audit and whole-command receipt atomically, leaving quantities and operational source authority untouched. Any previously bound authority would have an obsolete pool revision; this service does not manufacture new acceptance. Active-source changes still need the full suspension/drain/handoff workflow before approval. Proposals can be prepared while active without changing the settings.

Current reads validate original creation, latest revision, its immediate approved predecessor, base settings/time and sequence completeness. This is a configuration workflow with an indexed history count, not the stock-availability path. Complete historical graph validation remains a forward-migration requirement. Current authorization, person independence, fresh MFA, final time fences and historical same-key replay reuse the existing command boundary.

See [revision verification](../quality/stock-source-revision-verification-2026-09-29.md). New storage remains draft-only and registered migration 15 is unchanged. Revision migration/recovery/API/UI, shared-channel aliases, actual acceptance/observations/lots and downstream inventory/cost workflows remain required.

## Registered settings revision migration — 29 September 2026

Migration 16 now installs stockSourceRevisionProposals, stockSourceRevisionDecisions and stockSourceRevisions. Applied version-15 behavior remains unchanged. Before target DDL/counter changes, the new step validates the entire original and revised configuration history, including every version from creation to the current head, original physical ownership, complete base snapshots, chronology, decisions, audits, exact request hashes/results and retained approval participants. Reverse traversal rejects unexplained or duplicate links. Historical actor records must exist; current actor/scope/person-binding liveness is not used to reapprove past work. Participant metadata is not historical person proof.

Only after preflight succeeds does it install strict/error collection validation, safe unique/query indexes and allocation high-water marks using majority writes under the deployment lease. Stronger validators and compatible alternative index names survive. Interruptions resume from actual partial DDL without rewriting completed migrations. This supersedes the draft-only storage status above. It still supplies no active stock authority, quantity effects, revision API/client/recovery or staff revision screen.

See [migration verification](../quality/stock-source-revision-migration-verification-2026-09-29.md) and [the deployment rehearsal](../runbooks/stock-source-revision-migration.md). Current pool revisions represent successive approved settings snapshots. Future activation/suspension/alias work must preserve that invariant or explicitly migrate a broader event model; it cannot advance the head without explanatory history. Operational acceptance and real source/valuation decisions remain separate gates.

## Settings revision recovery, API and client checkpoint — 29 September 2026

The revision service now has receipt-only recovery and bounded exact-location current/paged history reads. Recovery uses the original persisted request/audit/receipt facts and requires the receipt again inside a current-authority replay transaction. Missing or disappearing receipts cannot submit a replacement command, and later versions do not change the historical result. Recovery remains separate from history-read permission.

Nine protected routes and eleven new schemas expose current/base/proposed settings and independent decisions through exact numeric strings. Proposal If-Match binds the current pool version; review If-Match binds the proposal version. Original setup contracts remain semantically unchanged. Frontend parsers/client freeze reviewed settings, validate identity/location/version/status relationships and preserve uncertain outcomes without automatic retry.

See [the integration verification](../quality/stock-source-revision-api-verification-2026-09-29.md). This supersedes the earlier no-revision-API/client/recovery status. Staff revision comparison/history, journal integration and their real browser coverage are next. No active authority, quantity or financial writer is enabled; operational handoff/acceptance and the wider CAT02 work remain required.

## Settings revision staff workspace checkpoint — 29 September 2026

The revision service/API/client now has a staff workspace at /inventory/sources/revisions, scoped physical-source selection, current/base/proposed comparison, immutable history, explicit frozen command review and current-version warnings. Exact values and immutable physical ownership are retained. Approval of stale or active settings is visibly blocked; the server continues to enforce current authority, independent person review and fresh MFA.

Four revision operations have their own allowlisted reference-only journal adapter, separate from initial setup. Unknown commands have receipt lookup only, survive reload and cannot be closed or submitted again. Command-only actors can recover their own request without gaining history access. Login/MFA return paths and scoped navigation include the exact new page.

This supersedes the preceding missing-screen/journal/browser status. The final 45-case affected backend run includes three executed real browser rehearsals; all 587 frontend checks and the production build passed. See [staff workspace verification](../quality/stock-source-revision-workspace-verification-2026-09-29.md). Active-source handoff/reactivation, aliases, accepted authority/observations, lot authoring, operational migration and inventory/cost workflows remain open; configuration approval still causes no quantity or finance effect.

## Governed channel mapping service checkpoint — 29 September 2026

The stock source service now has a private alias configuration workflow. A mapping references one canonical physical pool and a different channel at the same organization/shop/warehouse. Proposals bind the approved pool settings revision and preceding alias revision. Independent approval appends an enabled/disabled mapping version; rejection and maker withdrawal are terminal without an applied version. The canonical pool settings revision and all physical quantities remain untouched.

Every command and read requires authority for both the requested channel and canonical owner, including final transaction grant/membership/topology/session/time fences and current-authority receipt replay. Approval/rejection reuses the existing independent-person and fresh-MFA requirements. Approval requires an inactive pool and exact current source/mapping bases, and writes a pool fence to conflict with competing configuration or activation work. A source settings change makes the old mapping stale until reapproved.

Current reads distinguish configuration binding from operational acceptance. They validate current/predecessor history, approval cardinality and canonical ownership; missing, disabled or obsolete bindings cannot be used as source authority. InventoryGraph still requires a canonical physical location and does not accept a balance stored under an alias channel.

Three strict draft collections store proposals, decisions and append-only approved mappings. Registered migration version 16 is unchanged. Reference-only recovery now reconstructs and checks the original stored request and requires its receipt inside the replay transaction under both current scopes. Full graph/receipt preflight and deployment, API/client/UI/browser integration, actual canonical inventory consumers and source acceptance remain required. See [service verification](../quality/stock-source-alias-verification-2026-09-29.md). This checkpoint is not source activation or CAT02 completion.
