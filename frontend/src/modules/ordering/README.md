# Branch ordering

The first connected slice is a catalog subtotal preview inside the buyer's package-details dialog at `/catalog/browse`. The buyer explicitly chooses a currently permitted branch/division and a quantity. The ordering API checks `order.submit` independently of catalog membership, limits catalog entitlements to that buying context and reads approved price/package facts in the same Mongo transaction. Price, SKU, publication, grant, membership, organization and session fences serialize concurrent changes. A changed selection is rejected for renewed review.

The API accepts up to 100 explicit selections; this first screen calculates one package selection at a time. Amounts and base-unit totals are exact, minimum/increment rules are enforced, and missing price is different from an approved zero. The browser does not send monetary totals or silently choose a branch. Changing the quantity or branch clears the result; closing details aborts outstanding private reads.

This preview is a read, not a saved order or a held price. Repeating a read has no order, reservation, stock movement or financial effect and uses no command idempotency key.

The buyer can save an explicit selection as a private Mongo-backed draft, then reopen `/ordering/drafts` to edit quantities, add or reselect packages, and enter an optional purchase reference and delivery request. Lists are scoped to both creator and branch before pagination. Draft updates keep the branch fixed and require the current revision. A frozen review fetches fresh authoritative package prices before the save; the save transaction checks them again. Stale selections are never silently replaced. Delivery details are buyer requests, not approved organization addresses or recipient proof.

Draft head, immutable version, audit, original command receipt and private-ID-only outbox event commit together. A lost response leaves only actor/operation/key/time in local storage. The dedicated recovery adapter looks up that original receipt and cannot close or resubmit the command. Its result can recover an older save after later edits or catalog changes; current creator/branch authority remains mandatory. No draft content, contact information or price is stored in the recovery journal.

Migration 21 installs the draft collections; the writer remains closed until the whole migration history is current. The conditional checkout below recomputes and persists package, price, party, address and commercial-policy snapshots with source-separated suborders and stock reservations. Spending review, store acceptance and production activation remain M1 work. Pending business policies and production source acceptance stay gated.

## Conditional checkout

`/ordering/checkout?draft=<id>` reloads an owned saved draft, obtains the current reviewed commercial terms and explicitly submits one order. The typed client checks exact selections and decimal totals. An unknown submission keeps only the original actor/operation/key/time reference and uses the dedicated lookup-only `ordering.order` recovery adapter. No order contents, addresses or prices are persisted in browser storage.

The real local browser fixture connects Next, HTTPS, Kestrel, Mongo orders and the Inventory writer using synthetic approved policy inputs. Production admission has no default adapter and order storage has not yet been installed by a forward migration. Spending/store approvals, cancellation and fulfillment remain separate unfinished workflows. See the single [progress ledger](../../../../docs/quality/production-progress.md).
