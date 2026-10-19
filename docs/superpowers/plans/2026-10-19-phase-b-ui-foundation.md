# Phase B: UI Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every screen one cascade, one token set, one font, one formatter and one status vocabulary, so the later shell and pattern work (Phase C) builds on a consistent, WCAG 2.2 AA base.

**Architecture:**
- The 24 version-numbered global stylesheets move into a single `legacy` cascade layer that ranks below Tailwind's `components` and `utilities`, with their `!important` rules removed.
- Semantic tokens live in `globals.css` as OKLCH, and a node test checks their contrast.
- Money, number, date and time formatting moves into `src/lib/format.ts`.
- Status badges take their tone from a label→tone dictionary instead of CSS keyed on Indonesian label text.

**Tech Stack:** Next.js 16.3 (webpack build), React 19.2, Tailwind 4.2 (`@tailwindcss/postcss`), shadcn ui components, the node test runner (`npm test` bundles `tests/*.test.ts` with esbuild), and Playwright from `UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE`.

**Spec:** `docs/superpowers/specs/2026-10-19-production-completion-program.md` (Phase B row). Design source: `reports/Unit Toko UI UX improvements.md`, roadmap phases 0–1 and the "Twenty-four unlayered stylesheets" and "Indonesian conventions" sections.

## Global Constraints

- Bahasa Indonesia, "Anda" register. EYD V money format `Rp1.250.000`: no space after Rp, dot thousands, comma decimals. Times as `14.30 WIB`. Time zone Asia/Jakarta.
- WCAG 2.2 AA: text contrast ≥4.5:1, large text and UI/focus ≥3:1, targets ≥24 px.
- Add no new npm dependency. Downloads need owner approval. Playwright is already present in the runtime.
- Release 1 (`.next`) and full (`.next-full`) builds both keep working. `npm run typecheck`, `npm run lint` and `npm test` stay green.
- Stage explicit paths only. Never stage `docs/proposal-completion-plan.md`.

## Review Focus

1. **Rupiah from the API arrives as decimal strings** (for example `"189000.00"` or Decimal128 text). The formatter must accept strings without float drift. Covered by the Task 3 test `formatRupiah accepts decimal strings exactly`.
2. **A status label not in the dictionary** must render as a neutral, readable badge. It must never be uncoloured or invisible. Covered by the Task 4 test `unknown label falls back to neutral`.
3. **Legacy rules that relied on `!important` to beat other legacy rules** may lose once importance is removed. Task 2 makes the visual review compare every affected selector.
4. **Narrow screens:** after the cascade change, 360 px Android width must not scroll horizontally on public pages. Covered by the Task 1 harness assertion `no horizontal overflow at 360`.
5. **Server and client must render money identically:** formatting depends only on explicit options, never on ICU defaults. Covered by the Task 3 test, which pins fraction digits.

---

### Task 1: Visual and contrast safety net

**Files:**
- Create: `frontend/scripts/visual-baseline.mjs`
- Create: `frontend/tests/design-tokens.test.ts`
- Create: `frontend/src/lib/design/contrast.ts`

**Interfaces:**
- Produces: `contrastRatio(foreground: string, background: string): number`, which accepts `#rrggbb` and `oklch(L C H)`. Also `readTokens(css: string): Record<string, string>`, which parses `--name:value` pairs from the `:root` block of `globals.css`.

- [ ] **Step 1:** Write `design-tokens.test.ts`.
  - `contrastRatio('#000000','#ffffff')` ≈ 21 and `contrastRatio('#087f8c','#ffffff')` ≥ 4.5.
  - A table of required pairs, each read from `globals.css`:
    - foreground on background, card-foreground on card, muted-foreground on background and card, primary-foreground on primary, ring on background and card — the ring at ≥3, all others at ≥4.5;
    - each status tone's foreground on its background at ≥4.5 (tone tokens arrive in Task 3, so assert they exist);
    - the focus ring on background at ≥3.
