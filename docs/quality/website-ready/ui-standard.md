# Unit Toko UI standard (website-ready)

Status: binding for every UI change in the website-ready program (Task 1.3, UX agent, 21 Okt 2026).
Audience: the UI-SYSTEM implementer who builds the shared primitives, and the UI-PAGES-A/B/C implementers who move pages onto them.
Findings that measure today's UI against this standard: `docs/quality/website-ready/findings-ux.md`.

## 0. How to read this

- Every value below is exact. "Token" names a CSS custom property. **Existing** tokens live in `frontend/src/app/globals.css` (`:root` and `@theme inline`). **New** tokens are marked *(add)* and must be added to `globals.css` `:root` (and mapped in `@theme inline` when a Tailwind utility is wanted). Nothing else may define a colour, radius, size or spacing value.
- The legacy palette in `frontend/src/modules/workspace-palette.module.css` (`--workspace-*`, 44 hex values) and the module-local hex values (355 in module CSS today) are retired by this standard. Map them as in section 13.
- One look for the whole product: storefront, buyer pages, staff workspace and sign-in use the same tokens, the same type scale and the same primitives. The landing page (`/`) is the only place allowed a marketing treatment (display type, illustration), and it still uses these tokens for buttons, fields and colour.
- Shared primitives named here: `PageHeader`, `Section`, `Toolbar`, `Field`, `DataList`, `DataTable`, `Badge` (StatusBadge), `Button`, plus `Facts`, `Tabs`, `StateView` (empty, loading, error, denied, unknown). Put them in `frontend/src/components/workspace-kit/` (staff and buyer both use them); their CSS lives in `frontend/src/app/foundation.css` `@layer components` (that file is already the single owned stylesheet) or in a co-located CSS module, never in a page module.

## 1. Tokens

### 1.1 Colour (existing, keep)

| Role | Token | Value today | Use |
|---|---|---|---|
| Page canvas | `--background` | #f2f6f7 | `body`, every page, both frames (storefront and workspace). One canvas everywhere. |
| Text | `--foreground` | #1d3640 (11.7:1 on canvas) | Body text, headings. |
| Surface | `--card` | #ffffff | Section, card, table, dialog, field background. |
| Muted text | `--muted-foreground` | #4f6670 (6.1:1 on card, 5.6:1 on canvas) | Purpose line, meta, help text, table header text. |
| Muted surface | `--muted` | #eef3f5 | Table header row, skeleton, inset panels inside a section. |
| Hover / selected surface | `--secondary` / `--secondary-foreground` | #e8f3f4 / #164954 | Row hover, selected nav item background, chip hover. |
| Brand action | `--primary` / `--primary-foreground` | #077682 / #fff (5.35:1) | Primary button, links, active tab marker, selected chip, focus ring. |
| Danger action | `--destructive` | #bf3941 (5.4:1 with white) | Filled confirm of a destructive action only. |
| Divider | `--border` | #dfe8ec | Section border, row dividers. Decorative only. |
| Focus | `--ring` | #077682 | The only focus colour. |
| Status pairs | `--tone-{neutral,info,warning,success,danger}` + `-foreground` | contrast 5.7–7.6:1 | Badge, state panels, inline notices. |

### 1.2 Colour (add)

