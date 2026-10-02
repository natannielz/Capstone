# Supplier bank verification

The `/procurement/supplier-banks` workspace records one current bank beneficiary lineage per approved supplier. Its bank revision is independent of the supplier identity/source revision. Changing a bank account preserves existing purchase orders and funding authorizations. This workflow does not initiate transfers, confirm payments or post cash, stock or journals.

## Required operational inputs

An approved supplier identity and exact organization/shop/channel owner must already exist. A separate approved bank-verification policy binds that supplier revision, subject reference, bank code, IDR currency, trusted verification method/channel, named operating owner, authorization/approval references, finite validity and observation age. Identity/contact policy cannot authorize a bank account. No operational policy or account is seeded by migration 65.

Reference-only verification is available only when that actual policy explicitly permits it. A policy requiring private-file evidence remains unavailable until the genuine private evidence owner is connected; a typed reference is not a private-file pin. Production activation remains subject to the actual Finance verification procedure and approved operational inputs in the decision ledger.

## Roles and procedure

- `supplier.bank.read`: masked supplier-bank choices, current state and history.
- `supplier.bank.details.read`: full proposal and observed details under the same shop scope; full reads are audited without copying account details into audit payloads.
- `supplier.bank.propose`: propose a new beneficiary or a correction against the exact current bank revision.
- `supplier.bank.approve`: independently observe, approve or reject a proposal. The observer and approver may be one person; each must be a verified canonical person independent of the proposer.

All four permissions are shop-scoped. Mutations require current five-minute MFA and the relevant read/detail grants. Template suggestions are not grants. Ordinary supplier, procurement, incoming receiving-account and customer-refund permissions confer no beneficiary authority.

Enter the exact bank code, account number, holder name and currency. Preserve leading zeros; do not repair digits, normalize account ownership or use fuzzy matching. The reviewer independently enters blank observation fields through the approved trusted channel, then explicitly attests the source. Approval requires an exact match with the frozen proposal and current source, policy, observation, person, session and permission evidence. Concurrent corrections must retain one current bank revision. Rejected and superseded records remain in history.

After a lost response, use the pending operation's **Periksa hasil** action under the original account. It looks up the original operation key; do not create a second proposal, observation or decision to resolve an unknown outcome. Switching accounts does not authorize recovery of another person's command.

## Deployment and remaining acceptance

Apply migration 65 through the existing one-writer migration gate after retained-source/storage/counter checks and verified backup. It installs empty bank policies, heads, proposals, observations, decisions and versions, preserving stronger existing guards and supplier/order/funding history. Include the exact workspace path in any Auth0 return-path override.

Local synthetic verification establishes no real bank ownership or provider acceptance. Actual policy, independent operators, trusted bank observation, required private evidence, disbursement and independent bank-debit settlement remain distinct operational/release obligations. Policy withdrawal or expiry must block new beneficiary use without deleting historical approvals. A current beneficiary is routing evidence, not proof of payment.
