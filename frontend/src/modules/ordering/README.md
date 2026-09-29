# Branch ordering

The first connected slice is a catalog subtotal preview inside the buyer's package-details dialog at `/catalog/browse`. The buyer explicitly chooses a currently permitted branch/division and a quantity. The ordering API checks `order.submit` independently of catalog membership, limits catalog entitlements to that buying context and reads approved price/package facts in the same Mongo transaction. Price, SKU, publication, grant, membership, organization and session fences serialize concurrent changes. A changed selection is rejected for renewed review.

The API accepts up to 100 explicit selections; this first screen calculates one package selection at a time. Amounts and base-unit totals are exact, minimum/increment rules are enforced, and missing price is different from an approved zero. The browser does not send monetary totals or silently choose a branch. Changing the quantity or branch clears the result; closing details aborts outstanding private reads.

This preview is a read, not a saved order or a held price. Repeating a read has no order, reservation, stock movement or financial effect and uses no command idempotency key.

The buyer can save an explicit selection as a private Mongo-backed draft, then reopen `/ordering/drafts` to edit quantities, add or reselect packages, and enter an optional purchase reference and delivery request. Lists are scoped to both creator and branch before pagination. Draft updates keep the branch fixed and require the current revision. A frozen review fetches fresh authoritative package prices before the save; the save transaction checks them again. Stale selections are never silently replaced. Delivery details are buyer requests, not approved organization addresses or recipient proof.

Draft head, immutable version, audit, original command receipt and private-ID-only outbox event commit together. A lost response leaves only actor/operation/key/time in local storage. The dedicated recovery adapter looks up that original receipt and cannot close or resubmit the command. Its result can recover an older save after later edits or catalog changes; current creator/branch authority remains mandatory. No draft content, contact information or price is stored in the recovery journal.

Migration 21 installs the draft collections; the writer remains closed until the whole migration history is current. The conditional checkout below recomputes and persists package, price, party, address and commercial-policy snapshots with source-separated suborders and stock reservations. Production activation remains M1 work. Pending business policies and production source acceptance stay gated.

## Conditional checkout

`/ordering/checkout?draft=<id>` reloads an owned saved draft, obtains the current reviewed commercial terms and explicitly submits one order. The typed client checks exact selections and decimal totals. An unknown submission keeps only the original actor/operation/key/time reference and uses the dedicated lookup-only `ordering.order` recovery adapter. No order contents, addresses or prices are persisted in browser storage.

The real local browser fixture connects Next, HTTPS, Kestrel, Mongo orders and the Inventory writer using synthetic approved policy inputs. Production admission has no default adapter and order storage has not yet been installed by a forward migration. See the single [progress ledger](../../../../docs/quality/production-progress.md).

## Branch review and store acceptance

`/ordering/reviews` discovers only assigned review stages. Branch spending approval requires fresh MFA and a different verified person from the original requester. Store acceptance is a separate scoped decision after spending approval or an explicit approved waiver. Each store sees only its part of the order and destination; branch-level totals and other stores' lines are excluded. Frozen snapshot/revision review, reason and explicit confirmation precede the write. The account page retains the step-up entry when the factor ages, while protected reads and commands continue enforcing fresh assurance.

Branch/store reviewers can explicitly reject their scoped demand, and an assigned buyer can cancel their own outstanding order. Each action has a separate operation and recovery reference. Decisions, reservation release through the Inventory writer, audit, receipt and private-reference outbox event commit together. Store rejection releases only that store's reservation; a later cancellation releases only the remaining reservations. Physical stock is unchanged. The original order/suborder snapshots and submission receipt remain immutable.

Current progress is derived from verified decision and stock-movement history. `ordering.decision` recovery is lookup-only and checks current access/person evidence. Actual partial dispatch, substitution consent, commercial obligation release and source-stop drain remain unfinished; the current quantity graph fails closed on unexplained dispatched changes or an inactive stock pool. Production admission and the forward storage migration remain gated. These local workflows do not issue invoices, execute refunds or establish production acceptance.
