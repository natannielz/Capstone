# Workspace kit — shared UI primitives

The building blocks every page uses (staff workspace, buyer pages, storefront). Values come from
`docs/quality/website-ready/ui-standard.md`; tokens live in `src/app/globals.css`, the primitive CSS in
`src/app/foundation.css` (`@layer components`). Never restyle a primitive from a page module — if it needs a new
variant, add it here.

## Rules that make them work inside legacy pages

- Pages that still compose `common.page` (`modules/administration/access.module.css`) or `identity.module.css` get
  element defaults (`.page h1`, `.page p`, `.page label`, `.page input`, `.page th`). Those defaults skip anything inside an
  element marked `data-kit`, and the marked element itself (`:where(:not([data-kit], [data-kit] *))`), so a primitive
  keeps its own look inside a legacy page. Leaf primitives (Button, Pagination, PageHeader, Field, Tabs, StateView) carry
  `data-kit` on their root; `Section` carries it only on its header, so legacy content inside the section body still gets
  the page defaults, and the legacy flow spacing skips `.ui-section`. Don't add `data-kit` to your own markup.
  `tests/ui-kit-cascade-browser.test.ts` measures this in a real browser (primitives as direct children of a legacy `.page`
  and of an `.accountSection`); extend it when you add a primitive. It finds Playwright via `PLAYWRIGHT_PACKAGE` /
  `UNIT_TOKO_PLAYWRIGHT_PACKAGE`, then `tests/browser/node_modules/playwright`, then the local Codex runtime, and launches
  `UNIT_TOKO_BROWSER_CHANNEL` (default `msedge` on Windows, bundled Chromium elsewhere). It **skips** when Playwright or a
  launchable browser is missing (e.g. CI without `npm install` in `tests/browser`); set `UNITTOKO_REQUIRE_BROWSER_TESTS=1`
  to make that a failure.
- Money: buyer-facing storefront and ordering screens use `formatDisplayMoney` / `orderDisplayAmount` ("Rp1.250.000",
  "Rp12.000,25"); staff finance, accounting and every other staff screen use `formatMoney` / `orderAmount` /
  `formatMoneyText` (fixed decimals, "Rp1.250.000,00"). `tests/money-scope.test.ts` pins which files use which.
- One level of boxing: a bordered card never sits inside another (`Section` → `DataList` rows, or an inset panel).
- Max one primary button per region. Refresh is the header's (or section header's) secondary action.
- Keep visible copy and accessible names unchanged when you swap a page onto a primitive: browser rehearsals find
  elements by role and name.

## Primitives

