# Procurement requests

This workspace records an internal need or incoming notice using the real catalog and exact fulfillment location. It does not send an order to OMI or a supplier, record payment, or receive stock. The original BPP procurement outcomes and report F06 remain required through settlement.

## Operator inputs

Choose the approved organization, shop, warehouse and channel by name. The selected location must be covered by the current shop or warehouse grant. Enter an explicit business date and reason; select real SKU/package definitions and integer package quantities. The review keeps each SKU revision, immutable package version, package type, base unit and contents per package. Optional specification text describes the request; it never changes package identity or quantity conversion.

| Route | Permitted request | Later facts that remain separate |
| --- | --- | --- |
| OMI regular | Stock need, or an incoming notice with an actual external reference | OMI determines its order and schedule. Staff later count containers, inspect contents against documents and record the actual receipt. |
| DDO | Additional purchase request | OMI submission/reference, telephone confirmation, partial fulfillment and any linked alternate sourcing require their own actual evidence. |
| Smart | Purchase request, with explicit custom specification when applicable | Supplier selection, agreed price/quantity, invoice, advance/full payment, inspection and receipt remain separate. |
| Pasar Kering | Alternate or urgent purchase request | Supplier review, cooperative funding, actual purchase, transport reimbursement and receipt remain distinct facts. |

Channel names do not automatically determine the route. Missing supplier and commercial facts remain unresolved; they are never replaced with zero cost or a synthetic supplier. No external reference is invented. Each request contains one to 100 distinct SKU lines, each with one to 1,000,000 packages; optional specifications are bounded to 1,000 characters and an external reference to 128.

## Review and recovery

`procurement.read`, `procurement.prepare` and `procurement.approve` retain exact fulfillment shop/warehouse scope. Submit and approve/reject require current five-minute MFA. A different verified canonical person must decide the stored requester's request; switching accounts for the same person does not establish independence. Approval rechecks the current catalog/package and approved location source. A stale request may be rejected under current authority, but cannot be approved using stale facts.

Review the frozen location, route, date, explanation, package quantities and contents before submitting. Internal approval means that the need or notice was reviewed; it does not mean that a supplier accepted an order, funds were sent or stock arrived. The immutable request and decision retain their audit and original actor's operation receipt.

When a response is lost, retain the local pending reference and use lookup with the original signed-in account and original key. Do not submit again to resolve uncertainty. Changing the active account cannot recover another account's pending operation. History and recovery are private, scoped reads.

## Production gates

Migration 61 adds the request and decision owner stores without seeding supplier, funding, receipt, stock or financial facts. Real supplier/source mappings and trusted-channel changes, supplier order/confirmation, procurement evidence, price/tax/cost review, funding and unused advances, inspection/damage/claims, controlled receiving/valuation and source reconciliation remain required before the full procurement routes can be accepted. The seven-domain stock completeness gate and one-writer cutover remain in force. Local fixtures and source publication do not establish provider, business-policy or production acceptance.
