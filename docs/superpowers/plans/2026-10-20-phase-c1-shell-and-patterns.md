# Phase C1: Workspace Shell and Shared Patterns Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every staff workspace gets the same persistent navigation, quick switching and a shared set of state and list components. The busiest screens stop making staff click "Muat …" before they can see anything.

**Architecture:** `AdministrationNavigation`, already rendered by 33 workspaces with an unchanged prop API, becomes a grouped sidebar (a sheet on phones) built from the live permission discovery `identityDestinations` and the existing `workspaceDirectory` areas. A Ctrl/⌘+K palette uses the same destination list. New primitives in `src/components/workspace-kit/` (OutcomeState, ListTable, ReviewPanel) carry the research report's rules. They are adopted first on the payment-review queue and detail and on "Pesanan saya".

**Tech Stack:** Next.js 16.3, React 19.2, shadcn `dialog`/`sheet` on Radix, Tailwind 4 with the Phase B tokens and layers, and the node test runner with esbuild bundles.

**Spec:** `docs/superpowers/specs/2026-10-19-production-completion-program.md` (Phase C row). Design: `reports/Unit Toko UI UX improvements.md` sections "Tables and queues", "The review panel is the confirmation", "Unknown outcomes need their own state", "Navigation by domain, homes by role".

## Global Constraints

- Navigation is presentation only. Visibility comes from `identityDestinations(actions)` plus the pilot allowlist (`isPilotPathAllowed`), and every API still authorises independently. Never show a destination the discovery hides, except the current page, as today.
- `locked` keeps blocking navigation during an in-flight command, as today (`aria-disabled` plus `preventDefault`).
- Copy is Bahasa Indonesia, "Anda", verb-first. Targets are ≥24 px; the sidebar uses ≥40 px rows. Focus stays visible. Respect reduced motion.
- No new npm dependency.
- An unknown outcome is never labelled "Gagal". It offers "Periksa hasil", which reuses the original idempotency key through the existing recovery functions.
- Stage explicit paths only.

## Review Focus

1. **Locked navigation:** with `locked` true, sidebar links, palette results and the mobile sheet links must not navigate. Covered by test `locked navigation never navigates` (Task 1).
2. **Pilot release:** sidebar and palette must hide non-pilot paths even when an action grants them. Covered by test `pilot release hides non-pilot destinations` (Task 1).
3. **Current page without permission:** a page whose permission was just revoked still shows as the current item, but marked "tidak tersedia". Covered by Task 1.
4. **Auto-loaded detail panels:** when the user switches item mid-load, a late reply for the previous item is discarded. Covered by test `late panel reply for a previous selection is ignored` (Task 4).
5. **360 px width:** the sidebar collapses to a sheet with no horizontal overflow, checked by the visual-baseline overflow assertion on a workspace route through the browser test screenshots (Task 5).

---

### Task 1: Grouped navigation model and sidebar

**Files:**
- Create: `frontend/src/modules/identity/workspace-navigation.ts`
- Create: `frontend/tests/workspace-navigation.test.ts`
- Modify: `frontend/src/modules/administration/AdministrationNavigation.tsx` (render the sidebar; props unchanged)
- Modify: `frontend/src/app/foundation.css` (`@layer components`: `.workspace-sidebar`, plus the body offset `body:has(.workspace-sidebar)` at ≥1024 px)

**Interfaces:**
- Produces: `workspaceNavigation(actions: readonly string[], current: string | null): Array<{ id: string; label: string; items: Array<{ href: string; label: string; description: string; current: boolean; available: boolean }> }>`.
  - Areas follow the `workspaceDirectory` order.
  - "Pekerjaan saya" (`/identity/account`) and "Akun saya" always appear.
  - `current` is a pathname.
- Produces: `AdministrationNavigation` with the same props as today.

- [ ] **Step 1:** Write tests.
  - Grouping matches `workspaceDirectory` areas for a fixture action set.
  - The current path is always listed (`available: false` when not permitted).
  - Test `pilot release hides non-pilot destinations`, using a flag injected through a parameter so the test can toggle it.
  - Test `locked navigation never navigates`: a pure helper `navigationClick(locked)` returns `preventDefault`.
- [ ] **Step 2:** Run the bundled test and expect FAIL (module missing).
- [ ] **Step 3:** Implement `workspaceNavigation`. Map each existing `current` id (for example `"payment-reviews"`) to its href through the table already in `AdministrationNavigation`.
- [ ] **Step 4:** Rewrite the `AdministrationNavigation` render.
  - Desktop (≥1024 px): an `<aside className="workspace-sidebar" aria-label="Navigasi ruang kerja">` with brand, area headings (`<h2>`), links (`aria-current="page"`) and an "Akun saya" footer.
  - Below 1024 px: a top "Menu" button that opens shadcn `Sheet` with the same list.
  - `locked` disables links as today.
- [ ] **Step 5:** Run tests, typecheck and lint, then commit.

### Task 2: Quick switch palette (Ctrl/⌘+K)

**Files:**
- Create: `frontend/src/components/workspace-kit/command-palette.tsx`, `frontend/src/components/workspace-kit/palette-search.ts`, `frontend/tests/palette-search.test.ts`
- Modify: `AdministrationNavigation.tsx` (mount the palette and add a "Cari cepat (Ctrl K)" sidebar button)

