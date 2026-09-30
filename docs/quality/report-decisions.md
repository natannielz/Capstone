# Report business decisions

This ledger records actual decisions and pending inputs; source documents and proposed defaults do not grant operational approval. It extends D01–D08 in the [report acceptance register](../requirements/unit-toko-report-2026-09-28.md).

## Confirmed user choices

- First launch: BNI branches and approved business accounts.
- The November dates are references; production readiness governs launch.
- No existing identity provider; prepare the planned Auth0 integration. Actual tenant configuration, costs, delivery and owner recovery acceptance remain separate gates.

## D01 trusted staff identity source

Asked on 28 September 2026; answer pending: which official source can establish that different finance accounts belong to different people? Options presented were keeping the decision pending while preparing controls, an independently reviewed HR/staff roster, or an existing HR system/employee directory.

No source or named verification/equivalence adjudicator has been approved. Continue local ACC03 contracts and synthetic fixtures. Unknown or disputed person identity must not authorize the new production financial approval path; provider subject or matching email/name is not independent-person proof. Real activation also requires the source's actual evidence/validity rules and independent reviewers.

## D02 catalog audience and approval ownership

Asked on 28 September 2026; answers pending:

| Decision | Choices presented | Effect |
| --- | --- | --- |
| Shared catalog and prices | Same products/prices; vary by branch/account; keep pending | Determines reviewed assortment and pricing-audience rules for CAT01 |
| Catalog-access approver | Kepala Toko; Kepala Toko and Finance independently; keep pending | Determines who can authorize issuance; no role implicitly gains that power from existing account/catalog administration |

Continue local authorization, persistence and adversarial tests using explicitly synthetic fixtures. Do not issue live entitlements, select a pricing policy or mark these business gates approved without the user's answer and the required operational details.

Other D02 decisions (buyer spending approvers, historical readers, any specifically permitted suspended-branch settlement, person/finance conflicts and review/delegation cadence) remain open under the source register. Historical read never implies settlement permission. This ledger does not declare the remaining D01/D03–D08 gates satisfied.

## D02/D03 branch spending rule

Asked on 29 September 2026 while connecting branch drafts: which real-order spending rule is approved (every order independently approved, approval above a specified amount, or another rule), and which budget/credit limits are already approved? Answer pending. This is a request for business inputs, not approval of a proposed default. Buyer spending approval remains distinct from store acceptance. Continue implementation and isolated synthetic fixtures; no live rule, threshold, limit, tax treatment or credit permission has been inferred.

## D03 invoice grouping and payment terms

Asked on 30 September 2026 while connecting receipt-based invoicing: which Finance-approved grouping applies (per receipt, branch/period/channel, or organization/period/channel), and what event starts the due-date term, for how many calendar or working days? Answer pending. The UI and local rehearsal use explicitly synthetic terms; the meeting's concern about late invoices does not approve a 30-day contract. Live issuance remains closed until actual owner-backed approvals are available. Tax allocation/process, numbering continuity/format, seller/document template, holiday calendar and accounting mappings remain additional operational inputs; this question does not declare those gates satisfied.

## D02/D03 receiving-account verification channel

Asked on 30 September 2026 while connecting receiving-account review: which independently obtained source is approved to verify the account owner and bank details, and who is the responsible Finance owner? The choices were keeping real activation blocked, bank-issued confirmation checked through an established bank contact, or an authenticated bank portal/statement checked by authorized Finance staff. Answer pending; no account numbers or credentials were requested in chat.

Continue the local workflow with explicitly synthetic approved-channel/person fixtures. Real activation requires the approved source, independently established channel, canonical bank profile/code, fulfillment scope, named owner/adjudicators, approval provenance, validity and freshness rules. A proposer-supplied contact, typed reference, provider email or observed match alone cannot supply these operational approvals. The [receiving-account runbook](../runbooks/receiving-accounts.md) records the concrete workflow and its remaining release gates.
