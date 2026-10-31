# Unit Toko progress

- **Done:** 31 Oct: J02, J03 and J06 ran green on main. J02 had regressed; fixed the data-list title flex (6469f1f8) and the cancel-tab step in the golden rehearsal. 3/15 passing.
- **Next:** J14, warehouse: scan pick and dispatch against the Surat Jalan, plus a blind stock count with approval.
- **Blockers:** Phase F (D01–D08) still needs owner inputs. wr/follow-fe is unmerged (50+ UI fixes; its M-3 line is now on main).

## 3/15 passing

| ID | Journey | Passes | Evidence |
| --- | --- | --- | --- |
| J01 | Guest browses the sample catalog and signs in | no | Guest-sample test exists; no commit from the last week shows it green |
| J02 | Buyer fills the keranjang and checks out an order | **yes** | 31 Oct on main: OrderSubmission, OrderView and FunctionGap (second order, buyer cancellation) browser tests green |
| J03 | Order to dispatch to receipt across personas | **yes** | 31 Oct on main: Golden A1 (`Golden_order_to_receipt_across_personas_shows_each_result_and_order_number`) green |
| J04 | Invoice release to verified buyer payment | no | Golden A2 is skipped (`Pending.BrowserLatency`); seals (dcdd97aa) should unblock it |
| J05 | Procurement request to received and valued goods | no | All 11 procurement desk rehearsals green (9d8959e6); cross-persona Golden B last green 22 Oct |
| J06 | Substitution proposal and buyer consent | **yes** | 31 Oct on main: OrderSubstitutionBrowserTests green |
| J07 | Returns, replacement, credit and refund | no | F09 partial |
| J08 | Branch requests, spending policy and approvals | no | Branch-request rehearsal green only on the fix-order branch |
| J09 | Bank statement, receipts and reconciliation | no | No recent run |
| J10 | Reports and read-only auditor | no | No recent run |
| J11 | Staff workspaces: one shell, one queue, consistent pages | no | No journey test yet; page acceptance scripts only |
| J12 | Accounts, sign-in, delegation and access review | no | R11 unproven |
| J13 | Catalog, identifiers, photos and prices | no | No recent run |
| J14 | Warehouse: scanning, stock sources and counts | no | No recent run |
| J15 | Operations: release, monitoring and restore | no | No test; Q09, Q10 and Q12 open |

J02, J03 and J06 were rerun on main on 31 Oct. The other rows still rest on commit evidence. Branch-only runs don't count.

## Next 3 slices

These are ordered by what unblocks order to dispatch (F03, V06, V07, U04, Q05).

1. **J14, warehouse.**
   - Scan pick and dispatch against the Surat Jalan, plus blind stock count with approval.
   - These are the stock steps (F03, F04, F10) inside order to dispatch.
2. **J04, invoice release to payment.**
   - Un-skip Golden A2: its latency cause was unsealed fixtures, fixed in dcdd97aa.
   - This continues order to dispatch into invoice and payment.
3. **J08, branch requests and approvals.**
   - Its branch-request rehearsal was green only on the fix-order branch, which is now merged; rerun it on main.

## Parked work (not resumed)

These come from the 21 Oct multi-agent program. Its plan checkboxes are not status.
- **wr/follow-fe:** buyer polish follow-ups, 3 commits. It stopped on an OrderView rehearsal failure.
- **wr/follow-be:** backend and rules follow-ups, uncommitted, in Capstone-wt/fix-order.
- **wr/perf step 4:** seal backfill command, owned by a separate session, uncommitted. Don't run it on the owner's full pilot without a backup of both mongo-data and session-keys.
- **Throwaway worktree:** Capstone-wt/pc-main. Remove it with tmp/website-ready/remove-worktree.ps1.
