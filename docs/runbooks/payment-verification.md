# Evidence-bound payment verification

Finance can connect a submitted customer declaration to an independently confirmed bank receipt in `/billing/payment-reviews`. The decision binds the exact original, immutable OCR result and human corrections, confirmed bank transaction and an approved payer-verification procedure. It establishes a payer claim on existing cash. It neither posts cash again nor reduces an invoice balance. Allocation is a separate, still-required workflow.

## Authority and operational inputs

Migration 40 adds empty strict `paymentVerificationPolicies`, `paymentVerifications` and `paymentReceiptParties` collections. Applied migrations 1–39 stay unchanged. It creates no real policy, person proof, grant, customer balance or fixture. Preflight rejects unexpected business rows, related orphan effects, unsafe indexes/retention/options and invalid allocator state before target changes. Compatible stronger validators are preserved, and interrupted empty installation can resume.

Use the existing scoped `payment.review.read` and `invoice.read` permissions, plus `payment.verify` for decisions and original-account recovery. Verification now requires MFA completed within five minutes. Selecting a receipt also requires `bank.receipt.read`, `bank.statement.read`, `receiving.account.read` and `receiving.account.details.read` in the fulfillment scope. No role pack receives these grants automatically. The current verified person must differ from the original declaration maker and evidence uploader; a different login is insufficient. Unknown, expired or equivalent person evidence blocks the decision.

The controlled payer policy identifies the exact fulfillment organization/shop/channel, approved payer-checking procedure, responsible Finance owner, approval provenance and validity. There is no browser endpoint to create this policy. The receiving-bank channel and Finance-owner decision remains pending in the [decision ledger](../quality/report-decisions.md). A typed reference or matching sender name cannot establish this operational approval or payer identity. This workflow records an accountable human check; it does not connect to an external bank or establish payer identity automatically.

## Review and save

1. Open an explicitly submitted declaration. Compare its selected invoices and declared transfer against **Lihat bukti asli**, immutable OCR and the separately recorded corrections. Evidence must remain privately stored, validated, pinned to that submission and eligible for current access. This route requires successful OCR with a reviewable result; a failed OCR/manual-exception route is still outstanding.
2. Load the current verification result. For an unverified declaration, select one actual confirmed bank receipt. The server verifies its original confirmation, receiving-account version, immutable statement row, audit/receipt/outbox provenance and canonical cash journal. Full receiving-account scope must match the declaration's fulfillment scope.
3. The declared transfer amount, corrected image amount, IDR currency and full receiving-account number must agree exactly. Unknown or masked critical facts block this route. Record a reason for each surfaced date, recipient, bank, reference or time discrepancy. An imported booking date does not supply a verified transfer time or timezone.
4. Follow the approved independent payer procedure for the displayed buying organization, record its evidence reference, and explicitly attest to reviewing the exact image, bank receipt and payer. No checkbox is preselected. Review the frozen declaration/receipt/amount/party and reason before saving.
5. A single transaction records the immutable decision, first receipt-to-party anchor, required audit, original operation receipt and metadata-only outbox event. The reviewed declaration/evidence/OCR/correction and bank histories are fenced with current scope/session/grants/person/policy authority. Corrections cannot race a verification of the old review; accepted correction history becomes fixed for that decision. Time-sensitive authority and the held original-read window are checked through transaction completion.

One declaration has one accepted verification. Further declarations from the same buying organization and fulfillment scope may identify the same canonical receipt, with the original payer claim's complete history validated. They create no extra cash or spending balance. A conflicting party is refused. Future allocation must consume the receipt's single available balance across every linked declaration; summing declarations would double-count funds. Duplicate investigation/warnings and explicit reversal or replacement decisions remain required work.

## Uncertain responses and historical access

The browser retains only actor, operation, key and time. **Periksa hasil** performs an original-account lookup under current scoped authority and fresh MFA. It never resends, allocates, deletes or closes an unknown decision. Changed bodies cannot reuse a committed key. After a known transaction conflict, an intentional same-key retry can recover the original result. Missing decision, payer anchor, approval metadata, bank journal, submitted evidence/OCR/correction or audit/outbox history is denied, never repaired by retry.

Historical decision access checks current read authority and the accepted immutable graph. It does not reopen or return image bytes merely to report an earlier decision, and does not require today's policy to reapprove the original decision. Current person independence is still required for protected receipt recovery. Reading a historical decision never permits another financial effect.

## Release boundary

Local verification uses production-built Next, HTTPS, .NET, private Windows files and isolated replica-set MongoDB. Accounts, sessions, approved procedures/person records and OCR-engine output in this new workflow fixture are synthetic. These checks do not establish OCR-model acceptance, a live Auth0 session, bank connectivity, staging or production acceptance.

Before real activation, complete allocation/overpayment/duplicate/exception/replacement/reversal/refund work, approved operator/business inputs, full release validation, actual provider/API/worker/private-storage deployment checks, one-writer migration/restore and the approved branch pilot. No purchase, live message, bank transfer or real-data cutover is authorized by this runbook. Track status in the [single progress ledger](../quality/production-progress.md).
