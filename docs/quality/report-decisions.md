# Report business decisions

This ledger records actual decisions and pending inputs; source documents and proposed defaults do not grant operational approval. It extends D01–D08 in the [report acceptance register](../requirements/unit-toko-report-2026-09-28.md).

## Confirmed user choices

- First launch: BNI branches and approved business accounts.
- The November dates are references; production readiness governs launch.
- No existing identity provider; prepare the planned Auth0 integration. Actual tenant configuration, costs, delivery and owner recovery acceptance remain separate gates.

## D02 catalog audience and approval ownership

Asked on 28 September 2026; answers pending:

| Decision | Choices presented | Effect |
| --- | --- | --- |
| Shared catalog and prices | Same products/prices; vary by branch/account; keep pending | Determines reviewed assortment and pricing-audience rules for CAT01 |
| Catalog-access approver | Kepala Toko; Kepala Toko and Finance independently; keep pending | Determines who can authorize issuance; no role implicitly gains that power from existing account/catalog administration |

Continue local authorization, persistence and adversarial tests using explicitly synthetic fixtures. Do not issue live entitlements, select a pricing policy or mark these business gates approved without the user's answer and the required operational details.

Other D02 decisions (buyer spending approvers, historical readers, any specifically permitted suspended-branch settlement, person/finance conflicts and review/delegation cadence) remain open under the source register. Historical read never implies settlement permission. This ledger does not declare the remaining D01/D03–D08 gates satisfied.
