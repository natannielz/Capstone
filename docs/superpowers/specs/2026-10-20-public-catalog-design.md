# Public catalog without prices: design (20 October 2026)

## What the owner decided
- Anyone, signed in or not, can browse products. Prices, stock and ordering stay behind sign-in. (Owner answer, 20 October: "Public, no prices".)
- Staff choose what is public: a maker proposes making an **assortment** public, and a different checker approves. Nothing is public until then, so branch-only assortments never leak. (Owner: "Staff choose".)
- Photos become public automatically: once a product in a public assortment has an attached photo that passed the scan and decode, the worker makes a clean public copy. (Owner: "Automatic clean copy".)

## Why this shape
The buyer catalog is private for good reasons:
- Prices are contract prices for BNI branches.
- Each photo is a private evidence original, and every buyer view takes a lease and writes audit rows. Anonymous traffic must never reach that path, because it would turn into database writes per view.

So the public catalog is a separate, read-only projection with its own cached images. It shares the product facts, never the private files or the prices.

## What a visitor sees
- `/katalog`: search, category chips, sort by newest or name, cards with photo, name, package and category.
- `/katalog/[publicId]`: description, package contents, attributes, minimum order and a larger photo.
- Every page says "Masuk untuk melihat harga dan memesan" and links to sign-in, which returns to `/shop`.
- The landing page's "Lihat katalog" goes to `/katalog` for signed-out visitors.

Never shown: price, stock, internal ids, owner scope, assortment, supplier or source codes, GTIN, buyer identifiers.

## Backend (Catalog module owns all of it)

**Migration 121 (`CatalogPublicSchemas`).** Strict validators, the usual empty-target preflight, then indexes and counters, resumable:
- `catalogPublicShowcaseProposals`, `catalogPublicShowcaseDecisions`: maker/checker per assortment.
- `catalogPublicShowcaseHeads`: the current state per assortment: `public` true/false, `revision`, `decisionId`, `fence`.
- `catalogPublicImages`: one row per (skuId, original photo sha256). It holds the rendition (JPEG, at most 800 px, at most 400 KB, `BinData`), its sha256, width, height, `status` active or withdrawn, and timestamps. A unique index on the rendition sha256.
- The usual `idempotencyRecords`, `auditEvents` and `outboxEvents` kinds `catalog.public.showcase.*`.

**Permissions.** `catalog.public.propose` and `catalog.public.approve`, Operations scope, registered in `Permissions.Catalog`. Maker and checker must be different people (the existing independent-person rule).

**`CatalogPublicShowcaseService`** (staff):
- `PreviewAsync` / `ProposeAsync(assortmentId, expectedHeadRevision, makePublic, reason)`.
- `PreviewDecisionAsync` / `DecideAsync(proposalId, expectedRevision, approve|reject)`.
- `ListAsync` (assortments in the actor's scope with their public state and pending proposal).

Approval updates the head in the same transaction as the decision, the audit row, the outbox row and the command receipt. Idempotency keys replay.

**`CatalogPublicQueryService`** (anonymous, no actor):
- `QueryAsync(offset, limit ≤ 48, search ≤ 64 chars, category, sort)` returns `{ total, items, categories }`.
- `ReadAsync(publicId)` returns the item or null.

It reads only SKUs that are `active`, have a current definition, and are listed in an assortment whose head is `public: true` and `status: active`. An item's photo is the active rendition for its current primary photo, or none.

**Rendition worker** (`CatalogPublicImageProcessor`, runs where the evidence validation worker runs):
- Every pass it finds public SKUs whose current primary photo has no active rendition, and withdraws renditions whose SKU stopped being public or whose photo changed.
- For each missing one it re-checks that the photo's validation passed and is current. It then opens the verified original through the store registration and runs the confined decoder in its new render mode. It stores the result.

**Decoder render mode.**
- `UnitToko.ImageDecoder` gets an optional fifth argument `render-jpeg-800`.
- After the unchanged strict decode, it resizes to fit 800 px, strips all profiles and metadata, and writes a baseline JPEG at quality 82.
- Output stays one bounded JSON line, now with `rendition` as base64, at most 400 KB.
- The parent checks the JSON, then checks the rendition again with the strict JPEG envelope and its own sha256.
- Same job limits and pins (the pins change with the release, as for any decoder change).

**Endpoints:**
- `GET /api/v1/public/catalog`, `GET /api/v1/public/catalog/{publicId}`: anonymous, `Cache-Control: public, max-age=60`, no cookies read or set, inside the existing per-IP limit.
- `GET /api/v1/public/catalog/images/{sha256}`: anonymous, `image/jpeg`, `Cache-Control: public, max-age=31536000, immutable`, `ETag`, `X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'`. It returns 404 for withdrawn or unknown renditions.
- Staff: `/api/v1/catalog/public-showcase` list, preview, propose, decide and lookup, with the usual CSRF, origin and actor checks.

## Frontend
- `app/katalog/page.tsx` and `app/katalog/[publicId]/page.tsx`, rendered on the client from the public API. They use the shop look (not the staff frame), with an empty state and a load-failure state.
- `modules/catalog/public-catalog-client.ts` parses strictly, like the buyer client.
- Staff page `Katalog publik` in the workspace nav (Katalog dan persediaan): one row per assortment with its state, a propose action for makers and a review queue for checkers.
- The landing page's "Lihat katalog" goes to `/katalog` when signed out.

## Failure and safety rules
- The public read never touches leases, audits or sessions.
- If the showcase storage is missing, the public endpoints answer 503 `public_catalog_unavailable`, never an empty success.
- Making an assortment private again hides its items on the next read. Their renditions are withdrawn on the next worker pass, and image requests return 404 at once, because the image endpoint re-checks that the SKU is still public.
- No price field exists in any public DTO. The OpenAPI test asserts this.

## Tests
- Native tests:
  - the maker/checker chain, replay and conflicts;
  - the public query only shows items in public active assortments, never prices;
  - the image endpoint re-checks visibility;
  - the render worker (clean original becomes a rendition; private again means withdrawn; changed photo means a new rendition);
  - the decoder render mode (EXIF stripped, size bounded, corrupt input refused).
- API boundary tests: anonymous GETs work without cookies, every other method is refused, and there are no price fields.
- Frontend: client parsing, then page rendering in the mock review harness at 1440 and 390 px.
