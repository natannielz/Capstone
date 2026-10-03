# Independent procurement debit settlement

The `/procurement/settlements` workspace verifies a recorded supplier advance against an actual imported bank debit. Verification records `bank_debit_verified`, claims the canonical bank transaction and posts one supplier-advance debit/cash credit. The original disbursement remains immutable `recorded_unverified`; its separate settlement history supplies the confirmed outcome. This action never initiates a transfer.

## Operational inputs and access

Use a genuine independently approved funding authorization, its recorded disbursement and pinned private original, the governed beneficiary revision, the approved paying-account version and an actual imported statement. The statement's parser profile and sources must remain eligible. The selected debit must have a stable canonical transaction identity, the exact recorded amount and a booking date from the recorded transfer date through the current Jakarta business date. A debit already claimed by any financial owner is unavailable. Separate partial disbursement records require separate eligible debits.

Finance must supply a finite approved settlement policy for the exact organization/shop/warehouse/channel and paying-account version. It identifies the actual accounting book/chart, distinct active supplier-advance and cash mappings, independent debit-review and beneficiary-verification procedures, trusted bank channel, operating owner and approval provenance. The current observation route requires explicit approval of reference-only review. Migration 67 creates no live policy, grant, bank fact, opening balance or cash default. Synthetic test policies are not operational approval.

- `procurement.settlement.read` and `procurement.disbursement.read` permit scoped masked history.
- `disbursement.verify` permits confirmation with current five-minute MFA. The verifier must be a different verified canonical person from both the original recorder and bank-statement importer, even if another account carries every permission.
- Bank source review separately requires supplier-bank, receipt, statement and receiving-account read/detail permissions. Private original access separately requires `procurement.disbursement.evidence.read`.

Current organization scope, identity, person provenance, session, grants, policy and source eligibility are checked during review and again within the transaction. A role template or screen link grants no authority. Include `/procurement/settlements` in an explicitly configured Auth0 return-path allowlist before enabling the workspace.

## Review and recovery

Select the recorded disbursement, approved policy and an actual statement import/debit. Read the protected original retrieved by its exact evidence identity, version and digest. Enter the beneficiary details observed through the approved independent channel and the actual beneficiary and transfer verification references; these observations start blank. An image, OCR text, typed reference or amount alone does not establish bank acceptance.

Review the frozen amount, dates, source, policy and accounting mappings, then explicitly attest and verify. The settlement, balanced journal, canonical bank claim, operation receipt, audit and outbox record commit together. The existing funding-capacity consumption and original private pin are retained; this step does not release funding, receive stock or clear supplier obligations.

If a committed reply is lost, use **Periksa hasil** with the original account and operation key. The recovery query reads the existing result without resending a financial command. Another actor/key, changed command body, missing claim/journal/history, stale authority or incompatible sources must not manufacture a replacement result. Escalate damaged historical evidence through the approved operational process.

## Migration and acceptance

Migration 67 uses the existing one-writer migration gate. Preflight validates retained disbursement/private-original and prior credit/refund bank-claim histories before DDL. The original version-1 claim schema remains intact; version 2 adds explicit procurement-settlement ownership while retaining the global unique canonical transaction identity. Exact partial indexes preserve version-1 owner uniqueness and add version-2 owner-kind uniqueness. Compatible stronger operator validators remain required. An unknown schema/index shape, orphan owner, missing retained proof or nonempty incompatible suffix blocks migration.

Apply only with approved operational inputs, quiescent financial writers and a verified backup/restore procedure. No real data cutover or provider acceptance follows from source publication. The [progress ledger](../quality/production-progress.md) records actual validation separately from implementation.

Supplier confirmation, receiving/inspection, unused-advance disposition, supplier claims, commercial evidence/OCR matching, stock/source reconciliation and accounting close remain part of the production goal. Production load/latency, provider connections, operator review and branch pilot acceptance remain required.
