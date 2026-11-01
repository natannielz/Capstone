# Unit Toko progress

- **Done:** 1 Nov: J04 green on feat/j04-buyer-paid. The buyer's `/billing/invoices` now shows "Status pembayaran: Lunas · Dibayar … · Sisa tagihan Rp0,00" (invoice view carries the receivable balance, no new endpoint or permission). Green: Golden A2 j04-paid-a2-green (red first at A13c, j04-paid-a2-red3), A1 j04-paid-a1, InvoiceTests + InvoiceHttpTests 16/16, frontend 1525/1525, contracts checks. Earlier the same day: Golden A2 written, F-007 allocation persona (PR #6). 7/15 passing.
- **Next:** J08, branch requests and approvals: rerun the branch-request rehearsal on staging.
- **Blockers:** Phase F (D01–D08) still needs owner inputs. OCR misread the A2 proof (100.00 instead of Rp22.000, no reference), so OCR quality is unproven. Payment review and order-timeline reads sometimes return 409 `version_conflict`; A2 retries only those (docs/debt.md).

## 7/15 passing

| ID | Journey | Passes | Evidence |
| --- | --- | --- | --- |
| J01 | Guest browses the sample catalog and signs in | **yes** | 1 Nov on feat/j01-guest-catalog: CatalogPublicSampleTests 12/12 (samples only, no price, no private photo, non-sample hidden). Signed-out staff URL redirect proven by the catalog identifier and stock release browser rehearsals; buyer redirect by the frontend unit tests |
| J02 | Buyer fills the keranjang and checks out an order | **yes** | 31 Oct on main: OrderSubmission, OrderView and FunctionGap (second order, buyer cancellation) browser tests green |
| J03 | Order to dispatch to receipt across personas | **yes** | 31 Oct on main: Golden A1 (`Golden_order_to_receipt_across_personas_shows_each_result_and_order_number`) green |
| J04 | Invoice release to verified buyer payment | **yes** | 1 Nov on feat/j04-buyer-paid: Golden A2 (`Golden_invoice_release_to_verified_payment_across_personas_shows_each_result_and_invoice_number`) green, j04-paid-a2-green: release by two people, numbered invoice, proof + advisory OCR, checker correction and verification, keuangan-tagihan allocation (F-007), buyer sees Lunas on `/billing/invoices`; money end state asserted in Mongo |
| J05 | Procurement request to received and valued goods | **yes** | 1 Nov on feat/j05-procurement: Golden B (request → SP- → arrival → koli received) plus Funding, Disbursement, Inspection, QualityAcceptance, StockReceipt, StockValuation, SupplierPayable and Settlement browser tests green |
| J06 | Substitution proposal and buyer consent | **yes** | 31 Oct on main: OrderSubstitutionBrowserTests green |
| J07 | Returns, replacement, credit and refund | no | F09 partial |
| J08 | Branch requests, spending policy and approvals | no | Branch-request rehearsal green only on the fix-order branch |
| J09 | Bank statement, receipts and reconciliation | no | No recent run |
| J10 | Reports and read-only auditor | no | No recent run |
| J11 | Staff workspaces: one shell, one queue, consistent pages | no | No journey test yet; page acceptance scripts only |
| J12 | Accounts, sign-in, delegation and access review | no | R11 unproven |
| J13 | Catalog, identifiers, photos and prices | no | No recent run |
| J14 | Warehouse: scanning, stock sources and counts | **yes** | 1 Nov on feat/j14-warehouse (8801e831): receiving, fulfillment, stock count, source reconciliation and replenishment browser tests plus Replenishment(Service)Tests green, 27/27 |
| J15 | Operations: release, monitoring and restore | no | No test; Q09, Q10 and Q12 open |

J02, J03 and J06 were rerun on main on 31 Oct. J14 ran green on its slice branch, which goes to pc-sync by PR. The other rows still rest on commit evidence. Branch-only runs don't count, except a slice branch on its way to pc-sync.

## Next 3 slices

These are ordered by what unblocks order to dispatch (F03, V06, V07, U04, Q05).

1. **J08, branch requests and approvals.**
   - Its branch-request rehearsal was green only on the fix-order branch, which is now merged; rerun it on staging.
2. **J13, catalog, identifiers, photos and prices.**
   - CatalogIdentifierBrowserTests is green again since the J01 slice (G-012 redirect).
3. **J09, bank statement, receipts and reconciliation.**
   - Follows J04's verified payment into the bank side; no recent run.

## Parked work (not resumed)

These come from the 21 Oct multi-agent program. Its plan checkboxes are not status.
- **wr/follow-fe:** buyer polish follow-ups, 3 commits. It stopped on an OrderView rehearsal failure.
- **wr/follow-be:** backend and rules follow-ups, uncommitted, in Capstone-wt/fix-order.
- **wr/perf step 4:** seal backfill command, owned by a separate session, uncommitted. Don't run it on the owner's full pilot without a backup of both mongo-data and session-keys.
- **Throwaway worktree:** Capstone-wt/pc-main. Remove it with tmp/website-ready/remove-worktree.ps1.
