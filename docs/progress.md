# Unit Toko progress

- **Done:** board drafted: 15 journeys in `docs/readiness.json` covering all 97 register items. 2/15 passing.
- **Next:** J03, order to dispatch to receipt (Golden A1 on current main).
- **Blockers:** board awaiting owner approval. Phase F (D01–D08) still needs owner inputs.

## 2/15 passing

| ID | Journey | Passes | Evidence |
| --- | --- | --- | --- |
| J01 | Guest browses the sample catalog and signs in | no | Guest-sample test exists; no commit from the last week shows it green |
| J02 | Buyer fills the keranjang and checks out an order | **yes** | a319a18a (29 Oct): OrderSubmission and OrderView browser tests green; second order from the keranjang works (ac6de924) |
| J03 | Order to dispatch to receipt across personas | no | Golden A1 last green on the unmerged fix-order branch (29 Oct), never on main |
| J04 | Invoice release to verified buyer payment | no | Golden A2 is skipped (`Pending.BrowserLatency`); seals (dcdd97aa) should unblock it |
| J05 | Procurement request to received and valued goods | no | All 11 procurement desk rehearsals green (9d8959e6); cross-persona Golden B last green 22 Oct |
| J06 | Substitution proposal and buyer consent | **yes** | a319a18a (29 Oct): OrderSubstitutionBrowserTests green |
| J07 | Returns, replacement, credit and refund | no | F09 partial |
| J08 | Branch requests, spending policy and approvals | no | Branch-request rehearsal green only on the fix-order branch |
| J09 | Bank statement, receipts and reconciliation | no | No recent run |
| J10 | Reports and read-only auditor | no | No recent run |
| J11 | Staff workspaces: one shell, one queue, consistent pages | no | No journey test yet; page acceptance scripts only |
| J12 | Accounts, sign-in, delegation and access review | no | R11 unproven |
| J13 | Catalog, identifiers, photos and prices | no | No recent run |
| J14 | Warehouse: scanning, stock sources and counts | no | No recent run |
| J15 | Operations: release, monitoring and restore | no | No test; Q09, Q10 and Q12 open |

`passes` was set only where a commit from 22–29 Oct shows that journey's test green. Branch-only runs don't count.

## Next 3 slices

These are ordered by what unblocks order to dispatch (F03, V06, V07, U04, Q05).

1. **J03, order to dispatch to receipt.**
   - Run Golden A1 on current main; it has not run there since the UI-PAGES-C, PERF and rehearse merges.
   - Fix what fails: F03 stock reservation and release, V06 keranjang to receipt, U04 page states.
2. **J14, warehouse.**
   - Scan pick and dispatch against the Surat Jalan, plus blind stock count with approval.
   - These are the stock steps (F03, F04, F10) inside order to dispatch.
3. **J04, invoice release to payment.**
   - Un-skip Golden A2: its latency cause was unsealed fixtures, fixed in dcdd97aa.
   - This continues order to dispatch into invoice and payment.

## Parked work (not resumed)

These come from the 21 Oct multi-agent program. Its plan checkboxes are not status.
- **wr/follow-fe:** buyer polish follow-ups, 3 commits. It stopped on an OrderView rehearsal failure.
- **wr/follow-be:** backend and rules follow-ups, uncommitted, in Capstone-wt/fix-order.
- **wr/perf step 4:** seal backfill command, owned by a separate session, uncommitted. Don't run it on the owner's full pilot without a backup of both mongo-data and session-keys.
- **Throwaway worktree:** Capstone-wt/pc-main. Remove it with tmp/website-ready/remove-worktree.ps1.
