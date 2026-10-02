# Supplier identity and source mappings

The supplier desk prepares and independently verifies supplier identity and source item mappings for later purchase orders. An approved supplier version is a selection source. It does not confirm an order, authorize payment or receive stock.

## Scope and actual inputs

Use an approved organization, shop and channel, with an exact fulfillment-shop grant. A warehouse grant does not confer supplier ownership. `supplier.change.propose` permits preparation and full proposal reads; `supplier.change.approve` permits independent observation and decisions. Both require current five-minute MFA. Ordinary `procurement.read` exposes only named approved choices and active item mappings in its permitted scope; it does not expose contact details or verification references.

Enter actual legal and display names, the supplier subject reference, address and contact details. A registration identifier is optional; if supplied, its namespace and value must both be present. External account references and item codes retain their case and leading zeros. Do not substitute a supplier code for a SKU, package definition, bank account or stock pool.

Each mapping chooses a genuine catalog SKU and package version at an explicit warehouse and supplies a finite validity interval. The source namespace is the owner, source system, external account and item code. Overlapping mappings within that namespace are rejected, including mappings to another warehouse or supplier. A later approved supplier version replaces the current choices while preserving previous versions for historical orders. Future mappings become selectable at their own start time; expired mappings are unavailable.

Current choices also require the latest version's exact verification policy to remain active, unchanged and unexpired. Withdrawing or changing that policy removes the supplier from current choices without falling back to an older version. Historical details and original operation recovery remain available under current authority. Observation freshness is checked at approval; it is not a recurring timer that silently invalidates an otherwise approved supplier every few minutes.

## Independent verification

An approved operational verification policy must already identify this supplier and its trusted channel. It retains an accountable owner, approval reference, approval/expiry times and maximum observation age. Neither the proposed phone number nor proposed email address creates a trusted channel. The application does not seed approved supplier policies.

The reviewer independently enters observed supplier details from the policy's actual trusted source and records the source reference. Observation and approval require a different verified canonical person from the proposer. The observer may also make the decision. Approval requires an exact identity/contact match, an explicitly selected current observation, a current approved policy, and unchanged supplier, catalog and authority facts. Rejection may retain a stale proposal's original history under current authority.

This implementation supports reference-only observations only when the actual approved policy explicitly allows them. It does not upload or pin a supplier verification file. A policy requiring private-file evidence remains unavailable until that real evidence owner is connected. No bank-verification policy or payment permission substitutes for supplier verification.

## Review and recovery

Review the frozen supplier details, named scope, catalog packages, source codes, validity window and policy before submitting. Reasons are mandatory for proposal, observation and decision. A lost response is resolved by lookup with the original account and original operation key. Do not resend a command to resolve an uncertain outcome. Changing the signed-in account cannot recover the prior account's pending command.

## Release gates

Migration 62 installs empty supplier policy, proposal, observation, decision, version, head and source-namespace stores. Real approved policy inputs, supplier quotations/order references, commercial terms, procurement evidence, funding, inspection, receipt, valuation and settlement remain separate requirements. No financial transfer, external supplier message or live data cutover is authorized by a supplier approval. Local synthetic policy/person fixtures and a source push do not establish production acceptance.
