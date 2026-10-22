# UX after UI-PAGES-B (Task 3.7): catalogue, order desk, fulfilment and inventory staff pages

Scored against `docs/quality/website-ready/ui-standard.md` §14 with `tmp/website-ready/ux/pages-b/accept.cjs`.
The captures live in `tmp/website-ready/screens/` in the main checkout:
- `before-mock/<page>@<w>.png`: the original audit.
- `pages-b/before/`: the base 02e773d7, mocked API.
- `pages-b/after/`: this branch, mocked API.
- `pages-b/real/`: this branch against the loopback review stack, signed in as a minted persona.

The metrics are in `tmp/website-ready/ux/pages-b/metrics-{before,after,real}/`.

Acceptance (no horizontal scroll; h1 28/700, or 24 at 390, at x = 264 + gutter; controls 44 px; radii 8/12; axe with 0 serious or critical and 0 color-contrast, target-size or region):

| Run | Result |
|---|---|
| before | 21/44 |
| after (mocked) | **46/46** |
| real data | **44/44** |

| Finding | Status | Before capture | After capture |
|---|---|---|---|
| U-032 inventory: private palette, eyebrow, stripes, orange focus, square badges, error state | fixed | `before-mock/sources@1280.png`, `before-mock/inv-releases@1280.png`, `pages-b/before/inv-*@1280.png` | `pages-b/after/sources@1280.png`, `pages-b/real/sources@1280.png`, `pages-b/real/inv-{activations,aliases,policies,revisions,suspensions,releases,counts,replenishment,reconciliations}@{1280,390}.png` |
| U-033 order desk: whole-row open, badge on the right, refresh as a section action, no ink tab button | fixed for reviews and substitutions; incoming is in UI-PAGES-A's commit on the same file | `before-mock/ordering-reviews@1280.png`, `before-mock/incoming@1280.png` | `pages-b/real/ordering-reviews@{1280,390}.png`, `pages-b/real/ordering-substitutions@1280.png`, `pages-b/real/incoming@1280.png` (UI-PAGES-A markup) |
| U-034 catalogue staff: DataList rows, Badge, no placeholder title, `/catalog/browse` | fixed for `/administration/catalog`, `/catalog/prices` and `/catalog/public`; `/catalog/browse` now redirects to `/shop` (UI-PAGES-A) | `before-mock/showcase@1280.png`, `before-mock/catalog-prices@1280.png`, `before-mock/catalog-browse@1280.png` | `pages-b/real/catalog-prices@1280.png` (real rows), `pages-b/real/showcase@1280.png`, `pages-b/real/admin-catalog@1280.png` |
| U-035 fulfilment: standard header, states and lists, refresh in the header, receive renders its controls | fixed | `before-mock/ful-*@1280.png`, `before-mock/ful-receive@1280.png` (0 controls) | `pages-b/real/ful-*@{1280,390}.png`, `pages-b/after/ful-receive-qr@1280.png` ("Periksa kiriman" for a QR link), `pages-b/real/ful-receive@1280.png` (no QR: the standard state with its next step) |
| U-003 one PageHeader | fixed on all 21 pages | `pages-b/before/*` | `pages-b/after/*` |
| U-004 / U-021 one Button; mobile action rows | fixed: every legacy `.primary/.secondary/.danger` on these pages is the kit Button, and lone buttons in section grids keep their own width on desktop and span the row on phones | | |
| U-008 one level of boxing | fixed: 0 nested boxes before and after; review and confirmation panels inside cards are inset panels | | |
| U-009 / U-010 / U-023 stripes, outline, hex | fixed: the 7 inventory sheets, `catalog-editor` and the new `fulfillment` sheet have 0 hex, 0 stripes and 0 outline (they had 100 hex) | | |
| U-011 one state per region | fixed: lists render through DataList; an error replaces the list; loading is a skeleton | | |
| U-012 / U-013 / U-022 DataList, Badge, Facts | fixed on these pages | | |
| U-014 fields | unchanged (already 44 px from UI-SYSTEM; replenishment fields were 9.6 px radius and are now 8) | | |
| axe `region` (frame) | fixed: the workspace top bar is a `<header>`; 22 → 0 region violations | `pages-b/before/*@390` | `pages-b/after/*@390` |
