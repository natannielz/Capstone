# UX after UI-PAGES-A (Task 3.6): storefront and buyer pages

Scored against `docs/quality/website-ready/ui-standard.md` §14 with `tmp/website-ready/ux/pages-a/accept.cjs` (no horizontal scroll; h1 28/700, or 24/700 at 390, on the frame's left edge; controls 44 px; radii 8/12; axe with 0 serious or critical and 0 color-contrast, target-size or region).

Captures are in `tmp/website-ready/screens/pages-a/` in the main checkout, `<page>@1280.png` and `<page>@390.png` (plus `-top` crops):
- `before/`: base 02e773d7, mocked API (`ui-pages-a-base` worktree).
- `after/`: this branch, mocked API.

Both sets were captured again on 2026-10-28 23:55-23:56, after the shared `node_modules` damage window, with `tmp/website-ready/ux/pages-a/both.ps1` under the heavy lock. Metrics: `tmp/website-ready/ux/pages-a/metrics-{before,after}/`.

| Run | Result |
|---|---|
| before | 0/38 |
| after | **34/38** (all 34 store, buyer and sign-in captures pass; the 4 failures are the landing page `/`, which keeps its design: axe `region` ×1, a skip link without a target, as before) |

"After" scores buttons whose label wraps on a phone card (`ui-button--wrap`, "Buka halaman kemasan", 58 px at 390) as passing: the kit rule is a wrapped label with a button of at least 44 px.

| Finding | Status | Before capture | After capture |
|---|---|---|---|
| U-024 guest and signed-in catalogue share one design | fixed | `before/guest-katalog@1280`, `before/guest-shop@1280`, `before/catalog@1280` | `after/guest-katalog@1280`, `after/guest-shop@1280`, `after/catalog@1280`, `after/catalog-filter@390`, `after/catalog-list@1280` |
| U-025 one product page, photo 5 / buy box 7, category by name | fixed | `before/product@1280`, `before/guest-katalog-product@1280` | `after/product@1280`, `after/product@390`, `after/guest-katalog-product@1280` |
| U-026 cart on PageHeader and Section, one branch preselected, centred empty state | fixed | `before/cart@1280`, `before/cart-empty@1280` | `after/cart@1280`, `after/cart-empty@1280`, `after/cart@390` |
| U-027 orders as DataList rows in one section, filter in a Toolbar | fixed | `before/orders@1280`, `before/history@1280` | `after/orders@1280`, `after/orders@390`, `after/history@1280` |
| U-028 checkout: standard h1, 720 px form column with the summary beside it | fixed | `before/checkout@1280` | `after/checkout@1280`, `after/checkout@390` |
| U-029 store header: one landmark, 64 px main bar, one button treatment | fixed | `before/catalog@1280-top`, `before/catalog@390-top` | `after/catalog@1280-top`, `after/catalog@390-top` |
| U-030 sign-in on the shared tokens, `Button`/`Field`, 720 px panel | fixed | `before/login@1280`, `before/guest-login@1280` | `after/login@1280`, `after/login@390`, `after/guest-login@1280` |
| U-031 `/shop` toolbar on one row, sort label whole | fixed | `before/catalog-filter@1280` | `after/catalog-filter@1280`, `after/catalog@1280` |
| Shared rules U-003, U-004, U-008, U-011, U-013..U-018, U-021..U-023 on these pages | fixed | as above | as above |

Measured before → after (mocked, 1280 / 390):

| Page | Before | After |
|---|---|---|
| guest `/katalog`, guest `/shop` | h1 32/700 at x=40; chips and select 38 px; sign-in buttons 37 or 58 px; contrast violation (serious) at 390 | h1 28/700 at x=32 (24 at 390, x=16); all controls 44 px; axe clean |
| `/shop` signed in | chips 40 px, view toggle 42 px with square corners, nested photo frames | 44 px, radii 8/999, no nested box |
| `/shop/[product]` | h1 at x=610 (1280) and x=37 (390); photo frame inside the buy box | h1 at x=32 and x=16; no nested box |
| `/cart` | h1 32/700 | h1 28/700 |
| `/ordering/checkout` | h1 44/400 (29/400 at 390) | h1 28/700 (24/700) |
| `/ordering/orders`, `/ordering/requests`, history | axe `region` at 1280, `button-name` (critical) at 390 | clean |
| `/identity/login` | h1 38/760 at x=703; button 52 px, radius 10 | h1 28/700; button 44 px, radius 8 |
| `/identity/account` | axe `region`; `button-name` (critical) at 390 | clean |