| Import | Use | Notes |
|---|---|---|
| `Button`, `buttonClassName` (`button.tsx`) | Every button. `variant`: `primary` · `secondary` (default) · `tertiary` · `danger` · `danger-confirm`. `size`: `default` 44 px · `compact` 36 px (table rows/toolbars on staff desktop only) · `icon` 44×44 · `icon-compact` 36×36 · `large` 48 px (mobile sticky checkout bar). `busy` shows a spinner, sets `aria-busy` and disables. | For a `<Link>` that looks like a button: `className={buttonClassName({ variant: "primary" })}`. In a narrow column where a label must stay whole (two catalogue cards per phone row) add `className="ui-button--wrap"`: the label wraps and the button stays at least 44 px. Icon sizes need `aria-label`. |
| `PageHeader` (`page-header.tsx`) | The page's one h1. Props: `title`, `description` (one sentence, required on staff pages), `primaryAction`, `secondaryAction`, `breadcrumb`, `meta` (e.g. a `Badge` next to the title), `tabs`. | Secondary renders first, the primary outermost right; on phones they stack full width, primary on top. No eyebrow, no divider. |
| `Section` (`section.tsx`) | A page region: `<section aria-labelledby>` + h2. Props: `title`, `description`, `actions` (secondary/tertiary), `variant` `card` (default) or `plain`, `id` (when two sections share a title), `label` (keeps an existing region name that differs from the h2, so rehearsals that find the region by name keep working), `busy` (aria-busy). | 24 px padding (16 on phones), 24 px between sections. |
| `Toolbar` (`toolbar.tsx`) | Controls above a list: `label`, `search`, `filters` (left), `sort`, `view`, `actions` (right), `resultText` ("6 kemasan"). | Put the search in a `Field width="search"`. Result text never names the viewer. |
| `Field` (`field.tsx`) | Label + control + help/error. Props: `id` (required), `label`, `help`, `error`, `optional`, `width` (`number` 160 · `date` 200 · `rupiah` 240 · `search` 360). The single child is the control. | Field wires `id`, `aria-describedby` (keeps ids the control already had) and `aria-invalid`. Keep `RupiahInput`'s parsing; wrap its input. |
| `DataList` (`data-list.tsx`) | Queues, orders, requests: divided rows. Props: `label`, `items`, `getKey`, `renderTitle`, `renderMeta`, `renderBadge`, `renderContent`, `renderActions`, `onSelect` + `selectedKey` (whole row opens the record), `openLabel` (names that row button when the record already has a known name, e.g. "Tinjau pesanan PSN-…"; keep the visible title inside it, WCAG 2.5.3), `loading`, `error`, `empty`, `pagination`. | Exactly one state: error replaces the list (never "Belum ada …" after a failed read); the pager hides when everything fits. |
| `DataTable` (`data-table.tsx`, = `ListTable`) | Dense comparable records. Props as `ListTable` (`columns` with `align`/`width`, `rows`, `getKey`, `caption`, `density`, `onOpen`/`openLabel`, `selectedKey`, `disabled`) plus `loading`, `error: { message, detail?, onRetry? }`, `empty: { title, message?, action? }`, `pagination`. | Without `empty`, an empty table still renders (old behaviour). Rows become labelled cards below 640 px. |
| `Pagination` (`data-table.tsx`) | "1–25 dari 120" + previous/next icon buttons (`aria-label` "Sebelumnya"/"Berikutnya"). Renders nothing when there is no previous and no next page. | Used by `DataTable`/`DataList`; use it directly only for custom lists. |
| `Badge` (`badge.tsx`) | Status: `status="Lunas"` (tone from `lib/status/dictionary.ts`) or `tone` + `label`. | `StatusBadge` (existing callers) renders through it. Add new labels to the dictionary, not to CSS. |
| `Tabs` (`tabs.tsx`) | `items` with `href` → page tabs (`<nav>`, `aria-current="page"`); items with `value` → in-page views (a `role="group"` of buttons with `aria-pressed`, `onSelect`; not the ARIA tablist pattern). Props: `label`, `items`, `current`, `disabled` (view tabs: blocks switching while the page is busy). | View tabs stay buttons so rehearsals that click "Riwayat permintaan" by role button keep working. Links to other areas are not tabs. |
| `StateView`, `regionState` (`state-view.tsx`) | `kind`: `loading` · `empty` · `error` · `unknown` · `denied` · `success` · `offline`, with `title`, `message`, `detail` (trace code), `action`, `secondaryAction`, `rows` (skeleton), `align="center"` (the storefront full-page state — empty cart, nothing found: centred, 48 px padding, 40 px icon, the title an h2), `icon` (replaces the kind's icon). `regionState({ loading, error, empty, denied })` picks the one state to show. | New semantics for pages that adopt it: error/unknown are `role="alert"`, denied is neutral (h2), empty is an h3 inside its section. `OutcomeState` and `EmptyState` keep their legacy markup and roles (denied/empty/offline `role="status"`, `<strong>` title) for existing consumers and share the look. |
| `Facts` (`facts.tsx`) | Description list: `items: { term, value, key? }[]`, `columns` 2 (default, one below 640 px) or 1. | Replaces `common.facts`, `.review-panel dl` and request `.meta`. |

Existing kit pieces (`ReviewPanel`, `RupiahInput`, `QuantityStepper`, `CommandPalette`, `WorkspaceFrame`) already use the same tokens.

## Swapping a legacy page (for page agents)

| Legacy | Primitive |
|---|---|
| `common.header` / `styles.header` / `styles.heading` / `accountHeader` | `PageHeader` |
| `common.accountSection`, `.queue`/`.detail` panels, `branch-request .section` | `Section` |
| `common.primary` / `.secondary` / `.danger`, `.outcome-action`, `.list-table-open` | `Button` |
| `common.alert`, `common.state`, `common.empty`, buyer `.accessState` | `StateView` (decide with `regionState`) |
| `common.queueList` / `.queueRow` / `.selectedRow`, `branch-request .list/.item`, order cards | `DataList` |
| raw `<table>` under `.page` | `DataTable` |
| `common.status`, request/inventory `.badge` | `Badge` |
| `common.facts`, `.meta` | `Facts` |
| button tabs (`.primary` as current tab), `.administrationNav` | `Tabs` |
| `.page label > input` | `Field` |

Tests: `tests/workspace-kit-primitives.test.ts` (markup contracts) and `tests/ui-standard-css.test.ts` (token values).
