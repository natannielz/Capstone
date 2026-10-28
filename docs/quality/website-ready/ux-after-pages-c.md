# UX after UI-PAGES-C (Task 3.8): billing, administration, workspace home, operations, accounting and reporting staff pages

Scored against `docs/quality/website-ready/ui-standard.md` §14 with `tmp/website-ready/ux/pages-c/accept.cjs`. The captures live in `tmp/website-ready/screens/` in the main checkout:
- `before-mock/<page>@<w>.png`: the original audit. It predates UI-SYSTEM, so the "before" below includes what UI-SYSTEM already changed (the 4 px radii and 48 px buttons are gone since then).
- `pages-c/after/`: this branch, mocked API, with the interaction screens (`books-detail`, `rep-result`, `anomaly-result`, `orgs-detail`, `orgs-proposal`).
- `pages-c/real/`: this branch against the loopback review stack, signed in as a minted persona.

The metrics are in `tmp/website-ready/ux/pages-c/metrics-{after,real}/`. The audit metrics are in `tmp/website-ready/ux/metrics/`.

Procurement (the other half of U-040) is not in this document: it waits for FIX-FLOW D's merge.

Acceptance (no horizontal scroll; h1 28/700, or 24 at 390, at x = 264 + gutter; controls 44 px; radii 8/12; no bordered box inside another; at most one primary Button per region; axe with 0 serious or critical and 0 color-contrast, target-size or region):

| Run | Result |
|---|---|
| after (mocked, 23 screens × 2 widths) | **46/46** |
| real data (18 pages × 2 widths) | **36/36** |
| first mocked run, before the fixes below | 32/36 (health and workspace) |

Fixed after the first run: a table inside a card Section (nested boxes), and the skip link on `/workspace` (48 px, radius 0).

| Finding | Status | Before | After |
|---|---|---|---|
| U-036 billing (7 pages): an error replaces the content; forms in the 720 px column with 16/24 px spacing; refresh in the header; disabled primary uses the standard token | fixed | `before-mock/invoices@1280.png`, `bil-payments@1280.png`; h1 39.68 px at x = 315; buttons 48 px, radius 4; the invoices form was one 864 px textarea under an error banner and a live "Belum ada penerimaan" form | `pages-c/real/invoices@1280.png` (empty states, one per region), `pages-c/real/bil-*@{1280,390}.png`, `pages-c/after/bil-*`; h1 28/700 at x = 296; buttons 44 px, radius 8; forms in `.ui-form`; refresh as the header's secondary action. The persona `keuangan-tagihan` is denied on most billing pages (trimmed pack), so the captures show the denied state, not rows |
| U-037 administration (6 pages): two-column desk, queue at least 320 px, refresh in the header, no pager when empty, no glued controls, one state body per error | fixed | `before-mock/adm-delegations@1280.png` (155 px queue, 38 px buttons, refresh touching the card), `orgs@1280.png` (banner + button + card for one error) | `pages-c/real/adm-delegations@1280.png`, `pages-c/real/adm-access@1280.png`, `pages-c/after/orgs-detail@1280.png`, `pages-c/after/orgs-proposal@1280.png`; desk columns 320+ px, top-aligned; one StateView per error; pagers hidden when the list fits |
| U-038 workspace home: KPI tiles 16 px, no stripe; header actions; h1 28/700 at the staff left edge; off `identity.module.css` fields | fixed | `before-mock/workspace@1280.png`: h1 43.52 px at x = 328; "Muat ulang" 50 px floating; 64 px number column and a 4 px ink stripe; 155 px tiles (45 of them); 6 px radii (64) | `pages-c/real/workspace@{1280,390}.png`: h1 28/700 at x = 296; `Section` actions; tiles with a 28/700 number 16 px from the label; search through `Field`; axe `region` 1 → 0; `landmark-unique` ×6 → ×5, all in the workspace frame's sidebar groups, not in this page (moderate, not scored by §14; leftover) |
| U-039 operations: severity as Badge, health queue as DataTable that stacks on phones, facts through Facts, 44 px buttons | fixed | `before-mock/health@1280.png`, `health@390.png` (horizontal scroll with clipped headers), `ops-anomalies@1280.png` (52 px buttons, 46 px fields) | `pages-c/after/health@{1280,390}.png` (Badge beside each finding; stacked cards at 390, 0 horizontal scroll), `pages-c/after/anomaly-result@1280.png` (44 px buttons, Badge per signal) |
| U-040 accounting and reporting (procurement pending) | fixed for `/accounting/books` and `/reporting/accounts` | `before-mock/books@1280.png`, `rep-accounts@1280.png` (48 px ink buttons, 360 px selectors, tables scrolling sideways) | `pages-c/after/books-detail@{1280,390}.png`, `pages-c/after/rep-result@{1280,390}.png` (DataTable, Field selectors, header refresh) |
| U-003 one PageHeader | fixed on all 17 pages | | `pages-c/after/*`, `pages-c/real/*` |
| §8 one primary per region | fixed and pinned: the test scans every module of these pages for two primaries in one row, and `measure.cjs` counts primaries per rendered region (0 regions with two) | | |
| §9 warnings vs errors | warnings keep `role="status"` or no role; only errors are alerts | | |
| U-004 / U-021 one Button; mobile action rows | fixed: every legacy `.primary/.secondary/.danger` on the 17 pages is the kit Button; command Buttons show `busy` | | |
| U-008 one level of boxing | 0 nested boxes in the after runs; tables sit on the canvas (plain Section) | | |
| U-009 / U-010 / U-023 stripes, outline, hex | fixed in the sheets these pages own (`invoice`, `bank-statement`, `health`, `anomaly`, `reporting`, `organizations`, new `workspace-home`): 0 hex, 0 stripes, 0 outline | | |
| U-011 one state per region | fixed: lists through DataList or DataTable; an error replaces the list; loading is a skeleton | | |
| axe `region` / `landmark-unique` | `region` 0 on these pages (before ×1–2 per page); `landmark-unique` ×5 remains on `/workspace` from the frame sidebar (see U-038) | | |

## Not covered

- Real rows: the review stack has few rows for these personas. Billing, accounting, reporting and operations are mostly denied or empty there; the mocked run supplies rows for organizations, books, the report and the anomaly review.
- `/identity/account` is UI-PAGES-A's.
- Procurement (U-040).
