# Phase G: Legacy Retirement, Dependency Upgrades and UI Overhaul Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The repository contains only the maintained stack, its dependencies carry no known advisories, and the public and core screens look distinctly better. Specifically:
- the old Node/Drizzle backend, the old frontend app, its domain layer and its tests are deleted, locally and on GitHub;
- Next.js and other dependencies are upgraded;
- the landing page gets a custom 3D scene;
- the login page and core workspace screens are redesigned.

**Architecture:** Retirement is driven by reachability, not guesses. `scratchpad/reachable.cjs` lists every frontend source file that is not reachable from a Next entry point (app pages, layouts, routes, proxy). Unreachable files, the tests that only exercise them, and `backend/legacy` (which only feeds those tests) are deleted together. Legacy stylesheets are retired rule by rule: rules whose selectors no reachable component uses are deleted, and the rules still used move into owned CSS Modules or `foundation.css` until the `legacy` cascade layer is empty. The 3D landing is a client-only island loaded with `next/dynamic` (no SSR), with a static fallback and reduced-motion handling.

**Tech Stack:** Next.js 16.4.x, React 19, Tailwind 4 layers, three.js (with `@react-three/fiber` if it pays for itself), `node:test`, Playwright verify scripts.

**Spec:** Owner request of 20 October 2026:
- "delete old stuff … migrate … then delete all the old file from GitHub and local";
- "landing page much better … a 3D object with a 3D animation … creative … not AI slop";
- "improve the UI and UX from login page, landing page and many pages".

Owner approval covers the Next.js upgrade and dependency upgrades. Research basis: `reports/Unit Toko UI UX improvements.md` (Phase 4.1, "finish the strangle").

## Global Constraints

- Never delete a file reachable from a live route. Compatibility redirect routes (`/cart`, `/checkout`, `/account`, `/login`, `/workspace`, `/customer/login`, `/staff/login`) stay, because old bookmarks keep working.
- Delete untracked local evidence under `tmp/` only when the owner names it; this phase deletes code, not evidence.
- The 3D scene must:
  - be fully decorative: hidden from assistive tech, with the same content available as text;
  - respect `prefers-reduced-motion` with a still frame;
  - keep the landing page usable without WebGL;
  - work under the site CSP (no eval and no remote assets; any three.js shader uses WebGL only);
  - lazy-load so it never blocks first paint.
- Copy stays in Bahasa Indonesia (EYD V); WCAG 2.2 AA contrast; 44 px targets.

## Review Focus

1. **A deleted file was still imported somewhere reachable.** Test: `tsc`, `next build` and the full unit suite all pass after each deletion batch, and the reachability script then reports only the expected leftovers.
2. **A legacy CSS rule still styled a live screen.** Test: a cascade test asserts the `legacy` layer is empty or gone. A visual baseline at 360, 768 and 1440 px is compared before and after for the landing, login, account, catalog, drafts and payment-review screens.
3. **The 3D scene breaks low-end phones or reduced motion.** Test: the landing renders its static fallback when WebGL or motion is unavailable (unit test on the capability gate). The scene caps the device pixel ratio and pauses when hidden.
4. **The Next upgrade changes behaviour.** Test: the full unit suite, `tsc`, lint and a production build pass. The browser sweep covers flows in Phase E.
5. **CI references to deleted folders.** Test: `quality.yml` and `release-validation.yml` contain no `backend/legacy` steps, and the YAML parses.

---

### Task 1: Retire the old frontend app (reachability-driven)

- [x] Delete every unreachable file under:
  - `src/components` (except `ui/*` still used);
  - `src/lib/domain`;
  - `src/lib/client`;
  - `src/components/shop`;
  - `src/hooks`;
  - `src/app/chatgpt-auth.ts`;
  - the unused `src/components/ui/*`.
  Keep `src/lib/security-headers.ts`, which `next.config.ts` imports, `src/types/*.d.ts`, and test helpers that live tests use.
- [x] Delete the frontend tests that import only deleted code. Update the guard tests that list legacy files.
- [x] Verify with tsc, lint, the full unit suite and `next build`.

### Task 2: Retire `backend/legacy` and the leftover `website/` folder

- [x] Remove `backend/legacy` (37 tracked files plus its local `node_modules`) and `website/`.
- [x] Remove references to both:
  - the legacy golden run and `UNIT_TOKO_LEGACY_MIGRATIONS` in `scripts/test-domain.mjs`;
  - the `test:api:legacy` script in `package.json`;
  - `tsconfig` includes;
  - the CI steps that run `npm ci --prefix backend/legacy`;
  - documentation pointers.
- [x] Verify with tsc, the suite, the build, and the CI YAML parse.

### Task 3: Retire the legacy stylesheets

- [x] For each file imported with `layer(legacy)` in `globals.css`, extract its class selectors and check whether any reachable TSX uses them. Delete unused rules or files. Move the rules still used into the owning component's CSS Module or into `foundation.css`.
- [x] When the layer is empty, remove it from the `@layer` statement and update `css-cascade.test.ts`.
- [x] Compare the visual baseline before and after.

### Task 4: Upgrade dependencies