| Token *(add)* | Value | Why |
|---|---|---|
| `--control-border` | `oklch(0.62 0.02 225)` (#7a898f, 3.6:1 on card) | Field and secondary-button outline. Today `--input` = `--border` (1.24:1) and the legacy `--workspace-border-input` #a3bcb4 (2.0:1) both fail WCAG 1.4.11 (3:1 for control boundaries). Set `--input: var(--control-border)` too. |
| `--primary-hover` | `oklch(0.46 0.08 207.38)` (#00646f) | Primary button hover/pressed. Replaces `filter: brightness()` and `--workspace-ink-hover`. |
| `--overlay` | `color-mix(in oklab, var(--foreground) 45%, transparent)` | Sheet/dialog backdrop and menu shadow colour. |

### 1.3 Spacing scale (add)

The scale is 4, 8, 12, 16, 24, 32, 48 px. Nothing else is allowed for padding, margin or gap (exceptions: 1 px hairlines, 2 px focus offsets). Tailwind's `--spacing: 0.25rem` already yields these as `1, 2, 3, 4, 6, 8, 12`; CSS modules use:

| Token *(add)* | px | Typical use |
|---|---|---|
| `--space-1` | 4 | Icon-to-text inside badges, label to help text |
| `--space-2` | 8 | Label to control, gap between buttons in a group, badge padding-x |
| `--space-3` | 12 | Cell padding-y, gap inside a card's meta line, toolbar gaps |
| `--space-4` | 16 | Field-to-field gap, cell padding-x, section padding on mobile, mobile gutter |
| `--space-6` | 24 | Section padding on desktop, gap between sections, header bottom margin, desktop gutter |
| `--space-8` | 32 | Gap between section groups, page top padding on desktop, wide gutter (≥1280 px) |
| `--space-12` | 48 | Page bottom padding, empty-state vertical padding |

Today 748 of 1,261 padding/margin/gap px values (59%) are off this scale (18 px ×124, 20 px ×109, 10 px ×100, 14 px ×64, 22 px ×55, 6 px ×52, 28 px ×48 …). Round each to the nearest step in the direction that keeps rhythm: 6→8, 10→8 (or 12 in cells), 14→12 (or 16), 18→16, 20→16 (or 24 for section padding), 22/26/28→24, 36/40→32 (or 48).

### 1.4 Size tokens (add)

| Token *(add)* | Value | Use |
|---|---|---|
| `--control-h` | 44px | Every button, input, select, combobox, search box, chip, segmented control, pager button, icon button, nav item on touch. |
| `--control-h-compact` | 36px | Only inside `DataTable` rows and table toolbars on staff desktop pages (`data-density="compact"`). Never on storefront or ≤1023 px. |
| `--row-h` | 48px | `DataTable` default row min-height. |
| `--row-h-compact` | 36px | `DataTable` compact row min-height. |
| `--radius-control` | 8px | Buttons, fields, chips (non-pill), menus, row action buttons, thumbnails. |
| `--radius-card` | 12px (= existing `--radius: .75rem`) | Section, card, table wrapper, dialog, empty/state panels. |
| `--radius-badge` | 6px | Badge, tag, `kbd`. |
| `--radius-pill` | 999px | Filter chips and count bubbles only. |
| `--content-narrow` | 720px | Forms, single-record reading, sign-in panel, empty-state copy column. |
| `--content-default` | 1080px | Staff pages, checkout, order detail, account pages. |
| `--content-wide` | 1280px | Storefront frame and catalogue grid, staff pages whose main content is a multi-column `DataTable`. |
| `--gutter` | 16px (<768), 24px (768–1279), 32px (≥1280) | Horizontal page padding in both frames. |
| `--header-h` | 64px desktop, 56px mobile | Storefront main bar and workspace mobile top bar. |

Radii allowed: 6, 8, 12, 999 (pill) and 50% for avatars. Today there are 23 distinct radius values (8 px ×43, 6 px ×22, 10 px ×21, 4 px ×12, 12 px ×11, plus 0, 2, 3, 5, 14, 18, 30 px, .5rem, .6rem, .7rem…).

### 1.5 Elevation

Flat UI. Sections use a 1 px `--border` and no shadow. Only overlays (menu, popover, dialog, sheet, toast) get `box-shadow: 0 16px 40px -18px var(--overlay)`. No coloured accent stripes (`border-left/top: 3–4px solid <colour>`): status is shown with a `Badge` or a tone surface, not with stripes (46 such stripes exist today in 18 stylesheets).

## 2. Typography

Fonts: `--font-body` (DM Sans variable, self-hosted) everywhere. `--font-display` (Archivo Black) only on the landing hero. `--font-mono` (Consolas) only for codes in `kbd` and technical ids when monospaced alignment is required. Weights allowed: **400, 600, 700**. (650 and 760 are retired.) Numbers in tables, totals and KPIs use `font-variant-numeric: tabular-nums`.

| Style | Size / line-height / weight | Letter-spacing | Use |
|---|---|---|---|
| Page title (h1) | 28/34 px, 700 (≤639 px: 24/30) | -0.02em | One per page, in `PageHeader`. |
| Section title (h2) | 20/28 px, 600 (≤639 px: 18/26) | -0.01em | `Section` header. |
| Subsection / card title (h3) | 16/24 px, 600 | 0 | Card title, form group legend, dialog sub-heading. |
| Body | 15/24 px, 400 | 0 | Paragraphs, list text, field values. (`body` already 15 px.) |
| Body strong | 15/24 px, 600 | 0 | Record numbers, names in lists. |
| Label | 14/20 px, 600 | 0 | Field labels, table header text (13 px allowed in compact). |
| Small / meta | 13/20 px, 400, `--muted-foreground` | 0 | Purpose line on mobile, meta under a row title, help text, timestamps. |
| Caption / badge | 12/16 px, 600 | 0.01em | Badge, count bubble, sidebar group label. Nothing smaller than 12 px anywhere. |
| KPI number | 28/32 px, 700, tabular | -0.01em | Work counts on "Pekerjaan saya". |
| Price (product page) | 24/32 px, 700, tabular | 0 | Product price; list/table prices use body strong. |
| Landing display | `clamp(42px, 5.4vw, 84px)` / 0.98, 700, `--font-display` allowed | -0.04em | Landing hero only. |

Rules:
- One h1 per page, always the first heading in `<main>`; no `clamp()` on page titles (only on the landing display).
- Heading defaults must actually apply: move the heading rule out of the zero-specificity `:where(h1,h2,h3,h4)` in `foundation.css:7` (it loses to Tailwind preflight `h1{font-weight:inherit;font-size:inherit}`) and set the sizes above on `h1`–`h3` in `@layer base` with element selectors after preflight, so a page that forgets a class still gets the right heading.
- Text measure: paragraphs max 72ch. **Alerts, notices and state panels are not paragraphs**: they take the full width of their container (today 270 alerts are `<p>` and inherit `max-width: 75ch` from `access.module.css:7`, so their right edge misses the section edge).
- Sentence case for every heading, button, label, tab and badge. No ALL CAPS (category codes such as `PANTRY`/`RAPAT` must be shown by name).
- Money: `Rp1.250.000` (no decimals for IDR display; `formatRupiah` default). Dates `21 Okt 2026`, times `14.05 WIB` via `frontend/src/lib/format.ts`.

## 3. Layout

### 3.1 Frames and content widths

| Frame | Structure | Content container |
|---|---|---|
| Storefront (`StoreFrame`, guests and buyers) | Utility bar 32 px (desktop only) + sticky main bar `--header-h`; footer; mobile bottom tabs 56 px + safe area. | One container class shared by header, page and footer: `width: min(var(--content-wide), 100% - 2*var(--gutter)); margin-inline: auto`. Pages inside never add their own horizontal padding or second max-width. |
| Workspace (`WorkspaceFrame`, staff) | Fixed sidebar 264 px ≥1024 px; sticky top bar 56 px <1024 px. | Content column starts at `sidebar + var(--gutter)` and is `max-width: var(--content-default)` (or `--content-wide` for table-first pages). Left-aligned to the sidebar, not centred. Top padding 32 px desktop, 24 px mobile; bottom padding 48 px. |
| Focus pages (sign-in, checkout, single form) | Same frame. | `--content-narrow` for sign-in and single forms; checkout uses `--content-default` with a 2-column body (form 1fr, summary 360 px). |

Width tier per page type: forms and record readers 720; staff work pages, account pages, checkout, order detail 1080; catalogue grid and wide tables 1280.

### 3.2 Single left edge

Everything in the content column starts at one x position: breadcrumb, h1, purpose line, toolbar, section borders, alerts, state panels and the footer content. Concretely at 1280×800:
- Storefront: brand mark, breadcrumb, h1 and grid all at x = 32 (gutter). Today they sit at 40 (brand), 40 (cart, guest catalogue), 71 (orders, checkout, drafts, requests) and 84 (signed-in catalogue, product).
- Workspace: h1, sections and alerts all at x = 264 + 32 = 296. Today 315 on most pages and 328 on "Pekerjaan saya".
- Inside a section, content is inset by the section padding (24 px); nothing inside a section is indented further except list-item meta lines aligned to the title text.
- Right edges also align: alerts, toolbars and sections all end at the container's right edge.

### 3.3 Grid

12-column fluid grid inside the container, column gap 24 px (16 px <768). Common splits: main 8 / aside 4 (detail + summary), 6/6 (two short forms), product page 5/7. Catalogue grid: `repeat(auto-fill, minmax(200px, 1fr))`, gap 16 px — 5 columns at 1280, 2 columns at 390, for guests and signed-in buyers alike.

## 4. Page header (`PageHeader`)

```
[breadcrumb or back link]            (storefront and nested staff pages only; 13 px meta, 8 px below)
Title (h1)                                        [secondary action] [Primary action]
One-line purpose (15 px muted, max 72ch)
[Tabs]                                             (optional, 16 px below purpose)
```

- Props: `title` (required), `description` (one sentence; required on staff pages), `actions` (0–2 buttons: at most one primary, plus one secondary or a refresh icon-button), `breadcrumb?`, `tabs?`, `meta?` (e.g. "18 produk", status badge next to title for record pages).
- Layout: flex row, title block left, actions right and vertically aligned to the h1's first line (`align-items: flex-start`, actions `margin-top: 0`). Gap 24 px. Title block max-width 72ch.
- Spacing: h1 → description 8 px; header → first content 24 px; no divider line under the header (today `access.module.css:12` draws a border with 26 px padding). Tabs, if present, sit on their own bottom border and replace that gap.
- No eyebrow / kicker above the title (today only inventory pages show "Pengelolaan persediaan"); use the breadcrumb or the sidebar group for context.
- "Refresh" ("Muat ulang", "Muat data terbaru", "Perbarui …") is never a free-floating button in the page body: it is the header's secondary action (icon-button 44×44 with `aria-label`, or a secondary button) or the section header's action.
- Mobile (≤639 px): actions wrap under the description, full width if one primary, otherwise a row of equal-width buttons; the top bar keeps the page name, so the h1 stays (no duplication fix needed).

## 5. Section and card (`Section`, `Card`)

- `Section`: `<section aria-labelledby>`; `--card` background, 1 px `--border`, `--radius-card`, padding 24 px (16 px ≤639 px). Header row: h2 (20/600) left, 0–2 actions right (secondary or tertiary buttons only, never a primary unless the section *is* the page's single task), optional one-line description (13–15 px muted) under the title; header → body 16 px.
- Gap between sections: 24 px. Section groups (e.g. "Menunggu tindakan Anda" vs "Ruang kerja tersedia") are separated by 32 px and may drop the card surface (title directly on canvas) when they hold their own cards.
- **One level of boxing only.** A bordered card never sits inside another bordered card. Inside a section, structure comes from dividers (1 px `--border` between rows), spacing, or an inset panel (`--muted` background, no border, radius 8, padding 16). Today orders, incoming requests, history, product page (3 levels on mobile) and delegations nest cards.
- `Card` (a standalone tile such as a product card or KPI): same surface tokens, padding 16 px, radius 12; whole-card link gets hover `border-color: color-mix(in oklab, var(--primary) 45%, var(--border))`; no shadow at rest.
- Two-column work desks (queue left, detail right): queue column min 320 px, detail 1fr, gap 24 px; collapse to one column below 1024 px (queue first, detail after selection).

## 6. Lists and tables

### 6.1 `Toolbar`

Sits directly above the list/table it controls, inside the same `Section` (or directly under `PageHeader` when the list is the page).
- Left: search field (min 240, max 360 px, `--control-h`, leading search icon) then filters (selects or chips). Right: sort select, view toggle (segmented control), bulk actions.
- Gap 12 px; wraps on narrow screens with search full-width first.
- Below it: a result line in 13 px muted ("6 kemasan" / "Menampilkan 1–25 dari 120"), 12 px gap, never including the viewer's name.
- Controls never truncate their own label: sort select width = longest option + 40 px (today "Terbaru ditambahkan" is clipped at 1280 and 390).

### 6.2 `DataTable` (dense, comparable records; staff desktop)

- Wrapper: `--card`, 1 px `--border`, `--radius-card`, `overflow-x: auto`. Built on `.list-table` in `foundation.css:112–131` (keep; it is the right base).
- Header: `--muted` background, 13 px/600 `--muted-foreground`, padding 12 px 16 px, sticky top, sentence case.
- Rows: min-height `--row-h` (48), cell padding 12 px 16 px, 14 px text, `vertical-align: middle`, 1 px `--border` between rows, hover `--secondary`. Compact density (`data-density="compact"`): `--row-h-compact` 36, padding 6 px 12 px, row buttons `--control-h-compact`.
- First column: record number (`PSN-…`) as a link (600), opens the record. Numbers right-aligned, tabular. Status column uses `Badge`. Actions column right-aligned, last; ≤2 inline actions (tertiary buttons), more go into a "⋯" menu.
- Pagination below, right-aligned: "1–25 dari 120" + previous/next icon buttons (44, 36 compact). Hidden when everything fits on one page or the list is empty.
- Mobile ≤639 px: rows become stacked label/value cards (existing `.list-table` behaviour, `foundation.css:123–131`): row padding 16 px, label 12 px muted left, value right; first cell is the title (15/600).
- Raw `<table>` styled by `.page table` (`access.module.css:113–116`) is retired.

### 6.3 `DataList` (queues, orders, requests; storefront and touch)

- One `Section` containing a `<ul>` of rows separated by 1 px dividers — **not** one bordered card per row.
- Row padding 16 px 0 (first row 0 top), grid: `title + badge` line (15/600 title left, `Badge` right), meta line (13 px muted, 4 px below), optional content (line items, 8 px gap), action row (right-aligned on desktop, full-width stacked on mobile, 12 px above).
- Selected row (in a desk): `--secondary` background, `inset 3px 0 0 var(--primary)` marker (same as sidebar `aria-current`), not a coloured border box.
- Whole row clickable when it has one destination; then no separate "Lihat PB-…" button.

### 6.4 `Facts` (description lists)

Two columns ≥640 px (`minmax(0,1fr)` each, gap 16 px 24 px), one below. `dt` 13 px muted, `dd` 15 px/600, 4 px between. Replaces `.review-panel dl`, `.facts` (`access.module.css:97`), `.meta` (`branch-request.module.css:33`) and the health facts.

### 6.5 `Badge` (StatusBadge)

- Height 24 px, padding 0 8px, `--radius-badge` 6 px, 12/16 px 600, `white-space: nowrap`, tone pair from `--tone-*`. Optional 6 px dot or 14 px icon before the text (so status is never colour-only).
- Text from one status dictionary (state → label + tone); sentence case.
- Placement: right of the row title in lists, in its own column in tables, next to the h1 on record pages (`PageHeader.meta`).
- Retires `.status` (`access.module.css:47`), `.badge` (`branch-request.module.css:20`, `stock-source.module.css:18`) and the health tags.

## 7. Forms (`Field`)

- Form column: `--content-narrow` (720 px) max; fields stack with 16 px gap; groups (`fieldset`) separated by 24 px with an h3/legend (16/600) and optional 13 px description.
- Two fields per row only for short related pairs (name + phone, date + time) at ≥768 px, gap 16 px.
- `Field` anatomy: label (14/600) → 8 px → control → 4 px → help text (13 px muted) or error (13 px/600 `--tone-danger-foreground`, with icon, `aria-describedby`). Optional marker "(opsional)" in 13/400 muted after the label; required is the default.
- Control: height `--control-h` 44 px (textarea min 96 px, `resize: vertical`), padding 0 12px (textarea 12 px), 15 px text, `--card` background, 1 px `--control-border`, `--radius-control` 8 px; hover border `--muted-foreground`; focus: global ring (section 10); invalid: border `--destructive` + error text; disabled: `--muted` background, `--muted-foreground` text. Selects use the same box with a 16 px chevron, padding-right 36 px.
- Widths: text inputs full width of the 720 column; numbers/quantities 160 px; dates 200 px; rupiah 240 px; search 240–360 px.
- Actions: after the last field, 24 px gap, left-aligned on the form's left edge: primary, then secondary, then tertiary link (gap 8 px). Mobile: full-width stacked, primary on top. A destructive confirm sits in the `ReviewPanel`, not in the form.
- Never put a control directly under a paragraph or a button directly against a control: the 16/24 px gaps above are mandatory (today invoices' "Tinjau invoice" touches its textarea and delegations' "Perbarui sesi dan akses" touches the next card).
- Checkbox/radio: 20 px box, `accent-color: var(--primary)`, label 15/400 with 8 px gap, row min-height 44 px.

## 8. Buttons (`Button`)

| Variant | Look | Use |
|---|---|---|
| Primary | `--primary` fill, `--primary-foreground` text; hover `--primary-hover` | The one main action of a page or region (Ajukan, Simpan, Masuk, Tambah ke keranjang). Max one per region. |
| Secondary | `--card` fill, 1 px `--control-border`, `--foreground` text; hover `--secondary` fill | Alternatives (Batal, Lihat rincian, Muat ulang, Periksa kembali). |
| Tertiary (ghost/link) | No border, `--primary` text; hover `--secondary` fill | Low-emphasis and row actions, "Kembali ke…", "Lihat semua". |
| Danger | Secondary shape with `--destructive` text and border to start; filled `--destructive` for the final confirm | Tolak, Batalkan pesanan, Cabut. |

- Size: height exactly `--control-h` 44 px (`height`, not `min-height` + padding: the legacy `.primary` renders 48 px because of 10 px padding + 1.6 line-height, `access.module.css:21`); compact 36 px in tables/toolbars; icon-only 44×44 (36×36 compact) with `aria-label`. Large 48 px only for the mobile sticky checkout bar.
- Padding 0 16px (compact 0 12px), gap 8 px to a 16–18 px icon, 14/600 text, `--radius-control` 8 px, no text wrapping (`white-space: nowrap`; long labels get shortened copy).
- Disabled: `--muted` fill, `--muted-foreground` text, no border for primary; secondary keeps its border at `--border`. No `opacity` dimming (today `.page button:disabled{opacity:.55}`, `access.module.css:11`). Busy: spinner 16 px replaces the icon, label stays, `aria-busy`.
- Group: 8 px gap; order primary → secondary → tertiary in forms (left-aligned) and secondary → primary in headers/dialog footers (right-aligned, primary outermost right).
- One shared implementation: extend `frontend/src/components/ui/button.tsx` (cva) with these variants and sizes; retire `.primary/.secondary/.danger` in `access.module.css:21–27`, `.review-confirm/.review-cancel`, `.outcome-action`, `.list-table-open`, `.stateAction`, `.signIn` and module-local button styles.

### 8.1 Tabs, chips, segmented controls

- `Tabs` (switching views of one page: "Buat permintaan / Riwayat permintaan"): text tabs 44 px tall, 15/600, `--muted-foreground`, current = `--foreground` + 2 px `--primary` bottom border; on a 1 px `--border` baseline; gap 24 px; horizontally scrollable on mobile. Links to *other pages* ("Permintaan masuk") are not tabs. Today these are buttons, and the current tab is the filled primary, which reads as a call to action.
- Filter chips: pill, 36 px tall (inside a 44 px hit area via padding), 14/600, `--card` + 1 px `--border`; selected `--primary` fill and `--primary-foreground` text, count in 12/600 at full contrast (today the count drops to 4.32:1 through `opacity:.85`, `public-catalog.module.css:20`).
- Segmented control (Kisi/Daftar): 44 px, radius 8, selected segment `--secondary` fill + `--secondary-foreground` text (not the primary fill).

## 9. States (`StateView`)

Every data region (page or section) renders exactly one of: loading, empty, error, denied, unknown/conflict, content. Never two at once (today invoices and delegations show "Layanan belum tersedia" **and** "Belum ada …").

| State | Where | Look | Copy pattern | Action |
|---|---|---|---|---|
| Loading (first load) | In place of the region's content | Skeleton matching the layout (table rows, list rows, cards), `--muted` bars 12–16 px tall, radius 6; shimmer off with reduced motion (`.outcome-skeleton`, `foundation.css:108`). Never a blank page or "Memuat…" text alone. | – (visually hidden "Memuat …" for screen readers) | – |
| Loading (refresh) | Region stays visible | 2 px progress bar at the top of the section or busy state on the refresh button. | – | – |
| Empty | Inside the section, left-aligned on the section's left edge | No border (the section already has one), padding 24 px 0; 24 px icon in `--muted-foreground`, h3 16/600, one line 15 px muted (max 72ch), optional secondary button 12 px below. Storefront full-page empty (cart, no search results): centred block, 48 px vertical padding, 40 px icon, primary action. | Title says what is empty ("Belum ada invoice"), line says why/what next. | Optional: create / clear filters / go to catalogue. |
| Error (region) | Replaces the region's content | `--tone-danger` surface, no border, radius 12, padding 16 px 24 px; 20 px alert icon, title 15/600 `--tone-danger-foreground`, one line of reason, trace code in 13 px muted if available. Full width of the section. | What failed + what happened to the user's data + what to do. | Secondary "Coba lagi" (44 px). |
| Error (page) | Below `PageHeader`, instead of all sections | Same panel as region error at page width; sections not rendered. | As above. | "Coba lagi" + tertiary "Ke Pekerjaan saya". |
| Error (field) | Under the control | Section 7. | – | – |
| Denied | Instead of all sections | Neutral: `--card` section, padding 32 px, 24 px lock icon `--muted-foreground`, h2 20/600, one paragraph; **not** danger-coloured. | "Halaman ini di luar tugas akun Anda" + who can grant access. | Primary "Ke Pekerjaan saya", secondary "Periksa ulang akses". |
| Unknown outcome / conflict | Where the action's result would appear | `--tone-warning` panel, same geometry as error. | "Hasil belum dapat dipastikan…" + the safe next step. | Secondary "Periksa hasil". |
| Success | After a submit | Toast (`sonner`) for routine saves; inline `--tone-success` panel (same geometry) when the user must read a result (e.g. submitted for approval, record number). | Record number + next step. | Optional link to the record. |
| Offline | Top of the page | `--tone-info` panel. | – | – |

Implementation: one `StateView` (or the existing `OutcomeState` + `EmptyState` extended to these kinds) used by every page; retire `common.alert`, `common.state`, `common.empty`, `.empty-state` duplicate (`foundation.css:27` and `:165` define it twice with opposite alignment) and the unused `.error-message` (`foundation.css:47`).

## 10. Interaction and accessibility

- Focus: one rule, `foundation.css:12` (3 px `--ring`, offset 3 px; 2 px offset inside tables). Remove the 22 module-level `outline` declarations (orange `#c84513`/`#ed7829`/`#b96d26`, ink `#073b45`, etc.).
- Touch targets ≥44×44 px for every interactive element on ≤1023 px, including sidebar/menu items (today 38 px, `foundation.css:62`), breadcrumb links (21 px tall), inline text links in help copy (pad them or give the row 44 px).
- Contrast: text ≥4.5:1, control boundaries and focus ≥3:1 (`--control-border` above). Status never by colour alone (badge text/icon).
- Landmarks: all page content inside `header`/`nav`/`main`/`footer`; the store utility bar becomes part of `<header>`; `#konten-utama` is the `<main>` element itself (today a stray span sits outside `main`).
- Motion: 120–160 ms ease for hover/press; respect `prefers-reduced-motion` (already in `foundation.css:13`).

## 11. Mobile rules (≤639 px; tablet 640–1023 follows desktop layout with the workspace top bar)

1. Gutter 16 px on every page (today 16/20/22/36 px depending on the module).
2. One column. Two-column desks, forms and facts collapse; the action panel/summary comes after the content, except the storefront checkout/cart summary which becomes a sticky bottom bar (total + primary 48 px) above the tab bar.
3. h1 24/30, h2 18/26; purpose line 15 px (do not shrink below 13 px anywhere).
4. Sections: padding 16 px, radius 12, still one level only; full-bleed images inside cards are allowed.
5. Action rows: one action → full width; two or more → stacked full-width, primary first. Header actions follow the same rule. No mix of auto-width and full-width buttons in the same view.
6. Tables become stacked cards (6.2); only genuinely wide numeric tables (e.g. health queue table) may scroll horizontally, inside the table wrapper, with the first column sticky and a visible scroll hint (fade on the right edge).
7. Catalogue: 2 product columns, 12 px gap, product image 1:1, name 2 lines max.
8. No horizontal page scroll at 360 px (all 68 mocked captures pass at 390 today; keep it).
9. Storefront bottom tabs: 56 px + safe area, 4 items, label 12/600; page bottom padding = tab height + 16 px so the last content is never hidden.
10. Sticky elements: top bar 56 px; `scroll-padding-top` = top bar + 16 px (`foundation.css:162` has 72 px).

## 12. Primitive contracts (summary for UI-SYSTEM)

| Primitive | Props (minimum) | Uses tokens | Replaces |
|---|---|---|---|
| `PageHeader` | `title, description?, actions?, breadcrumb?, tabs?, meta?` | type h1, `--space-2/6`, `--muted-foreground` | `common.header` (32 files), `styles.header` (33), `styles.heading` (14), `accountHeader`, `storeHeading`, `top`, inventory `.eyebrow` |
| `Section` | `title, description?, actions?, children, variant: 'card' \| 'plain'` | `--card`, `--border`, `--radius-card`, `--space-4/6` | `common.accountSection`, `.queue`, `.detail`, `.form` panels, `branch-request .section`, `cart .lines/.summary` frames |
| `Toolbar` | `search?, filters?, sort?, view?, actions?, resultText?` | `--control-h`, `--space-3` | catalogue toolbar, `.lookup`, ad-hoc refresh rows |
| `Field` | `label, help?, error?, optional?, children (control), width?` | label/help type, `--control-h`, `--control-border`, `--radius-control` | `.page label/input` globals in `access.module.css:38–43`, `cart .branch`, `.sort select`, `RupiahInput` wrapper (keep logic) |
| `DataTable` | `columns (align, width), rows, density, caption, pagination?, empty, error, loading` | `--row-h*`, `--muted`, `--border` | `ListTable` (extend), `.page table` |
| `DataList` | `items, renderTitle, renderMeta, badge, actions, selectedId?, onSelect?` | dividers, `--space-3/4` | `common.queueList/.queueRow/.selectedRow`, `branch-request .list/.item`, orders cards |
| `Facts` | `items: {term, value}[] , columns?` | 6.4 | `.facts`, `.review-panel dl`, `.meta` |
| `Badge` | `status` (dictionary key) or `tone + label` | `--tone-*`, `--radius-badge` | `.status-badge` (keep name), `.status`, `.badge` variants |
| `Button` | `variant: primary\|secondary\|tertiary\|danger, size: default\|compact\|icon, busy?` | 8. | `.primary/.secondary/.danger`, `.review-confirm`, `.outcome-action`, `.list-table-open`, `.stateAction`, `.signIn` |
| `Tabs` | `items: {label, href \| value}[], current` | 8.1 | button-tabs on request pages, `.administrationNav` |
| `StateView` | `kind: loading\|empty\|error\|denied\|unknown\|success\|offline, title, message, action?` | 9. | `common.alert/.state/.empty/.success`, duplicate `.empty-state`, `.error-message`, `buyer .accessState/.empty` |

## 13. Legacy token map (for the migration)

| Legacy | Use instead |
|---|---|
| `--workspace-ink` #073b45 (text) | `--foreground` |
| `--workspace-ink` (primary button fill) | `--primary` |
| `--workspace-canvas` #f2f6f4, buyer `#fff` page background | `--background` |
| `--workspace-surface` | `--card` |
| `--workspace-surface-selected/-hover/-review` | `--secondary` (selected, hover), `--muted` (inset panel) |
| `--workspace-text-muted/-meta/-caption/-row/-support/-placeholder` (6 near-identical greys) | `--muted-foreground` |
| `--workspace-border/-divider/-row/-list/-history/-support` | `--border` |
| `--workspace-border-interactive/-input` | `--control-border` |
| `--workspace-accent` #c84513 (focus, markers) | `--ring` / `--primary` |
| `--workspace-critical-*`, `--workspace-alert-*` | `--tone-danger`, `--tone-danger-foreground`, `--destructive` |
| `--workspace-success-*` | `--tone-success*` |
| `--workspace-caution-*` | `--tone-warning*` |
| Module hex (e.g. `#087f8c`, `#126a74`, `#23796f`, `#526c70`, `#d7e3e7`) | nearest semantic token above |

## 14. Acceptance checks (what QA measures)

At 1280×800 and 390×844, with the DOM-metrics script `tmp/website-ready/ux/measure.cjs`:
1. Every page's h1 is 28 px/700 (24 px at 390) and sits at the frame's single left edge (storefront x = gutter; workspace x = 264 + gutter).
2. All buttons and fields measure 44 px tall (36 px only inside compact tables); all button radii 8 px; all section radii 12 px.
3. Rendered font sizes ⊆ {12, 13, 14, 15, 16, 18, 20, 24, 28} px (+ landing display); weights ⊆ {400, 600, 700}.
4. No bordered element inside another bordered card (except fields, buttons and badges).
5. No region shows an error and an empty message at the same time; pagination hidden when empty.
6. axe: 0 `color-contrast`, `target-size`, `region` violations.
7. CSS lint: no hex colour, no px spacing off the scale, no `outline` outside `foundation.css`, no `border-*: [2-9]px solid` stripes, in any module stylesheet.