- [ ] **Step 2:** Run `npm test` and confirm it fails. Expected failures: contrast for `--muted-foreground` (2.85–3.29) and the ring, plus missing tone tokens.
- [ ] **Step 3:** Implement `contrast.ts`, covering WCAG relative luminance and OKLCH→sRGB conversion with clamping.
- [ ] **Step 4:** Write `visual-baseline.mjs`.
  - It starts `next start` on the built distDir at port 3017 and visits `/`, `/shop`, `/catalog/browse` and `/identity/login`.
  - It takes full-page screenshots at widths 360, 768 and 1440 into `tmp/visual/<label>/`, where the label is a CLI argument.
  - It asserts `document.documentElement.scrollWidth <= innerWidth` at 360.
  - It writes a JSON summary.
- [ ] **Step 5:** Run `node scripts/visual-baseline.mjs before` on the current build. Expected: 12 screenshots and the summary, no overflow failures (record any as found). Keep the failing token test, which goes GREEN in Task 3, and commit.

### Task 2: Cascade quarantine

**Files:**
- Modify: `frontend/src/app/layout.tsx` (remove the 24 CSS imports and keep `./globals.css`)
- Modify: `frontend/src/app/globals.css` (header: `@layer theme, base, legacy, components, utilities;`, then `@import "./polish.css" layer(legacy);` and the rest in the current order)
- Modify: the legacy CSS files, removing `!important` (40 lines, about 64 declarations)
- Create: `frontend/tests/css-cascade.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: the guarantee that utilities and components outrank legacy CSS, which Tasks 3–4 rely on.

- [ ] **Step 1:** Write `css-cascade.test.ts`.
  - `layout.tsx` imports exactly one stylesheet.
  - `globals.css` declares the five-layer order before any rule.
  - Every former legacy file is imported with `layer(legacy)`.
  - No `!important` remains in `src/app/*.css` outside a comment naming an accessibility reason. The allowlist is the reduced-motion and `forced-colors` rules.
- [ ] **Step 2:** Run `npm test` and confirm it fails for each assertion.
- [ ] **Step 3: Spike.**
  - Build with the layered import.
  - In `.next/static/css/*.css`, check that `@layer theme,base,legacy,components,utilities` appears once and that the legacy rules sit inside `@layer legacy`.
  - If `@tailwindcss/postcss` reorders or flattens the layer, fall back to declaring the order in a dedicated first file. Record that ruling in the ledger.
- [ ] **Step 4:** Remove `!important`. Where a removal was making one legacy rule beat another, raise that rule's specificity or reorder it within its file.
- [ ] **Step 5:** Run `npm test`, `npm run typecheck`, `npm run lint`, the release-1 build and the full build (`NEXT_PUBLIC_UNIT_TOKO_RELEASE=full`). Expected: all pass.
- [ ] **Step 6:** Run `node scripts/visual-baseline.mjs layered` and compare against `before`. Every visible difference must be explained, and the explanations go in the ledger. Commit.

### Task 3: Tokens, font and formatter

**Files:**
- Modify: `frontend/src/app/globals.css` `:root` and `@theme inline`
- Create: `frontend/src/lib/format.ts`
- Create: `frontend/tests/format.test.ts`

**Interfaces:**
- Produces:
  - `formatRupiah(value: number | string | bigint, opts?: { decimals?: 0 | 2 }): string`
  - `formatNumber(value: number | string, fractionDigits?: number): string`
  - `formatPercent(ratio: number, fractionDigits?: number): string`
  - `formatDate(value: Date | string, style?: 'short' | 'long'): string`
  - `formatDateTime(value: Date | string): string`
  - `formatTime(value: Date | string): string`

- [ ] **Step 1:** Write `format.test.ts` with these exact expectations:

| Call | Expected |
| --- | --- |
| `formatRupiah(1250000)` | `Rp1.250.000` |
| `formatRupiah('189000.00')` | `Rp189.000` |
| `formatRupiah('1250000.5', {decimals:2})` | `Rp1.250.000,50` |
| `formatRupiah(-25000)` | `-Rp25.000` |
| `formatNumber(1234567)` | `1.234.567` |
| `formatPercent(0.873,1)` | `87,3%` |
| `formatDate('2026-10-17T07:30:00Z')` | `17 Okt 2026` |
| `formatDate(…,'long')` | `Sabtu, 17 Oktober 2026` |
| `formatDateTime('2026-10-17T07:30:00Z')` | `17 Okt 2026 14.30 WIB` |
| `formatTime(…)` | `14.30 WIB` |

  - Test `formatRupiah accepts decimal strings exactly`: `formatRupiah('9007199254740993')` gives `Rp9.007.199.254.740.993`.
  - Output must contain no U+00A0 or U+202F characters.
- [ ] **Step 2:** Run `npm test` and confirm it fails because the module is missing.
- [ ] **Step 3:** Implement `format.ts`.
  - Use integer and string arithmetic for the rupiah grouping (BigInt for the whole part).
  - Use `Intl.DateTimeFormat('id-ID', { timeZone: 'Asia/Jakarta' })` parts for dates.
  - Normalise any NBSP to nothing (Rp) or a space (date).
- [ ] **Step 4:** Update the tokens.
  - OKLCH values must keep the teal brand and meet every pair in Task 1. Raise `--muted-foreground` to ≥4.5. Change `--ring` to a ≥3:1 colour and keep the orange `#ed7829` only as a non-text accent.
  - Add status tones: `--tone-{neutral,info,warning,success,danger}` and their `-foreground` pairs, each mapped in `@theme inline`.
  - Set `--font-sans: var(--font-body), system-ui, sans-serif` so Segoe UI and Trebuchet MS are no longer forced.
  - Add a `.tabular-nums`-backed `money` utility under `@layer utilities`.
- [ ] **Step 5:** Run `npm test` and expect `format.test.ts` and `design-tokens.test.ts` to pass. Commit.

### Task 4: Status dictionary and formatter adoption

**Files:**
- Create: `frontend/src/lib/status/dictionary.ts` and `frontend/tests/status-dictionary.test.ts`
- Modify: `frontend/src/components/workspace.tsx:14` and `frontend/src/components/order-experience.tsx:26`, which set `data-tone` from the dictionary
- Modify: `globals.css` and `polish.css`, replacing the `[data-status=…]` selectors with one `.status-badge[data-tone]` block in `@layer components`
- Modify: the 48 files that use `toLocaleString`/`new Intl.NumberFormat` (`grep -rln "Intl.NumberFormat\|toLocaleString" frontend/src`), switching them to the `format.ts` functions
- Create: `frontend/tests/format-usage.test.ts`

**Interfaces:**
- Consumes: Task 3 `format.ts` and the tone tokens.
- Produces:
  - `type StatusTone = 'neutral' | 'info' | 'warning' | 'success' | 'danger'`
  - `statusTone(label: string): StatusTone`
  - `STATUS_LABELS: ReadonlyMap<string, StatusTone>`

- [ ] **Step 1:** Write the tests.
  - Every label currently styled by `[data-status=…]` maps to the tone its colour meant: Lunas, Terverifikasi, Terpenuhi, Diterima, Disetujui, Transaksi final → success; Menunggu tinjauan, persetujuan, verifikasi → warning; Diproses, Siap dikirim, Dalam perjalanan → info; Ditolak, Gagal dikirim, Lewat jatuh tempo → danger.
  - Test `unknown label falls back to neutral`.
  - `format-usage.test.ts` scans `src/` and finds zero `toLocaleString(` or `new Intl.NumberFormat`/`DateTimeFormat` outside `src/lib/format.ts`.
- [ ] **Step 2:** Run `npm test` and confirm it fails.
- [ ] **Step 3:** Implement the dictionary and the badge `data-tone`. Then migrate the call sites file by file, money to `formatRupiah` and timestamps to `formatDateTime`/`formatDate`, keeping each screen's existing meaning (date-only vs date-time).
- [ ] **Step 4:** Run `npm test`, typecheck, lint and both builds. Then rerun `node scripts/visual-baseline.mjs final`. Expected: all green, and badges coloured by tone with contrast ≥4.5.
- [ ] **Step 5: Workspace evidence.** Rerun the browser tests whose verify scripts screenshot the status-heavy workspaces (`OrderViewBrowserTests`, `PaymentVerificationBrowserTests`, `InvoiceBrowserTests`) in one batch. Compare their new screenshots with the existing ones under `tmp/production`. Commit.

### Task 5: Close Phase B

- [ ] Update the "Q02/U08" rows in `docs/quality/production-progress.md` with the evidence and the ledger (`.superpowers/sdd/<plan>/progress.md`).
- [ ] Final review by the most capable model. Fix Critical and Important findings RED→GREEN.
- [ ] Commit explicit paths, push, and check the Vercel deployment.