- [x] Upgrade `next` to 16.4.2 and run `npm audit fix` (sharp, source-map-js, baseline-browser-mapping). Review the other outdated packages with `npm outdated` and upgrade those whose release notes show no breaking change for this app.
- [x] Verify with tsc, lint, the suite, the build and `npm audit --omit=dev` (0 high or critical).

### Task 5: Landing page with a custom 3D scene

- [x] Design direction through the frontend-design skill: a scene that means something for Unit Toko, built from its own vocabulary (packages, branches, the cooperative shop), not stock blobs.
- [x] Capability gate `sceneMode(window)`: returns `"animated" | "still" | "none"` from WebGL support, `prefers-reduced-motion` and save-data. Unit-test it.
- [x] Load the scene as a client island with `next/dynamic` and `ssr: false`. Cap DPR at 2, pause on `visibilitychange`, and dispose on unmount.
  - Ruling: a dynamic `import()` inside the stage effect replaces `next/dynamic`. It is equally client-only and code-split (three.js builds into its own chunks, absent from the page entry), it keeps the server-rendered poster as the fallback, and it lets the landing render in the node test suite. Cost if wrong: a one-line switch back to `next/dynamic`.
- [x] Rebuild the landing copy and layout around it, with the CTA to Masuk and to Katalog.

#### Task 5 design plan (frontend-design, two passes)

**Subject.** Unit Toko sells packaged goods to BNI branches and approved business accounts. What sets it apart from an ordinary shop is the package rule: you order whole packages, each package has fixed contents, a minimum and a multiple, and the store reviews every request. The hero shows that rule rather than describing it.

**Color.**

| Name | Hex | Use |
|---|---|---|
| Dock | `#0F4C52` | Hero and login stage: the loading-dock wall |
| Label | `#F1F4EF` | Page ground; cool label paper, not cream |
| Ink | `#12343A` | Text on light (11.99:1 on Label); muted `#4A6266` (5.86:1) |
| Kraft | `#C69A62` | Cardboard in the scene only; `#E0BE8E` for text on Dock (5.48:1) |
| Marker | `#C2471A` | One highlight: the package total on the white label (4.99:1); caps in the scene use `#E8622A` |

**Type.** DM Sans (existing, variable) for everything readable; headings 700–760 with tight tracking. Saira Stencil One (OFL, self-hosted) only for package numerals: the total on the label and the markings printed on the cartons, the way crates are stencilled.

**Layout.**
```
┌ Dock ───────────────────────────────────────────────────────┐
│ [U] Unit Toko          Katalog   Permintaan saya   [Masuk]  │
│ Kebutuhan cabang,                 ┌──┐┌──┐                  │
│ dihitung per kemasan.             │▓▓││  │  ← pallet, 3D    │
│ lead (44ch)                       └──┘└──┘                  │
│ [Masuk ke akun]  Lihat katalog        ┌ shipping label ───┐ │
├───────────────────────────────────────│ Barang  Isi  Min  │─┤ ← label straddles Dock/Label
│ Label ground                          │ [−] 2 [+]   48    │ │
│                                       └───────────────────┘ │
│ How a request is recorded: an example record's status trail │
│ What the catalog holds: a packing list (table)              │
│ Two ways in: branches/business accounts | store staff       │
└─────────────────────────────────────────────────────────────┘
```
Text is left-aligned throughout. On phones the stage follows the text and the label sits under the stage.

**Principles.**
1. The carton is the one loud element, and it is also the explanation: the stepper on the label changes how many cartons stand on the pallet.
2. The structure carries information. Label fields are real fields (Barang, Isi per kemasan, Minimum, Jumlah). Numbers appear only in the status trail, because that is a real sequence. No eyebrow labels, and no arrows appended to buttons.
3. Motion happens once: the front carton opens on load and its 24 bottles rise. After that, motion only answers the stepper (a carton drops on or lifts off) and a slight pointer parallax. Reduced motion shows the final frame.
4. The page uses the app's own words (kemasan, isi, minimum, permintaan, peninjauan, tagihan), so the landing page teaches what the account will show.

**Review pass: what changed and why.**
- *Split hero with an image on the right* was the first idea and the generic default. It became a full-bleed dock stage, with the shipping label straddling the Dock/Label edge.
- *Numbered "how it works" icons* (four SaaS steps) became one example record's status trail with WIB times, matching the account timeline.
- *Three photo cards* for categories (the current AI-styled editorial illustrations) became a packing-list table that states example packaging and is marked as an example.
- *Abstract 3D shapes* were never considered. The object is a corrugated carton with flaps, tape and stencilled markings.
- *Teal and orange* was checked against the cliché list. It stays because it comes from the client's world (BNI's teal and orange, kraft board, marker pen), and orange appears only on the total and the bottle caps.

### Task 6: Login and core screens

- [x] Login: a two-column layout with the brand panel and a clear sign-in card, showing which account types exist and what to expect next. Error and session states use `OutcomeState`.
- [ ] Account ("Pekerjaan saya"), catalog, drafts and payment screens: apply the workspace kit consistently, using page headers, spacing rhythm, cards and tables.
- [ ] Re-capture the visual baseline and review it.

### Task 7: Close

- [ ] Register rows, ledger, final review, commit, push.
