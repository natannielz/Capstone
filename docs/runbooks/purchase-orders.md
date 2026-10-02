# Purchase orders from approved requests

The purchase-order desk at `/procurement/purchase-orders` consumes approved procurement requests and independently verified supplier/source mappings. It records internal approval and commits the selected request quantities. It does not send an order, confirm supplier acceptance, transfer funds, receive stock or post a journal.

## Required source inputs

- A genuine approved request at the selected organization, shop, warehouse and channel, with its original SKU/package definitions and specifications. The route comes from that request.
- A currently approved supplier version and exact source-system/account/item mapping for each selected package at that location. Policy withdrawal, expiry, supplier version changes and source changes require a fresh review; an older supplier version is not a fallback.
- An actual quotation, external order or invoice reference, issue date and documented validity limit. Do not invent a deadline when the actual commercial source lacks one; resolve that source requirement with the operating owner.
- Explicit package quantities, net price **per package**, quoted tax **total for the line**, and supplier charge **total for the line**. Enter a genuine zero explicitly when applicable. The system does not infer purchasing costs from catalog selling prices or apply an assumed tax rate.
- Actual payment terms and an explanation: full payment before dispatch, deposit, payment on receipt or credit. Enter the required advance explicitly. A due-date trigger requires the actual due date; other triggers do not take a due date. These are agreed commercial terms, not evidence that funds were approved or paid.
- A reason for proposal and for the independent decision.

Money is bounded exact decimal IDR, represented as strings over the API. Net line value is packages × net price per package; line total adds the line's tax and supplier charges. Document totals are checked sums. Package quantities stay in the original selected unit; derived base units do not replace the package identity. IDs and versions remain lossless decimal strings.

## Operator flow and authority

1. Select the named location and an approved request with remaining quantities. Select a verified supplier and an exact mapping for each package.
2. Enter the actual commercial reference, quantities, amounts and payment terms. Review the frozen source and totals, then submit the proposal.
3. A different verified canonical person opens the proposal and requests a fresh approval preview. The preview shows current request capacity alongside the immutable proposed snapshot. Approve only the reviewed facts, or record an explicit rejection.
4. After a lost reply, use the original account and stored operation key to look up the committed result. Do not create a fresh command to discover whether the first succeeded.

Existing `procurement.read`, `procurement.prepare` and `procurement.approve` authority is scoped to the actual shop/warehouse. Mutations require recent five-minute MFA. Approval/rejection cannot be performed by another account linked to the proposer's canonical person. Session, grant, person, source and supplier trust are rechecked before commit. No permission or role is granted by migration.

Approval creates an immutable purchase order, decision, request allocations and ordinary audit/receipt/outbox effects in one Mongo transaction. Request capacities are reconstructed from genuine approved orders and allocation history; missing owner history is not accepted as zero. Concurrent approvals cannot consume more than the approved request quantity. Rejection consumes no quantity. No approved quantity is released or rerouted by this desk; a later explicit release/cancellation owner must preserve the linked demand and history.

## Route boundaries

| Request route | What this desk records | Further required workflow |
| --- | --- | --- |
| OMI regular | Actual externally referenced commercial order/incoming notice, preserving the request and supplier mapping | External schedule/acceptance provenance, container/content inspection and receiving |
| DDO | Internal approval of the additional order from the approved request | Actual transmission/confirmation, partial fulfillment and independently approved linked remainder release before alternate sourcing |
| Smart | Actual negotiated merchandise specification, commercial amounts and advance/full-payment terms | Funding approval/evidence, supplier fulfillment, quality/quantity inspection and settlement |
| Pasar Kering | Actual outside-supplier offer and purchase terms | Cooperative funding/advance, actual purchase and inspection, separately supported transport reimbursement and unused-advance settlement |

No real external messages or payments are authorized by a manual, proposal or internal approval alone. Supplier invoice OCR/matching, private commercial evidence when required, funding, receiving, shortage/quality claims, cancellation/release, transport reimbursement, bank verification and accounting settlement remain separate owners. Production must satisfy their real policies and source evidence before the corresponding downstream action becomes available.

## Migration and acceptance

Migration 63 installs empty purchase-order proposal/decision/order and request commitment/allocation stores after preflight. Existing source storage, retained data shape, catalog graph, profile provenance, counters and orphan operation effects are checked before DDL. Stronger existing validators remain in place. The one-writer migration procedure and backup/restore gate still apply; this runbook is not authority to migrate real data.

Local integration evidence and release status belong in [the production progress ledger](../quality/production-progress.md). Synthetic local people, policies and commercial references establish local behavior only. Production acceptance requires actual operator inputs, provider deployment, restore/migration rehearsal, load validation and the branch pilot.
