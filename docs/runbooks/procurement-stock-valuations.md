# Held procurement stock valuation

Status: connected and functionally verified locally. Real accounting policy, performance and provider acceptance remain open; see the production progress ledger.

The `/procurement/stock-valuations` workspace connects an inspected held receipt to a human-reviewed supplier invoice match. A proposal reserves neither packages nor money. An independent checker approves or rejects it. Approval consumes receipt and invoice-match capacity and posts inventory debit / receipt-accrual credit through the existing accounting owner in the same Mongo transaction. Goods remain held; the source pool stays inactive. Rejection posts no journal and consumes no capacity.

## Required authority and inputs

- Reading requires scoped valuation, procurement and stock read permissions. Proposal and decision require separately assigned permissions and fresh MFA. Approval also requires scoped journal posting permission.
- The checker must represent a verified person different from both the proposal maker and the person who reviewed the invoice. Creating a second account does not bypass this rule. Existing role templates do not grant these new powers automatically.
- A real policy must identify its independent proposer/approver, approval references, expiry, approved accounting book/chart and exact inventory/accrual account mappings. Synthetic test policies are not Finance approval.
- The receipt must prove the original inspection, reviewed source and canonical held stock. The invoice must prove its private original, human match and exact linked inspection line. OCR output alone cannot authorize value.
- The current treatment is `receipt_accrual` with all positive net, tax and charge components capitalized. A positive component marked as excluded is refused until the appropriate tax/expense treatment is implemented. Zero-cost treatment, payable/advance application, conversion cost lineage and physical release remain separate unfinished scope.

## Operator flow

Select the location and held receipt, inspect its matched invoice line and choose an approved policy. Enter the packages, exact component amounts, posting date and reason, then review the server preview before proposing. A different authorized person reviews the saved proposal and records an explicit approval or rejection reason. Approval rechecks source records, policy expiry, the open period, permissions and remaining capacity inside the transaction.

If a response is lost, recover using the original actor's operation key. Do not submit a second command to discover whether the first one committed. Changed or incomplete historical evidence causes refusal rather than a success inferred from a journal alone.

## Current lot cost

The original receipt records the stock state at receipt time, before valuation. The current cost view separately reconstructs approved valuation allocations for that receipt and lot. It shows how many packages have an approved value, how many remain unvalued, and the exact net, tax, charge and capitalized totals. Unvalued packages have unknown cost; a zero approved total does not mean that the goods are free. No rounded per-package cost is inferred.

Each approved allocation links back to its proposal, independent decision, matched invoice line and journal. Detail pages show only a bounded part of the history; the totals cover the complete verified history. Use refresh to obtain a new snapshot. A historical policy expiry or accounting-period closure does not erase an approved value, but missing or inconsistent retained proof prevents the view from reporting a total. The read requires the same scoped valuation/procurement/stock permissions as the workspace.

This view uses existing valuation records, without another cost ledger or migration. It does not release held stock, activate a source, recognize a supplier payable or allocate cost to a conversion. Those owners remain separate required work. Verification status for this connection is recorded in the production progress ledger.

## Storage and release acceptance

Migration 73 preflights retained receipt/source ownership, accounting records, valuation effects and capacities before installing its strict validators and unique indexes. It preserves stronger validators, source records, collection identities and monotonic counters. Use the normal single-writer migration and restore process; this document does not authorize a production migration or cutover.

The runtime conditionally deletes and reinserts the exact immutable accounting chart in its Mongo transaction to obtain an actual write conflict with concurrent removal/replacement. Provider acceptance must verify the narrowly required find/insert/remove access on `accountingChartVersions` and the effect on configured change-stream consumers. The chart's committed contents remain identical. The existing held-receipt source-policy fences have their own equivalent privilege requirements.

Before launch, record actual Finance policy approval, provider permissions, backup/restore rehearsal, migration evidence, operational stock-source acceptance and the branch pilot in the production progress ledger. Local fixtures, a passing build and a GitHub push do not satisfy those gates.
