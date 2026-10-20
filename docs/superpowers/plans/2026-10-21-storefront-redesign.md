# Storefront redesign plan (21 October 2026)

Source: `reports/Unit Toko shopping flow benchmarks.md`, sections "Header and navigation by audience" and "Priorities and acceptance checks". Owner decisions are in `docs/superpowers/specs/2026-10-20-public-catalog-design.md`, and the owner's request of 21 October:
- The logo returns to the landing page.
- Staff get a clear sign-in button.
- Guests see only the basic catalog: no cart, no orders.
- Use e-commerce references for the flow.

Each phase is committed and pushed, with a check, before the next one starts.

## Phase A: one storefront header and a staff door (P0.1, P0.2, P0.4)
- `components/store/store-frame.tsx`, in three states: guest, buyer and staff.
  - Thin utility bar: Cara pemesanan cabang, Bantuan (WhatsApp), Ruang kerja petugas.
  - Main bar: logo to `/`, Kategori, search, then "Masuk" for guests, or keranjang plus the account menu for signed-in users, plus a "Ruang kerja" button for staff.
  - Mobile: top bar plus bottom tabs (Katalog, Keranjang, Pesanan, Akun). Guest taps on Keranjang, Pesanan or Akun go to sign-in and come back.
  - One footer.
- Frames are chosen by route: buyer routes (`/shop`, `/ordering/drafts`, `/ordering/checkout`, `/ordering/orders`, `/ordering/requests`) get the store frame, and back-office routes keep `WorkspaceFrame`. Inside the store frame, page-level brand links and "Akun saya" links are hidden.
- Every logo goes to `/`, including the staff sidebar. "Pekerjaan saya" stays the first sidebar item.
- `/workspace` becomes the real staff home (the account hub inside the workspace frame). The footer and utility bar point staff to sign-in with `returnPath=/workspace`.
- `/catalog/browse` redirects to `/shop`. The buyer-facing noun becomes "produk/barang"; "kemasan" stays only for the pack line.
- Check: every buyer route renders the store header, there is no logo link to anything but `/`, and guests see "Masuk" in the first viewport at 390 and 1440 px.

## Phase B: public catalog backend (spec, without photos)
- Migration 121: the showcase proposals, decisions and heads, maker/checker per assortment, and permissions `catalog.public.propose` and `catalog.public.approve`.
- Anonymous `GET /api/v1/public/catalog` and `/{publicId}`. No price fields.
- A staff "Katalog publik" page.

## Phase C: guest catalog and the sign-in loop (P0.3)
- `/katalog` and `/katalog/[publicId]` use the same card and grid as `/shop`, with "Masuk untuk lihat harga" in the price slot and no price sort.
- "Masuk untuk memesan" returns to the priced product page with the chosen quantity.
- A guest on `/shop` goes to `/katalog`, and a signed-in visitor on `/katalog` goes to `/shop`.

## Phase D: public photos
- Decoder render mode, a rendition worker, the public image endpoint, and a card-size rendition.

## Phase E: purchase path (P1)
- `/cart` as a real keranjang that saves itself, with steppers.
- A one-page checkout whose button reads "Ajukan persetujuan" when a spending rule applies.
- Approval states for requester and approver.
- "Pesanan saya" with status tabs, the five-step progress view and "Pesan lagi".

## Phase F: discovery depth (P2)
- Search synonyms and codes, filters with chips, a card stack with unit price and minimum order, "Muat lebih banyak" with position restore, and "Pernah dibeli".

## Progress (21 October, night)
- Phase A: done (fd767b1).
- Phase B: done. The staff "Katalog publik" page (`/catalog/public`) lists shop catalogs by shop and warehouse name, with propose / approve / reject by two different people (d818cb8).
- Phase C: done (96acd32).
- Phase E, cart: `/cart` is the buyer's latest branch draft. The product page has "Tambah ke keranjang", quantities save on their own, and changed prices are shown for the buyer to accept. A lost answer is resent with the same key. The header shows the cart count (d818cb8).
- Phase E, checkout: delivery is entered on the checkout page, with "Isi dari alamat cabang". The submit button reads "Ajukan untuk persetujuan" or "Ajukan pesanan". There is no second header (7fe50f7).
- Phase E, orders: "Pesanan saya" shows order cards with a status badge, "Lihat rincian" and "Pesan lagi" into the cart. Filters are folded away (5b35175).
- Product page in marketplace order (a166d73).
- Local full pilot rebuilt and restarted. The guest catalog stays empty until staff open a shop catalog, and no pilot account holds `catalog.public.propose` / `catalog.public.approve` yet (account changes are the owner's decision).
- Still open: D (public photo renditions), F (search depth, filters, "Pernah dibeli"), status tabs backed by a server-side filter, and a backend regression run of AccessHttpTests on an idle machine.
