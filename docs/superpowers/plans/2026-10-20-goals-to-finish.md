# Unit Toko: goals to finish everything (20 October 2026)

This is the finish line, written as checkable goals. It replaces nothing in the next-wave plan; it says when each area counts as done and in what order the remaining work happens.

## Where things really stand (audit, 20 October)

- **Migration from the old demo is complete in function, not in polish.** The old demo had a dashboard, catalog, orders, deliveries, stock, billing, payments, procurement, reports, accounting periods, profile, admin, a customer shop/cart/checkout and a sales register. Every one of these except two has a real, API-backed home in the new app (78 pages, 734 API operations). The two exceptions:
  - The **sales register** (walk-in till) is not in the 97-item requirement register, so it was never rebuilt. It stays out unless the owner adds it.
  - The old **dashboard and reports** are only partly back: the account hub shows the work queue counts and there is one accounting report. There is no receivables/payables aging page and no management summary.
- **API to UI coverage is complete.** The operations without a direct caller are all used through built paths (invoice release, courier, timeline, evidence uploads) or are health/provider endpoints.
- **Requirement register:** 97 items. 5 are proven locally, 89 are implemented but only partly proven ("Sebagian"), 3 are not built: R11 access reviews (waits for owner decisions), F12 unified work queue, M02 delivery-document OCR (needs real Surat Jalan samples). Nothing is proven on staging or production yet, because those environments do not exist yet (Phase F, owner inputs).
- **What feels unfinished is mostly the experience:** each staff page is reached from a long list on the account page, there is no persistent navigation, and page layouts differ from module to module. Long commands are slow (a stock decision took 13 s this morning, 6.8 s now).

## Goals

Each goal is done only when its check passes and the result is committed, pushed and visible in the local demo.

### G1. A cinematic, realistic landing
1. Each act has its own set and light: the shop's packing table with shelves of catalog goods softly out of focus, morning window light with visible light shafts and dust; the road; the branch office interior. Colour grade, gentle film grain and vignette on the cinematic tier.
2. Camera moves like a film: eased dolly and crane moves, a slow push-in on each key moment, focus pulls between the item and the box, a little handheld drift at rest.
3. Higher-fidelity hero objects: creased flaps and corners, glossy semi-transparent tape, a legible printed label, contact shadows and ambient occlusion on every object.
4. A short film of the same story, rendered locally and free the way HyperFrames works (headless Edge on the GPU seeks our pure story function frame by frame; the browser's own WebCodecs encoder writes a WebM), with sub-frame motion blur the live page cannot afford. Used where live 3D cannot run and as a "watch the story" section.
5. Check: GPU screenshots per act at 1440 and 390 px in both tiers, landing tests, frame-time downgrade still works, strict CSP with no remote assets.

### G2. A professional back-office UI
1. One app shell for every staff page: a sidebar grouped by area (Kebutuhan cabang, Toko dan gudang, Keuangan, Pengadaan, Organisasi dan akses), the person's name and role, search across tasks, breadcrumbs, and the work-queue counts as badges. Customer pages keep the shop header.
2. One page pattern everywhere: page header (title, one-line purpose, primary action), filter bar, table or list with sticky header and status badges, detail panel or drawer, and the same empty, loading, denied and error states.
3. A home dashboard per role: the work queue, today's figures for that role (orders waiting, shipments to send, invoices due, payments to check) and shortcuts.
4. Check: the mock review harness renders every staff route at 1440 and 390 px with no layout breaks, axe finds no serious issues, keyboard order is sensible, and the frontend suite stays green.

### G3. Every required function built
1. **F12 unified work queue**: land the prepared work-queue monitoring candidate (next free migration) so every assigned task appears in one queue with age and owner.
2. **F07/R14 reports**: receivables and payables aging, invoice-ready receipts and a read-only auditor view, from the existing ledgers.
3. **R11 access reviews and branch moves** and **M02 delivery-document OCR**: built as soon as the owner decisions and real samples arrive (they are listed in Phase F).
4. Check: each item has its native, client and browser tests, a runbook entry and a ledger row.

### G4. Every function proven
1. Move every "Sebagian" item to "Teruji lokal": run the browser rehearsal for each module and the full backend suite in batches on an idle machine, fix what fails, and record the evidence per item.
2. Retire or rewrite the remaining pending tests (procurement fixtures, latency-pending timeline and invoice browser tests).
3. Check: the ledger shows every item proven locally with a dated evidence link.

### G5. Fast enough to use
1. Stock decision and invoice issue under 3 s on the local probe (from 13.3 s and 30+ s), then toward the 1 s target, using the transaction read memo and narrower reads, never by skipping a check.
2. Check: the latency probe, the two latency-pending test groups back to green, and the invoice browser rehearsal passing inside the 30 s bound.

### G6. Ready to go live (owner)
Hosting, Auth0 tenant, domain and TLS, keys, real master data, real Surat Jalan samples, R11 decisions and the cutover rehearsal (D01–D08 in `docs/quality/operational-inputs-proposals-2026-10-16.md`). These need the owner; everything above can be finished without them.

## Order of work
1. Close today's latency change (regression batch, commit).
2. G2 app shell and page pattern, because it changes how every page feels.
3. G1 landing, with the HyperFrames film once its cost is approved.
4. G5 to under 3 s, which also unblocks the latency-pending tests.
5. G3 F12 and reports, then the dashboard in G2.3.
6. G4 proving pass, module by module.
