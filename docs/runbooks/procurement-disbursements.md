# Procurement advance disbursements

The `/procurement/disbursements` workspace connects independently approved advance authorizations to private evidence of a recorded outgoing payment. A final recording has status `recorded_unverified`. It does not initiate a transfer, establish bank acceptance, post cash or a journal, receive stock, or complete procurement.

## Required operational inputs

Use a genuine approved purchase order, funding request and independent funding authorization; an eligible separately governed supplier bank revision; and an approved paying account with its actual confirmed bank receipt and posted cash mapping. An account name, typed reference or approved order cannot replace those sources. This first paying-account source requires the existing genuine cash anchor; opening balances and additional bank-source routes remain separate work.

The finite Finance recording policy identifies the exact organization/shop/warehouse/channel, paying-account version, whether partial recordings are permitted, reservation of unverified amounts, actual recording and beneficiary-verification procedures, operating owner and approval provenance. Migration 66 seeds no policy, grant, account, cash, payment or operating default. Live operation depends on the actual approved inputs recorded in the decision ledger.

## Authority and recording

- `procurement.disbursement.read` permits scoped masked history. It does not reveal private images or full bank-account digits.
- `procurement.disbursement.evidence.read` separately governs protected originals under the same business scope.
- `disbursement.record` governs the maker commands with current five-minute MFA. The recorder must be a different verified canonical person from the original funding approver; another account for that person does not satisfy independence.

New recording also requires the actual funding, supplier-bank and paying-bank source permissions. Role suggestions grant nothing. Current session, account, scope, people, source and policy checks remain effective during review, original-file access and commit.

Select the approved authorization and an explicit eligible policy/paying-account option. Enter a positive exact IDR amount within its remaining capacity and the actual transfer date/reference. The date cannot precede the funding approval's Jakarta business date or be in the future. A policy that forbids partial recordings requires the first full authorized amount. Review and save the immutable draft; a draft reserves nothing.

Reserve and upload an original image. Actual private storage, scanner/decoder verdicts and current file integrity are required before protected access and pinning. Review the retrieved original and frozen payment details, then explicitly record. The final transaction binds the exact original, draft, authorization, bank revision and paying-account source, consumes authorization capacity and records its immutable allocation, receipt, audit and outbox effects together. Concurrent partial recordings cannot exceed the authorization. Unverified recorded amounts remain consumed until a separately authorized disposition exists; they must not disappear merely because a record or supporting history is missing.

After an uncertain reply, use **Periksa hasil** with the original account and operation key. Recovery queries the existing result; it must not resend the financial command or manufacture another upload/record. Draft, reservation and final-record operations each retain their own key and owner. Scope changes, another account, stale MFA or damaged history do not authorize recovery.

## Migration and remaining acceptance

Migration 66 runs through the existing one-writer migration gate after retained funding, supplier/bank, private evidence, storage and counter preflight. Its additive private evidence target/purpose/pin association must preserve catalog, incoming payment and refund histories, original migration definitions and compatible stronger operator guards. Apply with verified backup/restore and quiescent financial writers. Include the exact workspace path in any Auth0 return-path override.

This workflow's implementation and validation state is maintained in the [progress ledger](../quality/production-progress.md). Local synthetic people, bank records, procedure inputs and images are not staging or production acceptance. The separate [independent debit settlement workspace](procurement-settlements.md) consumes the shared canonical bank-transaction claim and posts the approved supplier-advance journal exactly once after actual bank review. Releases/cancellations, supplier confirmation, evidence/OCR matching, inspection/receiving, unused-advance settlement, source reconciliation and accounting close remain required. No original image, OCR extraction, typed reference or recorded amount alone proves payment.
