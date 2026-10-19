# Phase C3: Rupiah Input, Shell Accessibility and Field UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the highest-value remaining items of the UI/UX research roadmap:
- people type rupiah the Indonesian way;
- the shell meets WCAG 2.2 navigation rules;
- empty and finished states read clearly;
- help is one tap away;
- the review's deferred accessibility minors are fixed.

**Architecture:** The work is pure frontend and builds on the Phase B/C kit (`src/lib/format.ts`, `components/workspace-kit`).
- A tolerant parser turns typed text into the API's canonical decimal string.
- A `RupiahInput` shows the formatted value the server will receive.
- Shell additions live in `AdministrationNavigation.tsx` and `foundation.css`.

**Tech Stack:** Next.js 16, React 19, Tailwind 4 layers, `node:test` with esbuild bundles, Playwright verify scripts.

**Spec:** `reports/Unit Toko UI UX improvements.md`, items 2.1 (skip link, landmarks, scroll-padding, help position), 2.5 (CurrencyInput, error summary), 2.6 (EmptyState), 4.4 (WhatsApp links); and `docs/superpowers/specs/2026-10-19-production-completion-program.md` Phase C.

## Global Constraints

- The API contract does not change. Amounts are still sent as `^(0|[1-9][0-9]{0,14})(\.[0-9]{1,2})?$` strings.
- Copy is in Bahasa Indonesia (EYD V). Rupiah appears as `Rp4.250.000,00` through `format.ts` only.
- Targets are ≥44 px. Focus is never hidden by sticky bars (WCAG 2.4.11). Help sits in the same place on every workspace page (3.2.6).
- WhatsApp text never contains prices, account numbers or NIK patterns.
- No new dependencies.

## Review Focus

1. **Ambiguous separators:** "12.50", "1.000", "4,250,000", "1.000,5", "Rp 4.250.000,00", and "1.2.3" (refused) must each parse to one documented value. Test: `rupiah_input_resolves_each_separator_style`.
2. **Overflow and junk:** more than 15 integer digits, more than 2 decimals, letters or a negative sign are refused with copy rather than silently truncated. Test: `rupiah_input_refuses_overflow_and_junk`.
3. **Skip link target:** it moves focus to the workspace `<main>` even when the sidebar is closed on mobile. Test: `skip_link_targets_main_content`.
4. **Help number unset:** with no configured number, the help link is absent rather than pointing to `wa.me/` with nothing after it. Test: `help_link_absent_without_number`.
5. **Locked list rows:** with `disabled`, every row action is a disabled `<button>`. Test: `list_table_disabled_rows`.

---

### Task 1: Tolerant rupiah parser and `RupiahInput`

**Files:**
- `frontend/src/lib/rupiah-input.ts`
- `frontend/src/components/workspace-kit/rupiah-input.tsx`
- `frontend/tests/rupiah-input.test.ts`
- Adopt in: `PaymentWorkspace.tsx` (transfer amount), `PaymentAllocationPanel.tsx`, `PaymentAllocationCorrectionPanel.tsx`, `BankReceiptWorkspace.tsx`, `CatalogPriceWorkspace.tsx`

**Interfaces:**
- `parseRupiahInput(text: string): { value: string } | { error: string }`. `value` is the canonical API decimal.
- `RupiahInput({ id, label, value, onChange, required, disabled, describedBy })`. `onChange` receives raw text. Below the field it renders "Akan dicatat: Rp4.250.000,00" or the error.

Parsing rules:
- strip `Rp`/`IDR` and spaces;
- both `.` and `,` present: the last one is the decimal separator;
- several of one separator: they are grouping;
- a single separator followed by exactly 3 digits: grouping;
- otherwise that single separator is decimal;
- at most 2 decimals;
- at most 15 integer digits.

- [ ] Write tests for Review Focus 1 and 2. Expected values:

| Typed | Result |
|---|---|
| `12.50` | `12.50` |
| `1.000` | `1000` |
| `4,250,000` | `4250000` |
| `1.000,5` | `1000.5` |
| `Rp 4.250.000,00` | `4250000.00` |
| `1000.25` | `1000.25` |
| `1.2.3` | error |
| `-5` | error |
| `12a` | error |
| `1234567890123456` | error |
| `1,234` | `1234` |

- [ ] Expect FAIL, implement, expect PASS.
- [ ] Adopt in the five forms: submit `parseRupiahInput(text).value` and drop the native pattern. Existing browser scripts that `fill('1000.25')` keep working, because a dot followed by 2 digits is a decimal.

### Task 2: Shell — skip link, landmarks, scroll padding and help

**Files:** `AdministrationNavigation.tsx`, `foundation.css`, `frontend/src/lib/whatsapp.ts`, `frontend/tests/shell-a11y.test.ts`

**Interfaces:** `whatsappLink(phone: string | undefined, text: string): string | null`. It returns `https://wa.me/62…?text=…`, and null when the phone is unset or invalid.

- [ ] Tests:
  - Review Focus 3: the skip link is the first focusable element, its href is `#konten-utama`, and the workspace `<main id="konten-utama" tabIndex={-1}>` exists;
  - Review Focus 4: `whatsappLink`. A leading `0` or `+62` becomes `62` and the result is digits only. Text containing `Rp` or 16 digits throws. Unset gives null;
  - the help link renders "Bantuan via WhatsApp" with `rel="noopener"` when `NEXT_PUBLIC_UNIT_TOKO_HELP_WHATSAPP` is set.
- [ ] Expect FAIL, implement, expect PASS. Add `html{scroll-padding-top:72px}` below 1024 px (the sticky top bar). Change the sheet close label to "Tutup".

### Task 3: EmptyState and locked ListTable

**Files:** `components/workspace-kit/empty-state.tsx`, `list-table.tsx`, `tests/workspace-kit.test.ts`. Adopt in `WorkSummary.tsx`, `OrderHistoryWorkspace.tsx`, `PaymentReviewWorkspace.tsx`.

**Interfaces:**
- `EmptyState({ kind: 'first-use' | 'no-results' | 'done', title, body, action? })`;
- `ListTable` gains `disabled?: boolean`, which disables every row action button.

- [ ] Tests:
  - Review Focus 5;
  - each `EmptyState` kind renders its heading and has `role="status"` for `done`.
- [ ] Expect FAIL, implement, expect PASS. Use "Semua pekerjaan selesai" (done) in Pekerjaan saya. Pass `disabled={locked}` in the two workspaces.

### Task 4: Deferred review minors

**Files:** `storefront-v7.css`, `workspace-navigation.ts`, `command-palette.tsx`, `procurement-quality-acceptance-forms.tsx`, `PaymentReviewWorkspace.tsx`

- [ ] Fix with a test each where testable:
  - raise specificity for `.shop-v7 .shop-remove-item` and `.shop-v7 .shop-field-error` (cascade test asserts both selectors);
  - the current-page label comes from an unfiltered map (navigation test);
  - palette: `aria-expanded` follows the result count, `aria-controls` is present only with results, `aria-autocomplete="list"`, and the active option scrolls into view (palette test);
  - quality-acceptance minutes keep up to 3 decimals (format test);
  - OCR auto-load rethrows only 401 and `actor_changed`, never a single-evidence 403 (payment review test).

### Task 5: Close

- [ ] Full frontend suite, tsc, eslint. Visual baseline at 360/768/1440 (`scripts/visual-baseline.mjs`) compared with `tmp/visual/final`. Register rows U01–U04, ledger, final review, commit.
