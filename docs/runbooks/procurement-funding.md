# Internal procurement advance authorization

The funding desk at `/procurement/funding` requests and independently authorizes an advance against an already approved purchase order. Authorization reserves part of the PO's advance ceiling. It does not establish available cash, approve a supplier bank account, send funds, record a supplier receipt or post an accounting journal.

## Required inputs

- A genuine approved PO at the selected organization, shop, warehouse and channel, retaining its approved request allocation and original supplier/package/commercial facts.
- An explicit positive advance requirement in that PO. Credit and payment-on-receipt orders with zero advance are not eligible; their later balance needs a separate genuine payment obligation.
- An active, approved Finance operating policy for the exact location and route, with revision, responsible owner, procedure and approval provenance, and finite validity. Installment authorization must be explicitly permitted. Approved but undisbursed amounts remain reserved. If the policy requires an additional budget or approval-limit source that is not connected, approval remains unavailable.
- An exact positive requested amount, business date, needed-by date and reason. Enter values from the actual approved business request, not a percentage or amount suggested by the application.

Migration 64 seeds no policy, grant or operational approval. Typed references and synthetic test policies do not establish real Finance approval. Current D01/D02/D03 decisions remain open in [the business decision ledger](../quality/report-decisions.md).

## Operator workflow

1. Select the named location, approved PO and applicable Finance policy. Review original supplier, package/specification, price, tax, charges and payment terms.
2. Enter the exact requested amount and dates. Review the original advance ceiling, previously authorized amount and remaining authorization capacity before submitting.
3. A different verified canonical person reviews a fresh decision preview and approves the exact request or rejects it with a reason. A reviewer cannot silently reduce an amount.
4. Recover a lost reply using the original account and stored operation key. Do not send another command to determine whether the first committed.

`funding.read` grants narrowly scoped financial review. `funding.request` and `funding.approve` require five-minute MFA and the actual shop/warehouse scope. Procurement read alone grants no funding detail. Existing role snapshots remain unchanged; funding read must be explicitly granted through normal independent access review.

Requests alone reserve nothing. Approval atomically records the decision and immutable authorization allocation with its fenced capacity head and normal audit/receipt/outbox effects. The cumulative amount cannot exceed the original approved PO advance requirement. When installments are prohibited, authorization must cover the entire original requirement with none previously authorized. Missing or altered owner history cannot reopen capacity. Rejection reserves nothing, and this desk releases no prior authorization.

The PO's approved commercial history does not expire merely because its quotation later expires or a catalog entry changes. Current organization, person, session, grant and funding-policy checks still apply to new authorization. A withdrawn or expired policy cannot erase earlier authorized amounts. New supplier payment instructions require their own independently verified beneficiary and disbursement workflow.

## Remaining operational acceptance

Supplier confirmation, independently verified bank beneficiaries, paying-account and bank-source admission, private original payment evidence, actual bank debit verification, accounting recognition, transport/employee expenses, unused advances and approved cancellation/release remain separate work. No manual or internal authorization grants permission to transfer money or send an external message.

The one-writer migration and restore gate apply to real data. Local source integration, synthetic policy fixtures and browser checks do not constitute staging or production acceptance. Current evidence and remaining work are recorded in [the production progress ledger](../quality/production-progress.md).