**Interfaces:**
- Produces: `paletteSearch(groups: ReturnType<typeof workspaceNavigation>, query: string): Array<{ href: string; label: string; area: string }>`. Matching is case- and diacritic-insensitive over label, description and area, available items only, ranked with label prefix first. Also `<CommandPalette groups locked />`.

- [ ] **Step 1:** Test `paletteSearch`: "bayar" finds "Bukti pembayaran" and "Pemeriksaan pembayaran", the empty query lists all available items, and unavailable items are excluded.
- [ ] **Step 2:** Confirm RED, then implement.
- [ ] **Step 3:** Build the palette on shadcn `Dialog`.
  - An input with a `role="combobox"` listbox, ↑/↓/Enter, and Esc to close.
  - Ctrl+K or ⌘+K opens it, but not while typing in an input or textarea.
  - The empty-result copy is "Tidak ada ruang kerja yang cocok."
- [ ] **Step 4:** Run tests, typecheck and lint, then commit.

### Task 3: Shared state, list and review primitives

**Files:**
- Create: `frontend/src/components/workspace-kit/outcome-state.tsx`, `list-table.tsx`, `review-panel.tsx`, `outcome-copy.ts`
- Create: `frontend/tests/outcome-copy.test.ts`
- Modify: `foundation.css` (`@layer components` styles for the kit, using tokens only)

**Interfaces:**
- Produces: `type OutcomeKind = 'loading' | 'empty-first' | 'empty-filtered' | 'denied' | 'offline' | 'conflict' | 'unknown' | 'error'` and `outcomeCopy(kind): { title: string; body: string; action?: string }`.
- Produces: `<OutcomeState kind onAction? />`. `loading` renders skeleton rows (`aria-busy`). `unknown` uses the action "Periksa hasil".
- Produces: `<ListTable columns rows getKey onOpen density? caption />`, where columns are `{ key, header, align?: 'start' | 'end', render }`. It has a sticky header and right-aligned `end` columns with the `numeric` class. Below 640 px it renders cards, with the first column as the title.
- Produces: `<ReviewPanel title items={[{label, value, changeHref?}]} note? actionLabel onConfirm onCancel selfApprovalBlocked? stale? />`. The confirm button names the action. A self-approval block shows "Anda tidak dapat menyetujui pengajuan Anda sendiri." and disables confirm. `stale` shows "Data berubah sejak Anda membuka tinjauan ini. Muat ulang sebelum memutuskan." and disables confirm.

- [ ] **Step 1:** Test `outcomeCopy` exact strings:

| Kind | Title |
| --- | --- |
| unknown | Hasil belum pasti |
| offline | Anda sedang luring |
| denied | Akses tidak tersedia |
| conflict | Data sudah berubah |
| empty-filtered | Tidak ada yang cocok dengan filter |

  - The `unknown` body must not contain "Gagal".
  - The `unknown` action is "Periksa hasil".
- [ ] **Step 2:** Confirm RED, then implement the copy and components.
- [ ] **Step 3:** Run tests, typecheck and lint, then commit.

### Task 4: Adopt on the payment review workspace and order views

**Files:**
- Modify: `frontend/src/modules/billing/PaymentReviewWorkspace.tsx` and its detail panels (`PaymentOcrPanel.tsx`, `PaymentDuplicatePanel.tsx`, and the status and verification sections)
- Modify: `frontend/src/modules/ordering/OrderHistoryWorkspace.tsx` (the list as `ListTable`)
- Test: `frontend/tests/payment-review-autoload.test.ts`

**Interfaces:**
- Consumes: Task 3 components.
- Produces: `useAutoLoad<T>(key: string | null, load: (signal: AbortSignal) => Promise<T>): { state: 'idle' | 'loading' | 'ready' | 'error'; value?: T; error?: unknown; reload(): void }`, in `src/components/workspace-kit/use-auto-load.ts`. It aborts the previous load and ignores late replies when `key` changes.

- [ ] **Step 1:** Write `payment-review-autoload.test.ts` against `useAutoLoad` with a fake React hook harness, matching the existing mounted-test pattern.
  - Loading starts when a key is set.
  - Test `late panel reply for a previous selection is ignored`.
  - `reload` refetches.
- [ ] **Step 2:** Confirm RED, then implement `useAutoLoad`.
- [ ] **Step 3:** Change the payment review workspace.
  - The queue renders as a `ListTable`: pembayar, cabang, jumlah (end), diajukan, usia, status badge.
  - Opening an item auto-loads the OCR, duplicate, status and verification sections with `OutcomeState` loading skeletons. The former "Muat …" buttons become a secondary "Muat ulang" icon button per section.
  - Existing gateways and authorisation are unchanged.
- [ ] **Step 4:** "Pesanan saya" lists orders in a `ListTable`: nomor, cabang/divisi, tanggal, jumlah kemasan, status, total (end).
- [ ] **Step 5:** Run frontend tests, typecheck, lint and both builds, then commit.

### Task 5: Evidence and close

- [ ] Rerun the browser tests whose verify scripts cover these screens in one batch: `PaymentVerificationBrowserTests`, `PaymentHandoverBrowserTests` and `OrderViewBrowserTests`. Update a verify script only where a selector named a removed "Muat …" button, and keep each script's assertions about the data. Compare screenshots at 1440 and 360.
- [ ] Update the U01–U04 rows in the register, ledger, final review (Critical/Important fixed RED→GREEN), commit explicit paths and push.
