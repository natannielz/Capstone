# Design-system and styling architecture for Unit Toko (Next.js 16 + React 19 + Tailwind v4 + shadcn/ui), as of October 2026

Scope: how to collapse the current pile of global, versioned CSS override files into one token-driven design system with a consistent component layer, plus the performance, accessibility and form practices that fit this stack. Research date: 2026-10-19. Facts tied to a version carry that version. "Local codebase observation" means I read it from the repo on 2026-10-19 (paths are relative to `C:\Users\User\Documents\Capstone\frontend`).

---

## 1. Tailwind v4 theming: @theme tokens, CSS variables, semantic colors, dark mode, @layer ordering, avoiding specificity wars

### Takeaway
In Tailwind v4 the design system lives in CSS. Raw values are CSS variables on `:root` and `.dark`, and `@theme inline` maps them to utility namespaces (`--color-*`, `--radius-*`, `--font-*`, `--spacing`, and so on). Tailwind ships its CSS in native cascade layers (`theme, base, components, utilities`). Any CSS outside a layer beats every layered rule whatever its specificity. That is the main reason Unit Toko's unlayered override files keep defeating Tailwind and shadcn styles.

### Cited Findings
- **Tokens are utilities.** Tailwind v4 theme variables live in namespaces, and each namespace creates utilities. `--color-*` gives `bg-*`/`text-*`, `--font-*` gives font families, `--text-*` font sizes, `--font-weight-*`, `--tracking-*`, `--leading-*`, `--breakpoint-*` responsive variants, `--container-*` container-query variants, `--spacing-*` spacing/sizing, `--radius-*`, `--shadow-*`, and more. — [Tailwind: Theme variables](https://tailwindcss.com/docs/theme)
- **Use `@theme inline` when a token points at another variable.** Without `inline`, a `var()` resolves where the theme variable is defined, not where it is used, so you can get the wrong value (the docs' example falls back to `sans-serif` instead of Inter). With `inline`, the utility writes in the variable's value. — [Tailwind: Theme variables](https://tailwindcss.com/docs/theme)
- **Resetting the defaults.** `--color-*: initial` removes all default palette utilities (`bg-red-500` and so on) and keeps only your own. `--*: initial` removes the whole default theme. The docs' custom-theme example also sets `--spacing: 4px` and uses OKLCH colors. — [Tailwind: Theme variables](https://tailwindcss.com/docs/theme)
- **Layer order is built in.** `@import "tailwindcss"` expands to `@layer theme, base, components, utilities;` followed by `theme.css` → layer(theme), `preflight.css` → layer(base) and `utilities.css` → layer(utilities). — [Tailwind: Theme variables](https://tailwindcss.com/docs/theme)
- **v4 uses native cascade layers.** It no longer hijacks `@layer`. Custom utilities should use `@utility name { … }`. Custom utilities are sorted by how many properties they set, so a component-like `@utility btn` can still be overridden by single-purpose utilities. — [Tailwind: Upgrade guide](https://tailwindcss.com/docs/upgrade-guide)
- **Where custom CSS goes.** Element defaults go in `@layer base { h1 { font-size: var(--text-2xl) } }`. Component classes (`.card`, `.btn`, third-party widget skins) go in `@layer components` so utilities can still override them (`class="card rounded-none"`). `@utility` classes work with variants like `hover:` and `lg:`. — [Tailwind: Adding custom styles](https://tailwindcss.com/docs/adding-custom-styles)
- **Cascade-layer precedence (the root of specificity wars).** "Styles that are not defined in a layer always override styles declared in named and anonymous layers." For `!important` the order flips: important declarations inside layers beat important declarations outside layers, and earlier layers' important rules beat later ones. `@layer` has been Baseline (widely available) since March 2022. — [MDN: @layer](https://developer.mozilla.org/en-US/docs/Web/CSS/@layer)
- **Dark mode in v4** is a variant. To drive it from a class, use `@custom-variant dark (&:where(.dark, .dark *));`. To drive it from a data attribute, use `@custom-variant dark (&:where([data-theme=dark], [data-theme=dark] *));`. — [Tailwind: Dark mode](https://tailwindcss.com/docs/dark-mode)
- **Current shadcn token convention.** Tokens come in semantic pairs: a surface token plus `-foreground` for the text/icon colour on that surface (`bg-primary text-primary-foreground`). — [shadcn: Theming](https://ui.shadcn.com/docs/theming)
- **Current shadcn token set.** `background/foreground`, `card`, `popover`, `primary`, `secondary`, `muted`, `accent`, `destructive`, `border`, `input`, `ring`, `chart-1…5`, `sidebar`, `sidebar-primary`, `sidebar-accent`, `sidebar-border`, `sidebar-ring`, and `radius`. The default theme CSS imports `tailwindcss` and `shadcn/tailwind.css`, declares `@custom-variant dark (&:is(.dark *))`, maps every token in `@theme inline`, and defines values in OKLCH (for example `--primary: oklch(0.205 0 0)`). — [shadcn: Theming](https://ui.shadcn.com/docs/theming)
- **shadcn radius scale.** Every radius is derived from one `--radius` (default `0.625rem`): `--radius-sm: calc(var(--radius) * 0.6)`, `md` ×0.8, `lg` = base, `xl` ×1.4, `2xl` ×1.8, `3xl` ×2.2, `4xl` ×2.6. Changing `--radius` rescales them all. — [shadcn: Theming](https://ui.shadcn.com/docs/theming)
- **Adding custom semantic tokens (shadcn pattern).** Define `--warning`/`--warning-foreground` under `:root` and `.dark`, then expose them with `@theme inline { --color-warning: var(--warning); … }`. That gives `bg-warning text-warning-foreground`. — [shadcn: Theming](https://ui.shadcn.com/docs/theming)
- **shadcn's Tailwind v4 migration steps.** Move `:root`/`.dark` out of `@layer base`, wrap the values in the colour function, switch to `@theme inline`, and drop the `hsl()` wrappers in `@theme`. Chart configs then use `var(--chart-1)` directly. Components dropped `forwardRef` and gained `data-slot` attributes. `tailwindcss-animate` was deprecated on 2025-03-19 in favour of `tw-animate-css`. — [shadcn: Tailwind v4](https://ui.shadcn.com/docs/tailwind-v4)
- **next-themes defaults to `attribute = 'data-theme'`.** To use Tailwind's class-based dark mode you must pass `attribute="class"`. Other details: `ThemeProvider` is a client component; `<html>` needs `suppressHydrationWarning` because next-themes edits that element; options include `defaultTheme='system'`, `enableSystem`, `enableColorScheme` (sets the browser `color-scheme`), `disableTransitionOnChange`, and `nonce` for CSP. — [next-themes README](https://github.com/pacocoursey/next-themes)
- **Browser floor.** Tailwind v4 targets Safari 16.4, Chrome 111 and Firefox 128, and relies on `@property` and `color-mix()`. — [Tailwind: Upgrade guide](https://tailwindcss.com/docs/upgrade-guide). Next.js 16 sets the same floor (Chrome/Edge/Firefox 111+, Safari 16.4+). — [Next.js 16 blog](https://nextjs.org/blog/next-16)
- **Other v4 default changes.** `ring` is now 1px and `currentColor` (was 3px, blue-500). `--default-ring-width`/`--default-ring-color` exist only for compatibility. — [Tailwind: Upgrade guide](https://tailwindcss.com/docs/upgrade-guide)
- **Versions.** Tailwind's docs site shows v4.3 as current. — [Tailwind docs](https://tailwindcss.com/docs/theme). Unit Toko pins `tailwindcss` and `@tailwindcss/postcss` at 4.2.1. — local codebase observation, `package.json`

### Inferences
- **Recommended token architecture (one file, three tiers):**
  1. *Primitive tier*: brand/neutral ramps in OKLCH on `:root`, e.g. `--teal-600: oklch(...)`. Optionally reset the default palette with `--color-*: initial` so nobody reaches for `bg-red-500`.
  2. *Semantic tier*: the shadcn pairs (`--background`, `--primary`, `--muted`, `--destructive`, …) plus app-specific pairs written the shadcn way: `--success`, `--warning`, `--info`, and status tokens for order/fulfilment states (`--status-pending`, `--status-packed`, `--status-shipped`, `--status-cancelled`, each with `-foreground`). Define each in `:root` and `.dark`.
  3. *Utility mapping*: a single `@theme inline` block mapping semantic vars to `--color-*`, `--radius-*` (derived from `--radius`), and `--font-sans`/`--font-display`/`--font-mono`, pointing at `next/font` CSS variables.
- **Dark mode wiring:** keep `.dark` (the shadcn convention) and set `<ThemeProvider attribute="class" defaultTheme="system" enableSystem disableTransitionOnChange>`, with `suppressHydrationWarning` on `<html>`. If the provider is left at its default (`data-theme`), `dark:` utilities and the `.dark` token block never activate.
- **OKLCH vs hex:** Unit Toko's `globals.css` currently defines tokens in hex (local codebase observation: `--primary:#087f8c`, `--background:#f2f6f7`). Hex works with Tailwind v4 because opacity modifiers go through `color-mix()`. Converting to OKLCH matches shadcn's defaults and makes lightness ramps and dark-mode pairs predictable. This is a nice-to-have, not a blocker.
- **Rules that end the specificity wars:**
  - No unlayered app CSS except the token blocks (`:root`/`.dark` hold custom properties only, so layering doesn't matter for them).
  - All element defaults go in `@layer base`; all remaining bespoke component CSS goes in `@layer components` or `@utility`.
  - Legacy CSS goes in a dedicated `legacy` layer that sits **below** `components` and `utilities` (see Q2).
  - Ban `!important` in app CSS with a stylelint rule (`declaration-no-important`), because inside layers `!important` gets *stronger* the lower the layer.

### Gaps
- I did not verify whether `@tailwindcss/postcss` 4.2.x keeps a user-declared `@layer legacy` slotted between Tailwind's `base` and `components` when declared before `@import "tailwindcss"`. CSS semantics say the first `@layer` statement fixes the order. Verify with a 5-minute spike (inspect the built CSS).
- I found no official Tailwind/shadcn guidance on a "status token" naming scheme. The naming above is an inference.

---

## 2. Consolidating ~20+ layered override CSS files: audit, incremental (strangler) migration, CSS Modules vs global vs utilities, visual regression safety net

### Takeaway
Every Unit Toko override file is global and unlayered, and all are imported in the root layout. They therefore beat Tailwind/shadcn layered styles and ship to every route. The safest consolidation runs in five steps:
1. Wrap all legacy files in one low-priority `@layer legacy`, so new token-driven components win automatically.
2. Lock the current look with Playwright screenshot baselines.
3. Measure which legacy rules are actually used (Chrome Coverage / Playwright CSS coverage).
4. Strangle one screen family at a time onto primitives.
5. Delete each legacy file once its selectors have zero coverage hits and the screenshots are green.

### Cited Findings
- **Baseline: 24 global stylesheets.** `src/app/` holds 24 `.css` files, all imported in `src/app/layout.tsx` in version order: `globals.css`, `polish.css`, `orders-v5`, `catalog-v5`, `accessibility-v5`, `operations-v6`, `transactions-v6`, `storefront-v7`, `account-v7`, `brand-v8`, `landing-v8`, `motion-v9`, `experience-v11`, `storefront-v11`, `carousel-v12`, `controls-v13`, `landing-v13`, `login-v13`, `footer-v14`, `commerce-v14`, `workspace-v14`, `account-v14`, `workspace-lists-v15`, `operations-lists-v15`. — local codebase observation, `src/app/layout.tsx`
- **Baseline: size, `!important`, and no layers.** The files total about 5,886 lines, much of it minified one-line rules, so the real rule count is higher. `storefront-v7.css` alone is 2,567 lines; `catalog-v5.css` is 709 and `polish.css` 504. There are 64 `!important` declarations (`storefront-v7` 18, `polish` 12, `globals` 11). **Zero** `@layer` rules in any file. — local codebase observation
- **Baseline: conflicting fonts.** `globals.css` hard-codes `font-family:"Segoe UI",Arial,sans-serif` on `body`, uses `"Trebuchet MS"` for headings, and sets `--font-sans:"Segoe UI",Arial,sans-serif`. Later files use `var(--font-body)` and `var(--font-display)`. — local codebase observation. This explains the inconsistent fonts between old and new screens: "Segoe UI" and "Trebuchet MS" are Windows system fonts that are not on Android/macOS/iOS, so other platforms fall back to different faces.
- **Baseline: shadcn wiring.** `globals.css` imports `tailwindcss`, `tw-animate-css` and a vendored `../../vendor/shadcn-tailwind-4.13.0.css`, then defines the shadcn tokens in hex in the same file as large amounts of bespoke `.login-*`, `.workspace`, `.sidebar`, `.button.primary` rules. `components.json` uses `"style": "new-york"`, `"baseColor": "neutral"`, `"cssVariables": true`. — local codebase observation
- **Next.js: global CSS persists across navigation.** Global styles can be imported anywhere, but Next.js "currently does not remove stylesheets as you navigate between routes which can lead to conflicts". The docs recommend global CSS only for truly global styles (such as Tailwind base), Tailwind for component styling, and CSS Modules for scoped custom CSS. — [Next.js: CSS](https://nextjs.org/docs/app/getting-started/css)
- **Next.js: import order is cascade order.** CSS order in production follows import order. The docs advise keeping CSS imports in one entry file, importing globals/Tailwind at the root, using Tailwind for most styling, using CSS Modules (`*.module.css`, locally scoped class names) only where utilities fall short, and turning off auto-sorting of imports (ESLint `sort-imports`). `cssChunking` controls chunking. — [Next.js: CSS](https://nextjs.org/docs/app/getting-started/css)
- **Chrome DevTools Coverage panel.** Records which CSS and JS bytes a page uses while you interact (per function or per block). It shows Total vs Unused bytes per resource, a line-by-line used/unused gutter in Sources, CSS-only filtering, and export. — [Chrome DevTools: Coverage](https://developer.chrome.com/docs/devtools/coverage)
- **Playwright CSS coverage.** `page.coverage.startCSSCoverage()` / `stopCSSCoverage()` return, per stylesheet, its URL, text and used byte `ranges`. Chromium only. Style tags injected dynamically without `sourceURL` are not included. — [Playwright: Coverage](https://playwright.dev/docs/api/class-coverage)
- **Playwright `toHaveScreenshot()`.** Generates golden images on first run and compares later. Baselines are named per browser and platform (e.g. `-chromium-darwin.png`) because rendering differs by OS, fonts, hardware and headless mode. The docs warn to run tests in the same environment that produced the baselines. Tuning: `threshold` (pixelmatch perceptual tolerance, default 0.2), `maxDiffPixels`, and `stylePath` (a stylesheet applied during capture to hide volatile elements), all settable globally in `playwright.config`. — [Playwright: Visual comparisons](https://playwright.dev/docs/test-snapshots)
- **Storybook + Chromatic.** Storybook's visual tests turn every story into a snapshot test through the Chromatic cloud service (`npx storybook@latest add @chromatic-com/storybook`). Baselines accepted locally carry over to CI, and PRs get a "UI Tests" check. — [Storybook: Visual tests](https://storybook.js.org/docs/writing-tests/visual-testing)
- **Ladle.** A React-only, Vite-based "drop-in alternative to Storybook". Stories are code-split, and its index of components can be tested with Playwright. The project claims an empty Storybook 6.4 build was 5.1 MB vs Ladle's 250 KB; that comparison is old (Storybook 6.4). — [Ladle docs](https://ladle.dev/docs/)
- **Tooling gaps.** The frontend `package.json` has no `@playwright/test`, `storybook` or `@tanstack/react-table`. The test script is `node scripts/test-domain.mjs`. — local codebase observation

### Inferences
- **Step 0: quarantine the legacy CSS in a layer (one PR, near-zero visual change if `!important` is handled).** Remove the 23 JS `import "./x-vN.css"` lines from `layout.tsx` and re-import the files from CSS so they can carry a layer:
  ```css
  /* app/globals.css */
  @layer theme, base, legacy, components, utilities;   /* first declaration fixes order */
  @import "tailwindcss";
  @import "tw-animate-css";
  @import "shadcn/tailwind.css";                      /* or the vendored copy */
  @import "./legacy/polish.css" layer(legacy);
  @import "./legacy/storefront-v7.css" layer(legacy);
  /* …remaining files, in today's order… */
  @import "./tokens.css";                             /* :root / .dark custom properties */
  ```
  - New Tailwind utilities and shadcn components (layers `components`/`utilities`) now beat legacy selectors without `!important`.
  - Legacy still beats Preflight (`base`), which preserves today's look.
  - Risk: the 64 `!important` declarations become *stronger* inside a low layer, because layered important rules beat utilities' important rules. Audit and remove them first, or put the few files that use them in their own `legacy-important` layer and fix them early.
  - JS imports cannot express a layer. That is why the move to CSS `@import … layer()` is needed.
- **Step 1: lock the current look.** Add `@playwright/test` and write a screenshot suite of about 30–60 key states:
  - role × screen (owner, admin, cashier, warehouse, …; dashboard, lists, order detail, forms, dialogs open), light/dark, and desktop/tablet widths;
  - masks or `stylePath` for timestamps, IDs and charts; seeded data;
  - baselines generated in one fixed environment (a pinned Playwright Docker image in CI) to avoid OS font noise. This matters more than usual because the current fonts are Windows-only system fonts.
- **Step 2: build a usage map.** Run the same Playwright journeys with `startCSSCoverage()`, merge the byte ranges per file across all journeys, and report unused selectors per legacy file. Selectors never hit by any journey are deletion candidates; confirm with a manual DevTools Coverage pass on rare screens. Caveat: coverage only sees states your journeys reach (error states, empty states, rarely used roles). Treat "unused" as "unused in tested flows".
- **Step 3: strangler order.** Migrate by screen family, not by file:
  1. Tokens and fonts (one PR). This fixes the cross-screen font mismatch immediately.
  2. App shell (sidebar/topbar).
  3. Primitives (Button, Field, Badge, Card).
  4. Operations lists → DataTable.
  5. Order/fulfilment detail screens.
  6. Account/login.
  7. Public storefront/catalog/landing last, since it carries the largest legacy files (`storefront-v7`, `catalog-v5`) and GSAP.

  Each step: replace the markup with primitives, re-baseline the intentional diffs, delete the now-dead legacy selectors, and keep the screenshots green elsewhere. Add a lint/CI guard so new code can't add legacy class names, and no new `*-vNN.css` files.
- **Where styles should live after migration:**
  - Tailwind utilities inside component files (via `cva` variants, shadcn-style) for about 95% of styling.
  - CSS Modules only for genuinely bespoke visuals: marketing hero art, GSAP-driven sections, print/PDF layouts. They scope automatically and are loaded only by the routes that import them, which keeps operational screens from paying for marketing CSS.
  - Global CSS limited to tokens, `@layer base` element defaults, and third-party skins in `@layer components`.
- **Visual-testing tool choice:** the team already uses Playwright-style journeys for validation, and Chromatic is a paid SaaS. Start with page-level Playwright `toHaveScreenshot()`. Add component-level stories (Ladle is lighter; Storybook+Chromatic has the richest review workflow) once the primitive layer exists, so each primitive variant (sizes, states, density, dark) has a snapshot.

### Gaps
- I found no authoritative engineering-blog case study on a "strangler pattern for CSS" to cite. The step plan above is synthesized from the Next.js, MDN and Playwright docs.
- I did not evaluate PurgeCSS-style static unused-selector tools against Tailwind v4 / React class strings. Static analysis gives false positives with dynamic `className` strings.
- I did not fetch Playwright's `mask`/`animations` option docs (the API reference). Verify there before relying on them.

---

## 3. Typography and spacing for dense operational UIs: fonts for Indonesian (Latin) text, tabular money figures, type scale, density modes, layout grids

### Takeaway
Replace the Windows-only system fonts ("Segoe UI", "Trebuchet MS") with one self-hosted variable UI family loaded through `next/font`, such as Inter, or the Indonesian-designed Plus Jakarta Sans, which also has tabular figures. Expose it as `--font-sans`. Apply `tabular-nums` to every numeric/money column. Use a compact 13–14px base scale for operations screens and a separate marketing display scale. Density can be a token-level switch.

### Cited Findings
- **Inter.** A UI workhorse with 2,000+ glyphs. The intro says it covers "147 languages", while its language section says it "covers 147 scripts"; the wording is inconsistent on the site. Weights run 100–900, optical sizes go from text to display, and a true italic exists. OpenType features include `tnum` (tabular numbers, "useful for tabular data, where comparing columns across rows is desired"), slashed zero, `calt` contextual alternates (on by default), fractions, and case alternates. — [Inter typeface](https://rsms.me/inter/)
- **Plus Jakarta Sans** is by Tokotype (an Indonesian foundry) under the SIL Open Font License. Its release notes say v2.6 (Nov 2021) added tabular figures and v2.4 added a variable weight axis and stylistic sets. The latest GitHub release is v2.7.1, and Fontsource lists a package updated March 2026. — [Plus Jakarta Sans releases (GitHub)](https://github.com/tokotype/PlusJakartaSans/releases). Which OpenType tag enables its tabular figures was not confirmed.
- **Tailwind numeric utilities:** `tabular-nums`, `proportional-nums`, `lining-nums`, `oldstyle-nums`, `slashed-zero`, `ordinal`, `diagonal-fractions`, `stacked-fractions` (they map to `font-variant-numeric`). — [Tailwind: font-variant-numeric](https://tailwindcss.com/docs/font-variant-numeric)
- **`next/font`** optimizes fonts automatically, self-hosts them, and removes external network requests "for improved privacy and performance". — [Next.js: Font Optimization](https://nextjs.org/docs/app/getting-started/fonts)
- **Wiring fonts into tokens.** Font tokens should point at the `next/font` variable through `@theme inline { --font-sans: var(--font-inter); }`, which avoids the variable-resolution trap. — [Tailwind: Theme variables](https://tailwindcss.com/docs/theme)
- **Spacing is one variable.** Tailwind's spacing utilities are driven by `--spacing`; the custom-theme example sets `--spacing: 4px`. — [Tailwind: Theme variables](https://tailwindcss.com/docs/theme)
- **WCAG 2.2 SC 2.5.8 (AA).** Pointer targets must be at least 24×24 CSS px, unless a spacing exception applies: a 24px circle centred on each undersized target must not intersect another target or another undersized target's circle. Other exceptions: an equivalent control, inline links in text, user-agent-controlled defaults, and essential presentation. — [W3C: Understanding Target Size (Minimum)](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html)
- **shadcn/typeset (July 2026)** styles rendered HTML/markdown with one `typeset` class you own, with size/leading/flow controls per context (e.g. `.typeset-docs { --typeset-size: 15px; --typeset-leading: 1.75 }`). Useful for help text, policies and catalog descriptions, not for dense tables. — [shadcn: Introducing shadcn/typeset](https://ui.shadcn.com/docs/changelog/2026-07-typeset)

### Inferences
- **Font recommendation:**
  - Indonesian uses the basic Latin alphabet with no special diacritics, so any well-hinted Latin UI family works.
  - Choose **Inter** (variable, `tnum` and slashed zero for SKU/IDs, very legible at 12–14px) as `--font-sans` for operations.
  - Optionally use **Plus Jakarta Sans** as `--font-display` on the public landing/catalog for local brand character. It is an Indonesian design and also has tabular figures.
  - Load both with `next/font` (`variable: '--font-inter'` and so on). Remove the hard-coded `font-family` declarations in `globals.css`/legacy files during Step 0/1.
- **Numerals:** a `.num` convention, i.e. `tabular-nums` (plus `slashed-zero` for codes), on every money/quantity cell, KPI and total. Right-align numeric columns. Keep the "Rp" prefix consistent (see Q6 for formatting).
- **Proposed operations type scale** (14px base; lighter tokens than marketing):
  - `--text-xs` 12/16 for meta and table captions;
  - `--text-sm` 13/18 for dense table cells;
  - `--text-base` 14/20 for body and form controls;
  - `--text-lg` 16/24 for section titles;
  - `--text-xl` 20/28 for page titles;
  - `--text-2xl` 24/32 for KPI numbers.

  Marketing keeps a separate fluid display scale (`clamp()`) in its own CSS Module or tokens. Limit weights to 400/500/600.
- **Density modes:**
  - Two densities, "comfortable" (default) and "compact" (tables/lists), set by `data-density` on the workspace shell.
  - Implement them as component-level tokens rather than redefining global `--spacing`. Redefining `--spacing` would also shrink page layout gaps. Tokens such as `--row-h` (40px comfortable / 32px compact), `--cell-px`, `--control-h` (36/32px) and `--gap-stack`.
  - Keep interactive controls ≥24×24 CSS px (WCAG 2.5.8) even in compact mode. Row-level action icons need 24px hit areas or spacing.
- **Layout grid for the back-office:**
  - Fixed sidebar of 240px (collapsible to 64px icon rail) plus a fluid content area.
  - Content max-width ~1440–1600px for forms/detail pages and full-bleed for data tables.
  - 12-column grid with 16px/24px gutters (compact/comfortable).
  - Container queries (`@container`, Tailwind `--container-*`) for cards/panels that appear in both sidebars and main columns.

### Gaps
- I found no authoritative 2026 source on density-mode systems for back-office UIs to cite. The numbers above are design inferences and should be validated with users and screenshots.
- Inter's Indonesian coverage is not explicitly listed in the material I fetched. It is inferred from Latin coverage.
- Plus Jakarta Sans' exact feature tag for tabular figures (presumably `tnum`) is unconfirmed.

---

## 4. Component layer: consistent primitives on shadcn/Radix/Base UI; what accessibility Radix/Base UI guarantee vs what you must add

### Takeaway
Build one owned `components/ui` layer of shadcn primitives on a **single** headless library per component type. Since July 2026 shadcn defaults to Base UI; Radix is still supported and not deprecated. Compose app-level patterns (DataTable, StatusPill, EmptyState, Stepper/Timeline, PageHeader, FormField with error summary) only from those primitives and tokens. Radix and Base UI handle ARIA roles/attributes, focus management and keyboard patterns. The app still owns labels and accessible names, colour contrast, non-colour status cues, target sizes, error messaging and live announcements.

### Cited Findings
- **shadcn changelog, 2026.**
  - Feb 2026: "Unified Radix UI Package" and "Blocks for Radix and Base UI".
  - Mar 2026: shadcn/cli v4.
  - Apr–May 2026: presets, `shadcn apply`, "shadcn eject", package imports.
  - Jul 2026: "Base UI as the Default", a new Toast, React Aria support, shadcn/typeset.
  - Oct 2026: Trace and Code Block components, "available for Base UI, React Aria and Radix across all eight styles".

  — [shadcn: Changelog](https://ui.shadcn.com/docs/changelog)
- **Base UI as the default (July 2026).** "Radix is not being deprecated… every update and new component will ship for both libraries" (unless Base UI-only). "You do not need to migrate." `shadcn init -b radix` keeps Radix. "Starting something new? We recommend Base UI." — [shadcn: Base UI as the Default](https://ui.shadcn.com/docs/changelog/2026-07-base-ui-default)
- **New shadcn Toast (July 2026).** Built on Base UI primitives for Base UI projects. It supports actions, status types, promises, stacking and swipe dismissal (`shadcn add toast`). — [shadcn: Toast](https://ui.shadcn.com/docs/changelog/2026-07-toast)
- **Base UI releases.** v1.9.0 (Oct 9, 2026) is the latest: menu filtering preview, faster mounting of large lists in Combobox/Menu/Select, Drawer improvements, and "many accessibility, focus, and interaction fixes". Earlier: v1.8.0 (Sep 2026), v1.7.0 (Aug 2026, smaller bundle), v1.6.0 (Jun 2026, OTPField stable, Accordion keyboard nav aligned with APG). — [Base UI: Releases](https://base-ui.com/react/overview/releases)
- **Installed versions.** Unit Toko depends on both `radix-ui ^1.6.7` (the unified package) and `@base-ui/react ^1.7.0`, plus `sonner ^2.0.8`, `lucide-react ^1.31.0`, `tw-animate-css ^1.4.0`. — local codebase observation, `package.json`
- **What Radix guarantees.** It follows the WAI-ARIA Authoring Practices and is tested across browsers and assistive tech. It handles `aria`/`role` attributes, focus management (e.g. AlertDialog moves focus to Cancel on open) and keyboard navigation. Labels remain your job: "Ultimately it's up to you to provide those labels." — [Radix: Accessibility](https://www.radix-ui.com/primitives/docs/overview/accessibility)
- **What Base UI guarantees.** It handles ARIA/role attributes, pointer interactions, keyboard navigation (arrow keys, alphanumeric, Home, End, Enter, Esc per APG) and focus management. Form/Input/Field/Fieldset associate controls with labels automatically. Custom controls still need accessible names (`aria-label`, `aria-labelledby`, `alt`). Colour contrast is explicitly the developer's job (they suggest APCA unless strict compliance requires WCAG ratios). — [Base UI: Accessibility](https://base-ui.com/react/overview/accessibility)
- **TanStack Table is headless.** It provides state, data processing and APIs but "do[es] not provide markup, styles, or pre-built implementations". You get full control of markup and styles and smaller bundles, at the cost of more setup. — [TanStack Table: Introduction](https://tanstack.com/table/v8/docs/introduction)
- **TanStack Virtual's `useVirtualizer`** renders only visible rows of a large list (`count`, `getScrollElement`, `estimateSize`). Vertical, horizontal and grid virtualization are possible. — [TanStack Virtual: Introduction](https://tanstack.com/virtual/latest/docs/introduction)
- **Status indicators (Carbon).** Colour alone is insufficient (WCAG Use of Color). Use at least two of colour, shape and symbol. Keep ≥3:1 contrast between status colours and against the background (WCAG non-text contrast). Pair icons with text. — [Carbon: Status indicator pattern](https://carbondesignsystem.com/patterns/status-indicator-pattern/)

### Inferences
- **Pick one primitive library per component.** Having both Radix and Base UI risks two portal/focus-trap/dismiss-layer stacks interacting (e.g. a Base UI Select inside a Radix Dialog). Given Unit Toko already ships both, either:
  - (a) standardize on **Base UI**, the shadcn default and actively improved in 2026, for all new and migrated primitives, and keep Radix only where a component exists only there until replaced; or
  - (b) stay on Radix, which is fully supported.

  Record the choice in `components.json` and an ADR.
- **Toasts.** The app uses Sonner. If it moves to Base UI, the new shadcn Toast is the native option. Otherwise keep Sonner behind one `toast()` wrapper so the call sites never change.
- **Primitive inventory and owned contracts (each with cva variants + tokens only, no raw hex):**
  - **Button**: variants primary/secondary/outline/ghost/destructive/link; sizes sm/md/lg/icon. Must have a visible `focus-visible` ring using `--ring`, `aria-busy` plus a spinner and disabled state during pending actions, and icon-only buttons need `aria-label`.
  - **Field**: Label + Control + Description + Error, wired with `id`/`aria-describedby`/`aria-invalid` (Base UI Field does the association; with Radix, use shadcn's Form/Field pattern). Includes Input, Textarea, Select/Combobox, NumberInput/CurrencyInput, DatePicker, Checkbox/Radio/Switch.
  - **DataTable**: TanStack Table + shadcn Table markup. Column defs per domain; server-side pagination/sort/filter (the backend reads are slow); sticky header; `tabular-nums` right-aligned numeric columns; row selection with a bulk-action bar; a density toggle; a column-visibility menu; TanStack Virtual for >200 rows. Use a semantic `<table>` with `<th scope>` and `aria-sort`.
  - **Dialog / AlertDialog / Sheet (Drawer)**: focus trap and return are free. You add a title (required for an accessible name), a description, and a destructive-confirm pattern.
  - **Command palette** (shadcn Command/Combobox): role-aware navigation and actions ("Buat pesanan", "Cari SKU…").
  - **Toast**: polite announcements for success; errors that need action should be inline/persistent, not toast-only.
  - **Badge/StatusPill**: maps a domain status enum to token, icon and Indonesian label (colour + icon + text, per Carbon).
  - **Stepper/Timeline**: order-lifecycle/provenance display as an ordered list (`<ol>`) with `aria-current="step"`.
  - **EmptyState**: icon + title + explanation + primary action; role-aware copy.
  - **Skeleton**: matches final layout dimensions to avoid CLS; `aria-busy` on the region.
  - **PageHeader / Toolbar / FilterBar**: the consistent layout for every workspace screen.
- **Accessibility work the app must add regardless of library:**
  - accessible names for icon buttons;
  - visible labels (no placeholder-only fields);
  - contrast checks for every token pair in light and dark (automate with a script over the token file);
  - non-colour status cues;
  - 24px targets;
  - an error summary and focus management on form submit (Q6);
  - `aria-live` for async table refreshes;
  - skip-link and landmark structure in the app shell;
  - automated `@axe-core/playwright` scans alongside the screenshot tests.

### Gaps
- I did not fetch component-level docs for every Base UI/Radix component (e.g. which components exist only in Base UI). Check them per component during migration.
- I did not fetch the React Aria option shadcn added in July 2026. It may matter if the team wants Adobe's stricter a11y semantics.

---

## 5. Performance for Next.js 16 / React 19: Core Web Vitals targets, RSC boundaries, streaming/Suspense for slow APIs, optimistic UI, keeping heavy JS off ops screens, images, bundle analysis

### Takeaway
The targets are LCP ≤ 2.5s, INP ≤ 200ms and CLS ≤ 0.1, all at the 75th percentile. For a data-dense back office with multi-second backend reads, the main levers are:
- Server Components by default, with `'use client'` pushed to leaf widgets.
- `loading.tsx` plus nested `<Suspense>` so the shell and filters appear immediately while slow panels stream.
- `useOptimistic`/`useActionState` for mutations.
- Virtualized or paginated tables to cap DOM size (which drives INP).
- GSAP confined to marketing routes via dynamic import.
- Bundle analysis per route. Unit Toko builds with webpack, so use `@next/bundle-analyzer`; `next analyze` requires Turbopack.

### Cited Findings
- **Core Web Vitals targets.** LCP within **2.5 s**, INP of **200 ms** or less, CLS of **0.1** or less. Measure at the **75th percentile** of page loads, segmented by mobile and desktop. — [web.dev: Web Vitals](https://web.dev/articles/vitals)
- **INP subparts and fixes.** INP = input delay + processing duration + presentation delay. Advice: do little work in event callbacks, break up long tasks (yield via `setTimeout` or async yield points; yielding right after the UI update lets rendering run sooner), and minimize DOM size, because rendering work grows with DOM size on first render and on every interaction. — [web.dev: Optimize INP](https://web.dev/articles/optimize-inp)
- **Next.js 16 (released Oct 2025):**
  - Turbopack is the default bundler (opt out with `next build --webpack`).
  - Cache Components: opt-in `"use cache"`, `cacheComponents: true`, which completes Partial Prerendering. All dynamic code runs at request time by default.
  - `proxy.ts` replaces `middleware.ts` (deprecated).
  - React Compiler support is stable but **not enabled by default** (`reactCompiler: true`; it relies on Babel, so builds are slower).
  - React 19.2 features: View Transitions, `useEffectEvent`, `<Activity/>`.
  - Node 20.9+ and TS 5.1+ required.

  — [Next.js 16 blog](https://nextjs.org/blog/next-16)
- **Next.js 16 `next/image` default changes.** `minimumCacheTTL` went from 60s to 4h. `imageSizes` dropped 16. `qualities` defaults to `[75]` (the `quality` prop is coerced to the nearest allowed value). Local-IP optimization is blocked by default (`dangerouslyAllowLocalIP`). Max 3 redirects. `images.domains` is deprecated in favour of `remotePatterns`. — [Next.js 16 blog](https://nextjs.org/blog/next-16)
- **Unit Toko build setup.** It runs `next dev --webpack` and `next build --webpack` on `next 16.3.4` and `react 19.2.6`. — local codebase observation, `package.json`
- **Bundle analysis.** Two tools: the Next.js Bundle Analyzer for Turbopack (`npx next analyze`, v16.1+, integrated with Turbopack's module graph, with import tracing for server and client modules) and the `@next/bundle-analyzer` plugin for webpack. `optimizePackageImports` loads only the modules actually used from packages with many exports (icon/utility libraries), and some libraries are optimized automatically. — [Next.js: Optimizing package bundling](https://nextjs.org/docs/app/guides/package-bundling)
- **RSC boundaries.** Put `'use client'` at the top of the interactive leaf components rather than marking large parts of the UI as client. Once a file is `'use client'`, everything it imports and renders directly joins the client bundle. Server Components passed as `children`/props to Client Components stay server-rendered (the `<Modal><Cart/></Modal>` slot pattern). — [Next.js: Server and Client Components](https://nextjs.org/docs/app/getting-started/server-and-client-components)
- **Streaming.** `loading.tsx` wraps `page` and its children in a `<Suspense>` boundary, giving an instant, prerenderable fallback (skeletons or a meaningful partial screen) on navigation. Streaming is server-rendered and doesn't hurt SEO; metadata is resolved before streaming for HTML-only bots. — [Next.js: loading.js](https://nextjs.org/docs/app/api-reference/file-conventions/loading)
- **`useOptimistic`.** `const [optimisticState, setOptimistic] = useOptimistic(value, reducer?)`. The setter must run inside a Transition or Action; outside one, the optimistic state flashes and reverts immediately. On failure the UI rolls back automatically (the docs' "optimistic delete with error recovery" example shows the error and restores the item). — [React: useOptimistic](https://react.dev/reference/react/useOptimistic)
- **`useActionState`** returns `[state, dispatchAction, isPending]`. The reducer receives previous state plus payload. The optional `permalink` enables progressive enhancement with Server Functions (the form works before JS loads). With Server Functions, the server's response can show before hydration completes. — [React: useActionState](https://react.dev/reference/react/useActionState)

### Inferences
- **Slow backend reads, which take seconds:**
  - Make each workspace page a Server Component that renders the shell, PageHeader and FilterBar immediately.
  - Wrap each slow data region (KPI row, DataTable, side panel) in its own `<Suspense fallback={<Skeleton matching layout/>}>`, so one slow call doesn't block the screen.
  - Start fetches in parallel (create promises before awaiting, or pass promises down and `use()` them in children).
  - With `cacheComponents`, `"use cache"` reference data that changes rarely (catalog taxonomies, role/permission maps, warehouse lists) with tags, and use `updateTag` after mutations for read-your-writes. Adopting Cache Components is opt-in and should be its own spike.
- **Mutations:**
  - Status changes, approvals, picking/packing confirmations, etc. go through `useActionState` (pending state + server error state) plus `useOptimistic` for the row or pill.
  - Always render a pending indicator (`aria-busy`, a dimmed row) and a rollback message on failure.
  - Don't use optimism for irreversible or financial steps: payment capture, stock adjustments and provenance seals should show a confirmed server result.
- **INP on dense screens:**
  - Server-side pagination (25–100 rows) or TanStack Virtual for long lists.
  - Avoid re-rendering whole tables on filter keystrokes: debounce, or `useTransition` for filter state.
  - Consider enabling the React Compiler after measuring. It is stable in Next 16 but adds Babel build time, notable since the project is already on webpack.
- **Client-JS budget:**
  - GSAP and the marketing carousel load only on public routes, via `next/dynamic` or import inside a client component used only there.
  - Move landing/storefront CSS out of the root layout into route-group layouts (`app/(marketing)/layout.tsx`) or CSS Modules. That way ops screens don't download it, and given Next's "stylesheets aren't removed on navigation" caveat, marketing CSS can't leak into workspaces.
  - `lucide-react` imports should be per-icon named imports. Check whether it is on Next's auto-optimized list; add it to `optimizePackageImports` if not.
- **Bundle analysis plan:** because builds use `--webpack`, add `@next/bundle-analyzer` now. If the team later drops `--webpack` (Turbopack is the Next 16 default), switch to `next analyze`. Track route-level first-load JS for `/` (landing), catalog, and the 3 heaviest workspace routes in CI.
- **CLS:** reserve space for images (`next/image` with width/height), skeletons that match final dimensions, `next/font` with fallback metrics, and no late-injected banners above content.
- **Measurement:** web-vitals RUM reporting (`useReportWebVitals`) segmented by route and role. The 75th-percentile thresholds apply to field data, and back-office users on low-end Android tablets or older office PCs will dominate INP.

### Gaps
- I didn't verify whether Next 16.x has changed the `--webpack` story (e.g. new deprecation warnings) since the 16.0 blog. Check the 16.1–16.3 release notes.
- I did not fetch the `useReportWebVitals` and `next/dynamic` docs this session. Those recommendations rely on standard Next.js APIs and should be confirmed against the current docs.
- I did not check whether `lucide-react` is on the auto-optimized `optimizePackageImports` list.

---

## 6. Forms: react-hook-form + zod patterns, accessible error summaries, Indonesian number/currency input handling

### Takeaway
Use one `Form`/`Field` abstraction:
- react-hook-form (`mode: "onTouched"`, `shouldFocusError` on) with a zod schema shared with the server action.
- An inline error per field (`aria-invalid` + `aria-describedby`).
- A GOV.UK-style error summary for long forms, which gets focus on submit and links to each field.

For money, store integer rupiah, parse locale input (`.` thousands, `,` decimals), and format with `Intl.NumberFormat('id-ID')` with **explicit** fraction digits, because default IDR digits differ between ICU/CLDR versions.

### Cited Findings
- **RHF `mode`.** Options are `onSubmit` (default), `onBlur`, `onChange` (warned as costly in re-renders), `onTouched` (validate on first blur, then on every change) and `all`. — [React Hook Form: useForm](https://react-hook-form.com/docs/useform)
- **RHF `shouldFocusError`** defaults to `true` and focuses the first errored field on a failed submit. It only works if the field's `ref` is attached to a real DOM element, and the focus order follows `register` order. `criteriaMode` is `firstError` (default) or `all`. — [React Hook Form: useForm](https://react-hook-form.com/docs/useform)
- **GOV.UK error summary.** List every error at the top of the page and link each one to its field (`<a href="#full-name-input">`). The inline message carries a visually hidden "Error:" prefix. In Nov 2022 `role="alert"` moved to an inner element (fixing JAWS reading). Feb 2025 guidance says GOV.UK Frontend moves keyboard focus to the error summary automatically. — [GOV.UK Design System: Error summary](https://design-system.service.gov.uk/components/error-summary/)
- **Server-side validation.** `useActionState` returns server-produced state (e.g. validation errors) plus `isPending`, and supports progressive enhancement via `permalink`. — [React: useActionState](https://react.dev/reference/react/useActionState)
- **`Intl.NumberFormat`.** Currency style requires a `currency` option. `currencyDisplay` and `currencySign: 'accounting'` control presentation. Setting `minimumFractionDigits` and `maximumFractionDigits` to incompatible values throws `RangeError`. — [MDN: Intl.NumberFormat()](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Intl/NumberFormat/NumberFormat)
- **Empirical check (Node v25.7.0, run 2026-10-19):**
  - `new Intl.NumberFormat('id-ID',{style:'currency',currency:'IDR'}).format(1234567.5)` → `"Rp 1.234.568"`. The default rounded to 0 fraction digits in this runtime.
  - With `maximumFractionDigits:0` → `"Rp 1.234.567"`.
  - Decimal `1234567.89` → `"1.234.567,89"`.
  - `formatToParts` gives currency `Rp`, then a literal space, integer, group `.`.
  - `notation:'compact'` on 1,250,000 → `"1,3 jt"`.
  - `currencySign:'accounting'` on −5000 → `"-Rp 5.000"` (no parentheses for id-ID).

  — local computation; the API semantics are from [MDN: Intl.NumberFormat()](https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Intl/NumberFormat/NumberFormat)
- **Installed versions.** Unit Toko uses `react-hook-form ^7.85.0` and `zod ^3.25.76`. — local codebase observation, `package.json`

### Inferences
- **Form architecture:**
  - One zod schema per form, in a shared module imported by both the client RHF resolver (`@hookform/resolvers/zod`) and the server action, so client and server validation can't drift.
  - The server action returns `{ fieldErrors, formError }`, which is merged into RHF with `setError` when `useActionState` state changes.
  - Use `mode: "onTouched"` to avoid shouting errors while the user is still typing.
- **Error UX recipe:**
  - On a failed submit with more than about 3 fields or multiple sections, render an `ErrorSummary` at the top: heading "Ada masalah", a list of `<a href="#field-id">` links in the same order as the fields, and `role="alert"` on an inner element. Move focus to it, and override `shouldFocusError` to `false` for those forms so the two don't fight.
  - For short dialogs, keep the default RHF focus-first-error behaviour.
  - Each field shows its message under the label/control, with `aria-invalid="true"` and `aria-describedby` → error id, plus a visually hidden "Kesalahan:" prefix.
  - Error text names the fix ("Masukkan jumlah lebih dari 0"), not just "Invalid".
- **Currency/number inputs (IDR):**
  - Model money as **integer rupiah** in zod (`z.number().int().nonnegative()`). Rupiah prices in practice have no sen; decide explicitly with the business, and use minor units if sen are ever needed.
  - Input: `inputMode="numeric"` (or `decimal` for quantities with fractions), `type="text"` (not `type="number"`, which breaks grouping and locale decimals), a static "Rp" prefix adornment, and live grouping display (`1.250.000`).
  - Parse by stripping `.` grouping and treating `,` as the decimal separator. Also handle pasted values in "1,250,000"/"1250000.00" formats from spreadsheets with a tolerant parser and a confirmation hint.
  - Display: one shared `formatRupiah(n)` = `new Intl.NumberFormat('id-ID',{style:'currency',currency:'IDR',minimumFractionDigits:0,maximumFractionDigits:0})`. Pin both digits because runtime defaults differ (the Node 25 result above rounded half-rupiah values). Use `tabular-nums` in tables.
  - Server render and client hydration must use identical formatter options to avoid hydration mismatches. The "Rp" literal separator may be a no-break space depending on ICU; don't string-compare formatted output in tests.
  - Dates: `Intl.DateTimeFormat('id-ID')` and a consistent WIB/WITA/WIT timezone display policy.
- **zod version:** `zod ^3.25.x` is the release line that also exposes the v4 API under a subpath. Moving schemas to zod 4 is optional and separate from the design-system work (unverified this session).

### Gaps
- I did not fetch the current `@hookform/resolvers` docs for zod 4 compatibility, nor the zod 3.25/zod 4 subpath details. Verify before migrating schemas.
- Why IDR's default fraction digits change (CLDR currency data vs ISO 4217's 2 minor units) was not confirmed from a primary source. Behaviour may differ across browsers and ICU versions, so always set digits explicitly.
- I found no Indonesian-government or local design-system guidance (comparable to GOV.UK) on form error patterns in Bahasa Indonesia. The copy suggestions are inferences.
