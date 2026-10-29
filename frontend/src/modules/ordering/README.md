# Branch ordering

The first connected slice is a catalog subtotal preview inside the buyer's package-details dialog at `/catalog/browse`. The buyer explicitly chooses a currently permitted branch/division and a quantity. The ordering API checks `order.submit` independently of catalog membership, limits catalog entitlements to that buying context and reads approved price/package facts in the same Mongo transaction. Price, SKU, publication, grant, membership, organization and session fences serialize concurrent changes. A changed selection is rejected for renewed review.

The API accepts up to 100 explicit selections; this first screen calculates one package selection at a time. Amounts and base-unit totals are exact, minimum/increment rules are enforced, and missing price is different from an approved zero. The browser does not send monetary totals or silently choose a branch. Changing the quantity or branch clears the result; closing details aborts outstanding private reads.

This preview is a read, not a saved order or a held price. Repeating a read has no order, reservation, stock movement or financial effect and uses no command idempotency key.

The buyer can save an explicit selection as a private Mongo-backed draft, then reopen `/ordering/drafts` to edit quantities, add or reselect packages, and enter an optional purchase reference and delivery request. Lists are scoped to both creator and branch before pagination. Draft updates keep the branch fixed and require the current revision. A frozen review fetches fresh authoritative package prices before the save; the save transaction checks them again. Stale selections are never silently replaced. Delivery details are buyer requests, not approved organization addresses or recipient proof.

Draft head, immutable version, audit, original command receipt and private-ID-only outbox event commit together. A lost response leaves only actor/operation/key/time in local storage. The dedicated recovery adapter looks up that original receipt and cannot close or resubmit the command. Its result can recover an older save after later edits or catalog changes; current creator/branch authority remains mandatory. No draft content, contact information or price is stored in the recovery journal.

Migration 21 installs the draft collections; the writer remains closed until the whole migration history is current. The conditional checkout below recomputes and persists package, price, party, address and commercial-policy snapshots with source-separated suborders and stock reservations. Production activation remains M1 work. Pending business policies and production source acceptance stay gated.

## Keranjang (cart)

The product page has one "Tambah ke keranjang" control: the branch, a quantity stepper that starts at the lowest amount the package allows, and a subtotal. The keranjang is not a new record. It is the buyer's most recently changed saved draft for that branch, so every draft rule above still applies (creator and branch scope, revision fence, price and package check at save). A changed price or package is shown on `/cart` for the buyer to accept; it is never swapped in silently.

The buyer always names the branch for adding to the cart. The browser never chooses one there, not even the only branch allowed; the last explicit choice is remembered on that device as a branch reference only. Viewing is different (U-026): `/cart` opens the last explicit choice, or the only branch the buyer may order for, so a one-branch buyer sees their keranjang instead of an empty page behind a select; viewing records no choice (`cartViewBranch`). Cart saves use the same tracked draft journal and `ordering.draft.save` operation as the drafts page: a save whose answer was lost stays listed, even after a reload, until "Periksa hasil" looks up the original receipt, and no further cart change is sent until then. A cart changed in another tab is read again and the same change is applied once more at the newer revision. Emptying the cart discards the draft (restorable for 30 days).

Checkout asks for delivery on the page itself and can copy the branch address the organisation approved; recipient and phone stay the buyer's to write. "Pesan lagi" on an order puts its packages into the branch cart at today's package and price and names any that can no longer be ordered.

## Conditional checkout

`/ordering/checkout?draft=<id>` reloads an owned saved draft, obtains the current reviewed commercial terms and explicitly submits one order. The typed client checks exact selections and decimal totals. An unknown submission keeps only the original actor/operation/key/time reference and uses the dedicated lookup-only `ordering.order` recovery adapter. No order contents, addresses or prices are persisted in browser storage.

The real local browser fixture connects Next, HTTPS, Kestrel, Mongo orders and the Inventory writer using synthetic approved policy inputs. Migration 22 installs canonical order storage through the normal deployment chain; fixtures no longer install order schemas. Production admission still has no default adapter. See the single [progress ledger](../../../../docs/quality/production-progress.md) and [upgrade runbook](../../../../docs/runbooks/order-submission-migration.md).

## Branch review and store acceptance

`/ordering/reviews` discovers only assigned review stages. Branch spending approval requires fresh MFA and a different verified person from the original requester. Store acceptance is a separate scoped decision after spending approval or an explicit approved waiver. Each store sees only its part of the order and destination; branch-level totals and other stores' lines are excluded. Frozen snapshot/revision review, reason and explicit confirmation precede the write. The account page retains the step-up entry when the factor ages, while protected reads and commands continue enforcing fresh assurance.

Branch/store reviewers can explicitly reject their scoped demand, and an assigned buyer can cancel their own outstanding order. Each action has a separate operation and recovery reference. Decisions, reservation release through the Inventory writer, audit, receipt and private-reference outbox event commit together. Store rejection releases only that store's reservation; a later cancellation releases only the remaining reservations. Physical stock is unchanged. The original order/suborder snapshots and submission receipt remain immutable.

Current progress is derived from verified decision and stock-movement history. `ordering.decision` recovery is lookup-only and checks current access/person evidence. Actual partial dispatch, substitution consent, commercial obligation release and source-stop drain remain unfinished; the current quantity graph fails closed on unexplained dispatched changes or an inactive stock pool. Production admission and actual deployment remain gated. These local workflows do not issue invoices, execute refunds or establish production acceptance.

## Buyer order history

`/ordering/orders` uses the separate scoped `order.read` permission and shows only orders created by the current buyer. Lists apply creator and organization scope before descending Int64 cursor pagination (25 items). Detail and list responses revalidate current grants, membership, scope and session at the final transaction fence, including the extra row used to determine whether another page exists. Removing submission or catalog access does not remove an independently assigned order-read grant; suspended branches remain denied pending the explicit historical-access workflow.

The page presents immutable buyer, package, price and delivery snapshots together with current verified spending/store/cancellation state. Fresh detail reads replace the selected list status; original submitted totals are labelled as such and never presented as verified payment or refund. Account/session changes remove private content. No order data is saved in browser storage. This is buyer history, not the later cross-module delivery/invoice/payment/return timeline.
