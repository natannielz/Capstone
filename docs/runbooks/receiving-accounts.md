# Receiving accounts

The Finance workspace is `/billing/receiving-accounts`. It records proposed receiving details, a separate observation through an approved bank channel, and an independent decision. This is manual bank-channel verification. It is not a bank connection, a payment confirmation or a transfer facility.

## Activation inputs

Migration 37 creates empty collections. It does not install grants, bank accounts or verification policies. Before real use, Finance must supply an approved canonical bank code, fulfillment organization/shop/channel/optional warehouse, bank verification method, independently established trusted channel, responsible owner, approval reference, validity interval and maximum observation age. These are controlled operational configuration, not information supplied by an account proposer. The current implementation has no public policy-creation endpoint. Provisioning, owner verification and rollback must be reviewed in the actual deployment before activation; do not copy synthetic test policies into production.

The staff person registry must also have its approved real source, adjudicators and validity rules. Auth0 accounts, matching names and email addresses do not establish that two accounts belong to independent people. Both operational inputs remain open in the [decision ledger](../quality/report-decisions.md).

## Explicit permissions

| Permission | Use |
| --- | --- |
| `receiving.account.read` | Scoped masked proposal list |
| `receiving.account.details.read` | Full account details and approved channel references, together with read access |
| `receiving.account.propose` | Propose a new account/version or withdraw one's own pending proposal; also requires full read |
| `receiving.account.verify` | Record the separate bank observation; also requires full read, fresh MFA and current person independence from the proposer |
| `receiving.account.approve` | Approve/reject; also requires full read, fresh MFA and current person independence |

These are optional reviewed grant suggestions, not new role templates or automatic additions to existing Finance packs. Technical administration alone grants none of these powers. For approval, both the observer and the deciding actor must currently be independent from the original proposer. The observer may also decide if that person is independently authorized. Approval checks current person evidence again even when the earlier observation was valid.

## Staff workflow

1. Select an approved verification policy and enter exact bank/account/holder/currency details. Preserve leading zeros. Preview and submit the proposal with a reason. For a change, open an existing proposal and use **Ajukan perubahan rekening ini**; the new proposal is bound to the account's current version and unchanged fulfillment scope.
2. Another authorized person opens the proposal and independently uses the policy's established bank channel. The observation fields start empty. Record the observed values, bank-source reference, explanation and explicit attestation. A reference alone is not external evidence that the bank was contacted; actual operator/source acceptance is still required.
3. Review proposed and observed values. Approval requires exact equality, the unchanged current policy, a pending proposal on the current account version, a sufficiently recent observation and current authority/person evidence. No fuzzy name matching or number repair is used. A mismatch is retained; reject/withdraw and create a fresh proposal for correction. Observation and decision records cannot be overwritten.
4. The approved version, previous-version/hash links, immutable participants, audit, operation receipt and metadata-only outbox effect commit together. Original versions remain retained. A stale or expired proposal can still be rejected/withdrawn with the applicable authority; it cannot activate an account.

Ordinary lists mask account numbers. Full reads are scoped, audited and non-cacheable. The persistent browser operation journal retains only actor, operation, request key and time. After an interrupted response, use **Periksa hasil**. A missing result remains unresolved; do not clear it by resending a changed request or inventing a successful receipt.

## Downstream boundary and release

Future financial consumers must bind the immutable approved version used for their decision. They must not silently retarget historical transactions when a newer account is approved. The account's physical bank identity is separate from lineage/version IDs so canonical bank-receipt deduplication can prevent the same transfer creating cash twice across account changes. Actual bank import profiles and corroboration, payment verification, allocation, accounting effects and after-sales remain separate owning workflows; this checkpoint does not implement them.

Local tests use synthetic channels and person policies against a real isolated Mongo replica set. They do not establish bank ownership, live Auth0 acceptance or approval to activate a real receiving account. Actual provider/host/storage connections, policy provenance, migration/restore rehearsal, operator acceptance and pilot remain M6 release gates. See the [single progress ledger](../quality/production-progress.md).
