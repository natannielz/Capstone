# Catalog discovery UX: category navigation, search, filters, sorting and product listing pages (Shopee, Tokopedia, Lazada, Blibli, Amazon, Alibaba.com/1688, Indonesian B2B)

Research date: 21 October 2026. Scope: how the large marketplaces structure product discovery, plus published research (Baymard Institute, NN/g), applied to a small B2B shop catalog of about 250–2,000 products in about 10 categories.

Source caveats, read first:
- Live Shopee, Blibli and Amazon pages are JavaScript-rendered or block bots. Blibli returned HTTP 403 and Bhinneka's search URL returned 404. Platform details therefore come from (a) the server-rendered pages I could fetch (a Tokopedia `/find/` page, a Lazada `/tag/` page, Amazon search pages cached by Exa, and the Ralali and Mbizmarket search pages), (b) official help or seller-education pages, and (c) third-party scraper or automation docs that list the platforms' real URL parameters and card fields. Scraper docs describe the data behind a card, not always what the card shows. This is flagged where it matters.
- Several Indonesian UX case studies are from 2017–2022, and some metrics are self-reported by designers in portfolios or LinkedIn profiles. Both are labelled as such.
- Baymard's full guidelines are paywalled. Only the statistics published in its free articles are used here.

---

## 1. Category navigation: mega-menus vs category pages vs category chips; number of levels; category landing pages; the "Kategori" entry on mobile

### Takeaway
The Indonesian marketplaces treat category browsing as secondary to search. There is a "Kategori" entry in the header (web) or the menu (app), a 3-level taxonomy, and keyword landing pages for SEO. Most narrowing happens with filters on the listing page, not through deep category trees. The research agrees for a small catalog: show product categories directly as the top-level navigation (especially on mobile), keep the tree shallow, and use filters rather than extra subcategories for products that share attributes. An intermediary category page helps only when it focuses on subcategories.

### Cited Findings
- **Tokopedia web header:** the logo is followed by a single "Kategori" trigger, then "Masuk / Daftar". The homepage links to a category index at `/p`. — [Tokopedia homepage](https://www.tokopedia.com/); [Tokopedia /find page](https://www.tokopedia.com/find/barang-unik-terbaru)
- **Tokopedia taxonomy depth:** product breadcrumbs are 3 levels deep, e.g. `makanan-minuman/minuman/kopi-bubuk` (Makanan & Minuman → Minuman → Kopi Bubuk). — [browse.sh Tokopedia skill (expected output)](https://www.browse.sh/skills/tokopedia.com/compare-products-by-price-rating-deliver-b82t29)
- **Tokopedia keyword landing pages:** SEO pages at `/find/<keyword>` (e.g. "Jual Barang Unik Terbaru Terbaik – Harga Murah Oktober 2026") are full listing pages with a "Filter" panel starting with "Kategori" (e.g. Fashion Wanita, Aksesoris Wanita), a result count and a sort control. — [Tokopedia /find page](https://www.tokopedia.com/find/barang-unik-terbaru)
- **Lazada keyword landing pages:** Lazada uses the same pattern at `/tag/<keyword>/`. These are listing pages with facets ("Service", "Shipped From", price, material). — [Lazada tag page](https://www.lazada.co.id/tag/kasur-the-erland-120x200/)
- **Tokopedia app, historical (2017):** about 40 shopping categories were grouped into 4 classes (Gaya Hidup, Teknologi, Kategori Lain, Pembayaran & Top Up). Inside a category page, subcategories were hidden behind the bottom "Filter" button. A UX critique called this the main navigation failure and proposed a bottom bar of "Sub Kategori / Sort / Grid" with a tree-style subcategory picker. — [SabtUX Medium case study, 2017](https://medium.com/sabtux/case-study-re-ux-tokopedia-android-app-6780083a83a6)
- **Shopee, small user study (2019, n=4):** all 4 participants went straight to search rather than categories. One participant could not find the categories section on the home tab because it required scrolling, and scrolled past it the first time. Participants asked for clearer categories and subcategories. — [Prototypr Shopee case study, 2019](https://blog.prototypr.io/a-ux-case-study-on-shopee-and-my-redesign-of-it-cb0cabd0d8d0)
- **Shopee search results** show "Toko Terkait" (related shops) above the product results column. Paid results appear in the first row. — [Shopee Seller Education: Komponen Halaman Pencarian](https://seller.shopee.co.id/edu/article/17519)
- **Blibli** groups daily needs (food, drinks, cleaning, household) into a dedicated "Bliblimart" category destination with its own landing page and fast-delivery options. — [Blibli Bliblimart category page](https://www.blibli.com/c1/bliblimart/53400?destinationPageType=BLIBLIMART); [Blibli homepage](https://www.blibli.com/)
- **Mbizmarket (B2B)** tells buyers to find products with search, or with "navigasi kategori dan subkategori produk". — [Mbizmarket Cara Transaksi](https://www.mbizmarket.co.id/cara-transaksi)
- **Baymard, mobile navigation:** make product categories the top-level navigation items on mobile sites (33% of sites don't). On *small-catalog* sites, desktop testing showed a single "Shop" top-level item was no hindrance, because the taxonomy is much simpler. — [Baymard: Product Categories as Top-Level Navigation (33% Don't)](https://baymard.com/research-articles/main-navigation-product-categories)
- **Baymard, intermediary category pages** ("Consider providing intermediary category pages", 13% don't): the page's core purpose is to help users get to the right subcategory. Pages that showcase sales instead of subcategories (Gilt) perform poorly. Chewy's "Dog" page, which focuses on subcategories, is the positive example. — [Baymard: Intermediary Category Pages](https://baymard.com/research-articles/ecommerce-sub-category-pages)
- **Baymard 2024 re-verification:** the 2024 Product Finding update (5,550+ research hours, 219+ test sessions) lists "Intermediary category pages" among the 6 recommendations it adjusted. The article text I retrieved was cut off before the new wording (see Gaps). — [Baymard 2024 Product Finding Research Update](https://baymard.com/research-articles/product-finding-2024-launch)
- **Baymard, overcategorization:** splitting product types that share attributes into separate subcategories forces users into overly narrow scopes, and 75% of benchmarked sites get this wrong. One list with the types available as filters resolves the problem for most users. — [Baymard: Overcategorization (75% Get It Wrong)](https://baymard.com/research-articles/ecommerce-over-categorization)
- **NN/g, mega menus:** large two-dimensional dropdowns suit many options and reveal lower-level pages at a glance. For hover-triggered menus, wait 0.5 s before opening, display within 0.1 s, and close only after the pointer has been outside for 0.5 s (article from 2017). — [NN/g: Mega Menus Work Well](https://www.nngroup.com/articles/mega-menus-work-well/)

### Inferences
- For about 10 categories and up to 2,000 products, a 2-level tree (category → a few subcategories) is enough. Tokopedia needs 3 levels for millions of SKUs. Within a category, distinctions such as size, pack, brand or variant belong in filters, not new subcategories (Baymard overcategorization).
- A full mega-menu is not needed. A simple "Kategori" dropdown or panel listing the ~10 categories (optionally with icons) matches Tokopedia's header and Baymard's small-catalog finding. On mobile, list the categories themselves as top-level navigation items or as a "Kategori" tab/sheet, not behind an extra "Shop" level.
- Category chips work well on the listing page itself: a horizontal row of subcategory chips at the top of a category list, or of category chips on a search results page. They fill the role of an intermediary page without an extra click and avoid the 2017 Tokopedia problem of subcategories hidden inside "Filter".
- If a category landing page is used, it should be subcategory-first, with promos secondary (Baymard/Gilt vs Chewy).

### Gaps
- I could not render the 2026 Shopee or Tokopedia app home/category screens, so the current position of the mobile "Kategori" entry (home grid icons, bottom tab, or menu) is not verified with a 2025–2026 source.
- Baymard's adjusted 2024 recommendation on intermediary category pages was truncated in the retrieved text.
- Desktop mega-menu use on Shopee, Lazada and Blibli was not verified.

---

## 2. Search: autocomplete and suggestions, recent searches, results page layout, zero results, typo tolerance, searching by SKU/code

### Takeaway
Search is the main discovery path on Indonesian marketplaces. The results page combines a result count, sort control, filter panel, related-shop row and ads in the first row. Research is clear on the essentials: limit autocomplete to about 10 suggestions on desktop and 4–8 on mobile, tolerate typos inside autocomplete, support exact name, model and SKU searches, map product-type queries to categories, keep the query in the box, and never leave a zero-results page as a dead end. Indonesian B2B sites use zero results to push RFQ/tender forms.

### Cited Findings
**Platforms**
- **Shopee search page components** (official seller education): 1) Kolom Pencarian, 2) Filter Pencarian, 3) Toko Terkait, 4) Kategori Urutan Pencarian, 5) Kolom Hasil Pencarian. Ads appear in the first row of results, and the page shows "Jumlah Halaman" (page count). — [Shopee Seller Education 17519](https://seller.shopee.co.id/edu/article/17519)
- **Tokopedia search scopes:** the search URL has `st=product` vs `st=shop`, i.e. separate product and shop result scopes. — [browse.sh Tokopedia skill](https://www.browse.sh/skills/tokopedia.com/compare-products-by-price-rating-deliver-b82t29)
- **Tokopedia "search reimagine" project** (designer portfolio, self-reported):
  - It split low-intent from high-intent keywords.
  - The initial state was simplified to mostly text.
  - Autocomplete keeps shop and product suggestions clearly separated.
  - A "Smart Guide Widget" suggests more specific keywords.
  - Quick filters were added.
  - Reported results: +18% clicks on initial state and autocomplete, +5.06% CVR on non-seasonal campaigns, +13.8% CVR on seasonal campaigns.
  - High-intent queries got more detailed product cards, sometimes a list layout in certain categories.
  - [Galuh Akhdandika portfolio: New Search Reimagine](https://galuh-akhdandika.framer.website/new-search-reimagine)
- **Tokopedia search unification** (former Tokopedia search UX designer, self-reported on LinkedIn): reduced searches with no results by 70%, and a filtering redesign across 20+ pages raised filter usage by 4.06%, CTR by 1.25% and conversion by 3.03%. — [M. Azwar Adli, LinkedIn](https://linkedin.com/in/azwaradli)
- **Indonesian academic usability study** (Fasilkom UI, 2020; 655 survey respondents plus 15 moderated usability participants on Tokopedia, Bukalapak and Shopee): found 68 user behaviours and problems in product search, grouped into keyword problems, search results and filtering. A redesign solved 51 of them, with a 90.5% task success rate in re-testing with 20 participants. — [Lontar UI thesis record](https://lontar.cs.ui.ac.id/Lontar/opac/themes/newui/detail.jsp?id=48229&lokasi=lokal)
- **Amazon zero results:** the page shows "No results for …" with "Try checking your spelling or use more general terms", plus a help link. — [Amazon search page (cached)](https://www.amazon.com/s?page=10&srs=7481458011)
- **Ralali zero results:** "Tidak menemukan produk yang Anda cari?" pushes the "Ralali Tender" feature with "Pelajari lebih lanjut" and "Dapatkan Sekarang", both linking to a request-for-quotation form. — [Ralali search page](https://www.ralali.com/search/kopi)

**Research: autocomplete**
- 80% of e-commerce sites provide autocomplete, but only 19% get every implementation detail right. — [Baymard: 9 Autocomplete Best Practices](https://baymard.com/research-articles/autocomplete-design)
- Keep the list to at most about 10 suggestions on desktop and about 4–8 on mobile. Beyond that users ignore suggestions or stall, and most pick from the first few. — [same](https://baymard.com/research-articles/autocomplete-design)
- Style category-scope suggestions ("in Minuman") differently from query suggestions. Reduce visual noise: product thumbnails and trending blocks in autocomplete can overpower the query suggestions. — [same](https://baymard.com/research-articles/autocomplete-design)
- 69% of sites don't give relevant autocomplete suggestions for closely misspelled queries. Correcting spelling only on the results page comes too late for users who rework the query in the box. — [Baymard: Autocomplete for Misspellings](https://baymard.com/research-articles/offer-autocomplete-suggestions-for-misspellings)
- 58% of sites don't copy the highlighted autocomplete suggestion into the search field, which stops users from editing a suggestion. Users should be able to cycle through suggestions with the keyboard. — [Baymard: Copy Active Suggestion](https://baymard.com/research-articles/copy-search-suggestion-to-search-field)

**Research: query types and search quality**
- 46% of desktop sites, 58% of mobile sites and 64% of apps have "mediocre or worse" search UX. — [Baymard: Search Query Types](https://baymard.com/research-articles/ecommerce-search-query-types)
- **"Exact" searches** (12% of sites have issues): users type or paste product names or model numbers. A participant was grateful to find a product by SKU/model number at Home Depot. Users quickly conclude a site doesn't carry a product when an exact model search fails. Phonetic misspellings and alternate names (e.g. Nesquik vs Nestlé Quik) must also resolve. — [same](https://baymard.com/research-articles/ecommerce-search-query-types)
- **"Product type" searches** (20% have issues): when a query matches a category 1:1, autodirect to that category, or promote matching categories on the results page. — [same](https://baymard.com/research-articles/ecommerce-search-query-types)
- **Other query types:** "Feature" searches (e.g. "red dresses", "$100–$200 backpacks") have issues at 39% of sites, "Use case" searches at 43%, and "Abbreviation and symbol" searches at 54%. — [same](https://baymard.com/research-articles/ecommerce-search-query-types)
- **Older benchmark (2014, top-50 US sites):**
  - 16% didn't support search by product name or model number, and another 18% returned nothing useful for a single-character typo.
  - 70% required the site's exact jargon (e.g. "blow dryer" vs "hair dryer").
  - 60% didn't support symbols and abbreviations.
  - 65% of test subjects needed 2 or more queries, yet only 34% of sites kept the query in the search field on the results page.
  - [Smashing Magazine / Baymard, 2014](https://www.smashingmagazine.com/2014/08/the-current-state-of-e-commerce-search/)

**Research: zero results**
- About 50% of sites fail to give users effective ways to recover from a no-results search. — [Baymard: 5 Strategies for "No Results" Pages](https://baymard.com/research-articles/no-results-page)
- Proven strategies:
  - Feature related categories, or better, a category with a filter applied.
  - Suggest alternative searches by running query permutations, with a preview of the top 3–5 products each. Auto-apply the alternative if only one is found, with a notice.
  - Display a sales phone number, chat and help links directly.
  - Promote popular products and categories.
  - "Search tips" alone are rarely read and are not enough.
  - [same](https://baymard.com/research-articles/no-results-page)

### Inferences
- A B2B shop catalog needs strong exact-match on SKU/product code and product name, including partial codes, codes without separators, and pasted text. Buyers reorder from invoices or POs (cf. Baymard "Exact" searches, and B2B trade stores that offer search by name, keyword or SKU; see section 7).
- Indonesian pantry, office and cleaning goods carry many units and abbreviations: ml/L, gr/kg, pcs, rim, lusin, dus/karton, A4/F4. Brand-as-generic terms are common (Aqua → air mineral, Rinso → deterjen, Tipp-Ex → correction pen). This falls under Baymard's weakest query types (Abbreviation/Symbol 54%, jargon 70% in 2014), so a synonym table is worth more than a fancy UI.
- With about 10 categories, product-type queries such as "kopi", "tisu" or "kertas" should autodirect to, or prominently promote, the matching category, keeping the category's filters.
- Zero results for a B2B shop should offer: a typo-corrected alternative, related categories, a WhatsApp or "Minta penawaran" (request this item) link like Ralali Tender, and popular products. A "check your spelling" message alone is the Amazon-style minimum Baymard calls insufficient.
- Recent searches and previously bought items are high-value suggestions in a repeat-purchase B2B context (see the Blibli "pernah dibeli" catalog note in section 7). This is an inference: no source measured recent-search value specifically.

### Gaps
- I could not observe the 2025–2026 Shopee or Tokopedia autocomplete or empty-search screens directly: recent searches, trending keywords, image search, or whether SKU-like queries resolve.
- I found no public data on typo tolerance specifically for Bahasa Indonesia queries on these marketplaces.

---

## 3. Filters and sorting on listing pages: which filters, desktop left rail vs mobile bottom sheet, default sort options, applied-filter chips

### Takeaway
All the Indonesian marketplaces share one model:
- **Sort:** a short, mostly flat set. Relevance is the default ("Paling Sesuai" or "Terkait"), plus newest, best-selling or reviews, and price ascending/descending.
- **Filters:** dominated by price range, seller location, rating, shop tier (Mall/Official/Star), shipping or service options (free shipping, instant/same-day, COD) and promotions (discount, Grosir).
- **Layout:** a left rail on desktop, and a filter icon opening a full-screen or overlay panel with an explicit "Pakai/Terapkan" button on mobile.

Baymard's benchmark says the essential filters are price, rating, brand, plus category-specific ones. The essential sorts are price, rating, best-selling and newest. Applied filters belong in a removable-chip overview, and result counts belong next to filter options.

### Cited Findings
**Tokopedia**
- **Sort ("Urutkan"):** Paling Sesuai (default), Ulasan, Terbaru, Harga Tertinggi, Harga Terendah. The URL values are `ob=23` (Paling Sesuai), `5` (Ulasan), `9` (Terbaru), `4` (Harga Tertinggi), `3` (Harga Terendah). — [browse.sh Tokopedia skill](https://www.browse.sh/skills/tokopedia.com/compare-products-by-price-rating-deliver-b82t29); the default "Urutkan: Paling Sesuai" is visible on the [Tokopedia /find page](https://www.tokopedia.com/find/barang-unik-terbaru)
- **Filters, all exposed as URL params on `/search`:**
  - rating 4★ and up (`rt=4,5`)
  - shipping: Instan / Same Day (`shipping_child=…`)
  - price min/max (`pmin`/`pmax`, in Rupiah)
  - seller city (`fcity`)
  - shop tier: Mall (`shop_tier=2`) or Power Shop (`shop_tier=3`)
  - condition: Baru/Bekas
  - COD only
  - discounted only (`is_discount=true`)
  - [browse.sh Tokopedia skill](https://www.browse.sh/skills/tokopedia.com/compare-products-by-price-rating-deliver-b82t29)
- **Tokopedia filter redesign** (Tokopedia Design, 2019, mobile app):
  - Research findings: Filter and Sort were the most-used and most-important tools, and Layout (grid/list toggle) the least. Harga and Lokasi were the most important filters; Harga Terendah and Ulasan the most important sorts.
  - Design changes: "hero options" for each filter shown on the first filter screen, personalised from the user's past choices (e.g. their frequent cities) and from the options with the most results (e.g. Jepara for wooden furniture). The price slider sits in the thumb zone. Chips are used for options.
  - Usability tests: a no-button auto-apply design on a 3/4-height sheet was ignored or seen as annoying, and users wanted an explicit "Apply" tap for the "I've done something" feeling.
  - Reported result: +26.6% add-to-cart conversion rate on Android.
  - [Tokopedia Design Medium, 2019](https://medium.com/tokopedia-design/small-things-matter-redesign-filter-of-tokopedia-app-c6dbe6ccae6e) (retrieved via Exa search highlights; direct fetch returned 403); also summarised at [casestudy.club](https://www.casestudy.club/case-studies/redesigning-tokopedia-s-search-filter)

**Shopee**
- **Sort** ("Kategori Urutan Pencarian"), officially 4 options:
  - **Terkait:** relevance between product info and keyword.
  - **Terbaru:** product creation time, or when it was boosted with "Naikkan Produk".
  - **Terlaris:** units sold per month.
  - **Harga:** high→low or low→high.
  - [Shopee Seller Education 17519](https://seller.shopee.co.id/edu/article/17519)
- **Filters** ("Filter Pencarian") range "from Metode Pembayaran to Merek". — [Shopee Seller Education 17519](https://seller.shopee.co.id/edu/article/17519)
- **Mobile filter panel, as described in 2021:** opened from a filter icon at the top right. Sections:
  - Lokasi, at the very top, by region
  - Metode Pembayaran (COD, cicilan)
  - Kategori
  - Batas Harga (minimum and maximum fields)
  - Tipe Penjual (Shopee Mall, Star, Star+, etc.)
  - Penilaian (rating)
  - Pengiriman (e.g. Shopee Express)
  - Program Promosi
  - Brands

  Selections are confirmed with "Pakai". — [Tomtekno, 2021](https://www.tomtekno.com/2021/01/cara-mengatur-filter-pencarian-di-shopee.html)
- **Grosir:** Filter > Program Promosi > "Grosir" (also "Gratis Ongkir", "Murah Lebay"). Products with wholesale tiers carry a "Grosir" label under the product image in search results. — [Finansialfokus, 2025](https://finansialfokus.com/detail/46383/cara-beli-barang-grosir-di-shopee-dan-keuntungannya)
- **Programmatic sort values** behind the search API: relevancy / sales / price_asc / price_desc / newest, with min/max price. — [Apify Shopee Scraper ID](https://apify.com/pumpkin_jingo/shopee-scraper-id.md)

**Lazada**
- **Listing facets** observed on a Lazada Indonesia tag page:
  - "Service": LazFlash, Installation, LazMall, New, Priority Delivery, Cash On Delivery, Free Shipping, Economy Delivery, Coins
  - "Shipped From": regions such as Jabodetabek, West/East/Central Java, plus individual cities
  - Price (Max)
  - category-specific attributes, e.g. Material
  - [Lazada tag page](https://www.lazada.co.id/tag/kasur-the-erland-120x200/)
- A secondary blog lists Lazada filters for price range, shipping (free / delivery time) and seller location, and sorting by popularity, price, seller rating or relevance. This is a lower-quality source and I did not verify the exact labels. — [HaloNiaga, 2025](https://www.haloniaga.com/cara-mencari-seller-di-lazada/)

**Blibli**
- Blibli's help centre confirms "Urutan" (sort) and "Filter" on product list pages across desktop, mobile web and app. It does not list the options. — [Blibli FAQ: produk yang sama dengan harga berbeda](https://www.blibli.com/faq/belanja/produk-yang-sama-dengan-harga-berbeda/)
- A 2025 app release note says "Fresh by Bliblimart" results can be freely sorted. — [Blibli App Store listing](https://apps.apple.com/id/app/blibli/id1034231507?l=id)

**Amazon**
- **Sort by:** Featured (default), Price: Low to High, Price: High to Low, Avg. Customer Review, Newest Arrivals, Best Sellers. — [Amazon search page (cached)](https://www.amazon.com/s?i=merchant-items&me=A3JPM3Q1OKJJPX)
- **Left sidebar** includes Department, Customer Reviews ("4 Stars & Up"), Deals & Discounts (Today's Deals, All Discounts), Condition (New/Used) and Availability ("Include Out of Stock"). — [Amazon search page (cached)](https://www.amazon.com/s?page=10&srs=7481458011)
- **Mobile:** the same options sit under a "Refine" menu. — [Unanswered.io guide, 2026](https://unanswered.io/guide/how-to-sort-and-filter-by-price-on-amazon)

**Alibaba.com and 1688**
- **Alibaba.com sort:** relevance/best match, sales ("most orders"), supplier response rate. There is **no price sort**: price is bounded with min/max instead.
- **Alibaba.com filters:**
  - price range and MOQ range
  - supplier review score (4 / 4.5 / 5)
  - supplier country
  - Trade Assurance, Verified Supplier, Verified Pro, Alibaba Guaranteed
  - sample available
  - local warehouse, delivery within N days
  - supplier and product certifications

  [ReefAPI Alibaba docs](https://reefapi.com/docs/alibaba); [Omkar Alibaba scraper: Products by Category](https://www.omkar.cloud/tools/alibaba-scraper/products-by-category)
- **1688 sort:** relevance (default), price ascending/descending, `booked` (30-day transactions), `newOffer`. Filters: price range and supplier province (MOQ in some tools). — [browse.sh 1688 skill](https://browse.sh/skills/1688.com/search-wholesale-l33zbe); [Apify 1688 Search Scraper](https://apify.com/datacach/1688-search-scraper)

**Indonesian B2B**
- **Mbizmarket filter rail:**
  - Kategori ("Lihat Semua")
  - Lokasi ("Lihat Selengkapnya")
  - Harga, with "Produk Dengan Pajak / Produk Tanpa Pajak" plus Rp min/max
  - Status Verifikasi

  [Mbizmarket catalog search](https://www.mbizmarket.co.id/catalog/search)
- **Ralali sort:** only "Harga Terendah / Harga Tertinggi" in the server-rendered template. — [Ralali search page](https://www.ralali.com/search/kopi)

**Research: filters and sorting**
- **5 essential filter types:** price, user rating, color, size, brand. 51% of sites don't provide them all. — [Baymard: Product List UX 2025](https://baymard.com/research-articles/current-state-product-list-and-filtering)
- **4 essential sort types:** price, user rating, best-selling, newest. 68% of desktop and 69% of mobile sites don't offer all four. — [same](https://baymard.com/research-articles/current-state-product-list-and-filtering)
- **Applied filters overview:** always show one (20% don't). The 2026 PLP guide says to display applied filters as removable chips above or next to the grid (28% don't), show result counts next to each filter option, support multi-select within a filter type, and include category-specific filters. — [Baymard 2025](https://baymard.com/research-articles/current-state-product-list-and-filtering); [Baymard PLP UX guide, 2026](https://baymard.com/blog/product-listing-page-plp-ux)
- **Desktop vs mobile layout:** keep filters visible in a persistent panel on desktop. On mobile, use a sticky filter drawer triggered by a fixed "Filter" button. — [Baymard PLP UX guide, 2026](https://baymard.com/blog/product-listing-page-plp-ux)
- **Multi-select ("OR" logic):** 15% of sites don't let users combine values within the same filter type. — [Baymard: Combining Filter Options](https://baymard.com/research-articles/allow-applying-of-multiple-filter-values)
- **Promoted filters:** 61% don't promote important filters at the top of the list. The first action of most participants on a list was to look for filters. Promoted filters must also stay in their original place. — [Baymard: Promoting Important Filters](https://baymard.com/research-articles/promoting-product-filters)
- **Horizontal filter toolbars:** be cautious. They suit sites with roughly 8 or fewer filters. — [Baymard 2025](https://baymard.com/research-articles/current-state-product-list-and-filtering)
- **Filters for displayed attributes:** 38% of sites lack filters for attributes they display on list items. — [Baymard: Filters for List Item Info](https://baymard.com/research-articles/have-filters-for-list-item-info)
- **Explain jargon:** 66% don't explain industry-specific filter terms. — [Baymard: 10 Desktop UX Best Practices](https://baymard.com/research-articles/desktop-ux-ecommerce)
- **Findability (2015):** 40% of test subjects could not find a site's filtering options at some point despite looking for them. Lists of 10+ filter values need truncation, and inline scroll areas performed worse. — [Smashing Magazine / Baymard, 2015](https://www.smashingmagazine.com/2015/04/the-current-state-of-e-commerce-filtering/)
- **Impact on abandonment:** sites with mediocre product-list usability saw 67–90% abandonment, versus 17–33% for slightly optimised toolsets on the same product-finding tasks. — [Baymard Product Lists research overview](https://baymard.com/research/ecommerce-product-lists)
- **NN/g, mobile filter tray:** show facet controls in a tray or overlay so some results stay visible behind it ("continuous visibility of results"). Amazon's app and eBay's mobile site were the examples. — [NN/g: Mobile Faceted Search with a Tray](https://www.nngroup.com/articles/mobile-faceted-search/)

### Inferences
- **Sort for the B2B shop:** Paling Sesuai (default on search) or Terlaris (default on category), Terbaru, Harga Terendah, Harga Tertinggi. Rating/Ulasan sort is less useful when a single-seller B2B shop has few reviews. "Sering dibeli" or "Pernah dibeli" (bought before, per logged-in customer) is the B2B equivalent of social proof (inference).
- **Filters to drop:** marketplace filters that make no sense for a single seller. Lokasi penjual, shop tier and courier type are the most prominent marketplace filters, but they exist because many sellers ship from many cities.
- **Filters to keep, Indonesian labels:**
  - Kategori/Subkategori
  - Merek
  - Harga (min/max)
  - Ketersediaan (Tersedia / Pre-order)
  - Promo / Harga grosir
  - Ukuran/Kemasan (pcs, pack, dus/karton)
  - category-specific attributes, e.g. paper size (A4/F4), gramasi, volume (ml), flavour
- **Layout:** with roughly 5–7 filters, a desktop left rail or a horizontal bar both work (Baymard: horizontal is fine at ≤8). Mobile: a "Filter" button that opens a bottom sheet or tray, with an explicit "Terapkan" button, plus a row of quick-filter chips and an applied-filter chip row with "Hapus semua". This matches the Tokopedia apply-button finding and Baymard's chip overview.
- **Price filters for guests:** if prices are hidden from guests (section 7), price sort and price filter must also be hidden or disabled for guests, because sorting by price would reveal relative pricing (inference).

### Gaps
- Exact current (2026) filter-panel labels on Shopee desktop and on Blibli could not be captured directly. The Shopee list is from a 2021 article plus official sort definitions.
- I could not confirm whether Shopee and Tokopedia show applied-filter chips on desktop or mobile in 2026.
- I found no public data on Lazada's or Blibli's exact sort labels in Indonesian.

---

## 4. Product cards: what each card shows, image aspect ratio, columns on desktop and mobile, quick add-to-cart

### Takeaway
Indonesian marketplace cards are dense and follow one convention:
- square image with a discount or promo badge
- 2-line truncated name
- bold price, with a strike-through original price
- rating plus "X terjual" sold count (rounded, e.g. "70+ terjual", "4.7K sold")
- seller city
- shop-tier and free-shipping badges, with "Grosir" on Shopee

Amazon adds delivery date, unit price, "bought in past month" and a low-stock line. B2B cards replace social proof with a price range or "Mulai dari", MOQ, tier hints and buyer-relevant business data (tax status, verification). Research recommends showing price per unit for varying pack sizes, uniform attributes, and multiple thumbnails. Quick views help visually driven products and hurt spec-driven ones. Multi-action cards (add-to-cart on the card) cause misclicks unless actions are clearly grouped.

### Cited Findings
**Tokopedia**
- **Card contents** (from the server-rendered `/find` page): square image (rendition `white-pad…200:200`), discount badge ("80%"), long product name, price "Rp19.932", strike-through "Rp100.000", "Hemat s.d 8% Pakai Bonus", rating "5.0", "12 terjual", shop badge. — [Tokopedia /find page](https://www.tokopedia.com/find/barang-unik-terbaru)
- **Data behind each card:** price, original price, discount %, rating, sold count as displayed ("27 terjual", "70+ terjual"), shop name, shop city, shop tier, free-shipping flag. Courier options are "not exposed on search card". — [browse.sh Tokopedia skill](https://www.browse.sh/skills/tokopedia.com/compare-products-by-price-rating-deliver-b82t29)

**Shopee**
- **Data behind each card:** price, original price, discount %, rating, rating count, sold ("terjual"), stock, shop location (e.g. "Kab. Bandung Barat"), Mall flag. — [Apify Shopee Scraper ID](https://apify.com/pumpkin_jingo/shopee-scraper-id.md)
- **Badges:** a "Grosir" label (blue) under the image for products with wholesale tiers ([Finansialfokus](https://finansialfokus.com/detail/46383/cara-beli-barang-grosir-di-shopee-dan-keuntungannya)), and a green-truck "Gratis Ongkir" logo on products in the free-shipping programme ([Inspirasi Shopee, 2021](https://shopee.co.id/inspirasi-shopee/shopee-sellers-program-pendukung-promosi-toko/)).
- **Proposed "ORI" badge (2022 student redesign, not a live Shopee feature):** 37.5% of surveyed respondents found it difficult to find original products. The redesign proposed an "ORI" badge on cards and a "100% Ori" filter. — [Joan Isva Medium case study, 2022](https://medium.com/@joanisva/ui-ux-case-study-shopee-redesign-64f38a4a970d)

**Lazada**
- **Card contents:** price "Rp727.650", voucher line "1% Off Voucher save 1%", "4.7K sold", review count "(2158)", seller location "Kab. Tangerang". — [Lazada tag page](https://www.lazada.co.id/tag/kasur-the-erland-120x200/)

**Amazon**
- **Card contents:**
  - "4.8 out of 5 stars" plus review count
  - "100+ bought in past month"
  - price, with unit price for multipacks, e.g. "$39.99 ($6.67/Count)"
  - "FREE delivery Thu, Jul 11 … Or fastest delivery …"
  - "Only 17 left in stock – order soon"
  - "More Buying Choices $73.86 (2 used & new offers)"
  - occasional "#1 Top Rated"

  [Amazon search page (cached)](https://www.amazon.com/s?hidden-keywords=B017M396C0%7CB0089NM9RC%7CB005HO2E30%7CB002JM16KA%7CB00P57J1S0%7CB00AOGD0I%7CB016AG3HV6%7CB000GFWH6Y&linkId=27096917); [Amazon search page (cached)](https://www.amazon.com/s?refresh=1&srs=120829983011)

**Alibaba.com and 1688**
- **Alibaba.com card:** price *range* ("$2.82–2.96"), "Min. order: 300 meters", supplier name, country and years on Alibaba, rating and review count, certifications (CE, RoHS…), Verified flag, sponsored rows (23 of 48 rows were ads for "led strip"). The quantity price ladder is only on the product page. — [Apify Alibaba.com scraper (dami_studio)](https://apify.com/dami_studio/alibaba-com-scraper); [ReefAPI Alibaba docs](https://reefapi.com/docs/alibaba)
- **1688 card:** tiered prices, e.g. "2~49件 ¥85 / 50~199件 ¥82 / ≥200件 ¥78"; network sales label "全网500+件"; repurchase rate "35%"; seller type, province and city; factory and inspection badges. — [Apify 1688 Search Scraper](https://apify.com/datacach/1688-search-scraper)

**Indonesian B2B**
- **Mbizmarket card:**
  - name and "Mulai dari Rp 11.500,00"
  - warranty status (Tanpa Garansi / Garansi Toko / Garansi Resmi)
  - tax tags (PPN, PPh 22, PKP, Pajak Daerah)
  - seller province and name
  - "Jenis Usaha" (Badan Usaha / Non Badan Usaha) and "Skala Usaha" (Mikro / Kecil)
  - badges: "Terverifikasi", "Bangga Buatan Indonesia"
  - a "Bandingkan Produk" (compare) action

  [Mbizmarket catalog search](https://www.mbizmarket.co.id/catalog/search)
- **Ralali card** (from the server-rendered Angular template; the fields are template variables, not observed values):
  - wholesale tier rows ("≥ qty  Rp price")
  - price "Per {unit}"
  - retail price with discount %, or "Nego" (NEGOTIABLE) when no price is set
  - free-shipping label
  - vendor location, "superseller" business/enterprise badges, shop name
  - actions: "Ajukan Permintaan" (RFQ), contact seller, and Buy

  [Ralali search page](https://www.ralali.com/search/kopi)

**Research: card content and layout**
- **Combine variations** of a product into one list item (42% don't). — [Baymard 2025](https://baymard.com/research-articles/current-state-product-list-and-filtering)
- **Thumbnails:** provide 3 or more in lists and search results (80% don't). — [same](https://baymard.com/research-articles/current-state-product-list-and-filtering)
- **Price per unit:** show it for products with varying volumes or quantities (67% don't). — [same](https://baymard.com/research-articles/current-state-product-list-and-filtering)
- **Attributes:** distinguish them from the title with spacing or styling, show essential price and key attributes on the list, and keep displayed attributes uniform across similar items. — [Baymard PLP UX guide, 2026](https://baymard.com/blog/product-listing-page-plp-ux)
- **Grid vs list:** use a grid for visually driven products, a list for spec-heavy products, and adapt dynamically for mixed catalogs. — [Baymard PLP UX guide, 2026](https://baymard.com/blog/product-listing-page-plp-ux)
- **Quick views:**
  - Provide them for visually driven products (50% don't). Avoid them for spec-driven types (21% don't). Spec-driven shoppers need comparison features instead.
  - A quick view must not replace good list-item info.
  - On mobile, keep the list visible behind the quick view and include a prominent link to the product page.
  - [Baymard: Quick Views for Visually Driven Products](https://baymard.com/research-articles/mobile-desktop-quick-views)
  - The 2024 update softened the earlier "generally avoid" advice into "helpful for visually driven products". — [Baymard 2024 update](https://baymard.com/research-articles/product-finding-2024-launch)
- **Multi-path cards:** cards with several destinations (add-to-cart, swatches, review links, quick view) cause accidental clicks. Group the elements that lead to the product page (image plus title) and put the other actions together, e.g. at the bottom. 76% don't use synchronised hover effects or unified hit areas. — [Baymard: Hover UX & Hit Areas](https://baymard.com/research-articles/list-items-hover-and-hit-area)
- **Desktop columns:** when grids reflow on large screens, cap them at about 5–8 columns depending on item size and vertical. — [Baymard: Responsive Upscaling](https://baymard.com/research-articles/responsive-upscaling)
- **Vendor heuristics (Metricuno, 2026; unverified, no methodology shown):**
  - food and supplements use 4–5 desktop columns, and quick add raises add-to-cart by 4–7% in repeat-purchase categories
  - 2 columns is standard on mobile
  - 3 mobile columns only works for deliberately small tiles (food, accessories)

  [Metricuno: Product Listing UX](https://www.metricuno.com/product-listing-ux)
- **Add-to-cart on listings (opinion from a component-library guide):** add-to-cart buttons are usually removed from listings, but they remain useful for repetitive grocery-type purchases where shoppers don't need product details. — [MDBootstrap e-commerce blocks](https://mdbootstrap.com/docs/standard/design-blocks/ecommerce/product-lists-and-grids/)
- **Tokopedia users** rated the grid/list layout toggle the least important of Filter, Sort and Layout (2019). — [Tokopedia Design Medium, 2019](https://medium.com/tokopedia-design/small-things-matter-redesign-filter-of-tokopedia-app-c6dbe6ccae6e)

### Inferences
- Use square (1:1) images: Tokopedia's 200×200 white-padded renditions, Alibaba's 300×300 thumbnails ([ScrapeUnblocker Alibaba docs](https://docs.scrapeunblocker.com/api-reference/plugins/scrape-alibabacom-product-search-results)), and Indonesian buyers' expectations all point that way. White padding avoids cropping packaged goods.
- **Card stack for the B2B shop** (B2C social proof is less meaningful for a single seller):
  1. Square image.
  2. Name, 2 lines.
  3. Pack/variant line, e.g. "Isi 24 btl × 600 ml" or "1 rim, 70 gsm".
  4. Price per selling unit, plus unit price when packs vary (Baymard 67%).
  5. Tier hint, e.g. "Rp 4.500/btl mulai 48 btl" or a "Harga grosir" badge (Shopee "Grosir", 1688 tiers).
  6. MOQ when above 1 (Alibaba "Min. order").
  7. Stock state ("Stok tersedia / Sisa 5 / Pre-order").
- Sold count and rating are optional for a single-seller shop with few reviews. Terlaris can show up as a badge instead.
- A quick add-to-cart with a quantity stepper is justified for repeat-purchase staples (drinks, pantry, cleaning, office consumables). Following Baymard hit-area guidance, it needs a visually separate zone from the image/title link. Merchandise (visually driven, configurable) is better served by going to the product page (Baymard quick-view guidance; inference).
- **Columns:**
  - Mobile: 2 (marketplace norm; vendor heuristic). A list layout (1 per row, image left) suits spec-like office or cleaning items.
  - Desktop: 4–5 with a filter rail, or 5–6 without one, within Baymard's 5–8 maximum.

### Gaps
- Exact 2026 column counts and card heights for Shopee and Tokopedia desktop and mobile could not be measured, because the pages could not be rendered.
- I found no public Baymard statistic on list vs grid performance (paywalled), and no source on whether Shopee or Tokopedia currently offer add-to-cart directly on search cards.

---

## 5. Pagination vs infinite scroll vs "load more"; results count; restoring the back-button position

### Takeaway
The marketplaces use classic paged results on web, around 48–60 items per page, with a visible count. Baymard's long-running recommendation is "Load More" plus lazy-loading: about 50–100 items per batch for categories, 25–75 for search, 15–30 on mobile. Baymard softened this in 2024 to "pagination can work OK if details are right". NN/g says no single option is best, and infinite scroll suits only small or homogeneous sets with good filters and a visible count. Whatever the pattern, returning users to the same spot after the Back button is a firm requirement.

### Cited Findings
**Platforms**
- **Tokopedia:** "Menampilkan 103 produk untuk 'barang unik terbaru' (1 – 60 dari 103)", i.e. 60 per page with a range and total. — [Tokopedia /find page](https://www.tokopedia.com/find/barang-unik-terbaru)
- **Shopee:** results show "Jumlah Halaman" (page count). — [Shopee Seller Education 17519](https://seller.shopee.co.id/edu/article/17519) The internal search API was called with `limit=60` in a public scraping script (older code). — [GitHub dagdo03 Shopee scraper](https://github.com/dagdo03/SHOPEE-SCRAPER-WITH-SELENIUM/blob/main/tesScrap.py)
- **Amazon:** "1-16 of 739 results". — [Amazon search page (cached)](https://www.amazon.com/s?i=merchant-items&me=A3JPM3Q1OKJJPX)
- **Alibaba.com:** 48 offers per page. — [ReefAPI](https://reefapi.com/docs/alibaba)
- **1688:** 60 per page. — [ScrapeUnblocker 1688 docs](https://docs.scrapeunblocker.com/api-reference/plugins/scrape-1688com-wholesale-search-results)
- **Ralali:** "Memperlihatkan X – Y Produk Dari N Produk Kami". — [Ralali](https://www.ralali.com/search/kopi)
- **Mbizmarket:** "Menampilkan 1404503 Produk untuk 'Semua Produk'". — [Mbizmarket](https://www.mbizmarket.co.id/catalog/search)

**Research**
- **Baymard testing of all three patterns** (desktop and mobile; 2016 article):
  - Users browsed far less of a list with pagination, mostly used Next/Previous, and perceived it as slow.
  - Infinite scroll showed vastly more products but led to superficial scanning and an unreachable footer.
  - "Load more" combined both strengths.
  - Recommended thresholds: categories 50–100 items with lazy-load; search 25–75, ideally adjusted by relevance score drop-off, and never infinite scroll for search; mobile 15–30.
  - Mobile users scrolled through more than twice as many products with infinite scroll as with pagination.
  - Fix the Back button, e.g. with `history.pushState()`.
  - Only 8% of the top 50 US sites used "load more" at the time.
  - [Smashing Magazine / Baymard, 2016](https://www.smashingmagazine.com/2016/03/pagination-infinite-scrolling-load-more-buttons/)
- **Items per load:** on desktop, load 50–150 products at once: 100–150 for visually driven products, 50–100 for spec-driven ones. 52% of desktop sites fall outside these ranges. Combine "Load More" with lazy-loading to avoid the performance cost. — [Baymard: Number of Products to Load](https://baymard.com/research-articles/number-of-items-loaded-by-default)
- **Baymard 2024 changes:** "load more" is still preferred, but pagination "can work okay" with many details to get right. "Items per page" selectors are now acceptable for paginated lists, if likely underused. — [Baymard 2024 update](https://baymard.com/research-articles/product-finding-2024-launch)
- **Baymard 2026 guide:** prioritise "Load More" over endless scrolling, size batches sensibly for desktop and mobile, and always return users to the same place in the list after viewing a product page. — [Baymard PLP UX guide, 2026](https://baymard.com/blog/product-listing-page-plp-ux)
- **Back navigation in testing:** Staples did not return users to the same list position, and mobile users suffered more. None of the participants in recent mobile tests opened product pages in new tabs. — [Baymard: Quick Views](https://baymard.com/research-articles/mobile-desktop-quick-views)
- **NN/g on infinite scrolling:**
  - Avoid it when users need to find something specific, compare items far apart, or inspect only the top results.
  - Its problems are losing the place after Back (users dumped at the top), no landmarks, and the footer.
  - "Load More" is the compromise. Asos's version shows how many products were viewed out of the total.
  - "Integrated pagination" (page markers inside infinite scroll) is new and untested.
  - No option is universally superior.
  - [NN/g: Infinite Scrolling](https://www.nngroup.com/articles/infinite-scrolling-tips/)
- **NN/g on product-listing pages (2022):**
  - Infinite loading can work when there are relatively few products (typically fewer than 40 per page), good filters and a clear item count ("42 items"). Avoid it for large inventories.
  - "Show More" buttons tested fine. They are bigger tap targets than page numbers and keep the footer reachable.
  - Save the shopper's spot.
  - [NN/g: Alternatives to Pagination on Product-Listing Pages](https://www.nngroup.com/articles/alternatives-pagination-listing-pages/)

### Inferences
- With a catalog of 250–2,000 products, most category lists will hold about 20–300 items. A "Muat lebih banyak" button is enough: 48 per batch on desktop and 24 on mobile, with lazy-loaded images. Show a count line such as "Menampilkan 48 dari 212 produk", and keep scroll position and loaded batches when the user returns from a product page via the URL/history state. Numbered pagination is acceptable too (Baymard 2024), and simpler for server-rendered and SEO pages.
- Search results should load fewer items before the button (24–48), because relevance drops quickly (Baymard).
- Restoring position on Back matters most on mobile, where users don't open new tabs (Baymard).

### Gaps
- Whether the Shopee and Tokopedia *apps* use infinite scroll (widely assumed) was not verified with a 2025–2026 source. The web evidence above shows paged results.

---

## 6. Evidence-based guidance (Baymard, NN/g) on filters, sorting, card content and list vs grid, with statistics

### Takeaway
Product finding is where most sites underperform: 58% of desktop and 78% of mobile sites are "poor" to "mediocre" on product list UX. The biggest documented wins are basic: the essential filters and sorts, a visible applied-filter overview, multi-select, unit prices, adequate thumbnails, load-more with position restore, and robust search for exact, product-type and abbreviation queries.

### Cited Findings
**Overall benchmark**
- 58% of desktop and 78% of mobile e-commerce sites have "poor" to "mediocre" Product List UX (2025 benchmark, 21,000+ UX scores, 170+ sites). — [Baymard: Product List UX 2025](https://baymard.com/research-articles/current-state-product-list-and-filtering)
- Sites with mediocre product-list usability saw 67–90% abandonment, versus 17–33% for slightly optimised toolsets on the same tasks: "as much as a 4-fold increase in leads". 36% of sites had flaws severe enough to be harmful, and the average site needs 35 design changes. — [Baymard Product Lists research overview](https://baymard.com/research/ecommerce-product-lists)
- 34% of sites have poor filtering implementations. — [Baymard Filtering Options examples](https://baymard.com/ecommerce-design-examples/filtering-options)

**Product list pitfalls (2025 list)**

| Guideline | Share of sites that don't comply | Source |
|---|---|---|
| Combine variations into one item | 42% | [Baymard 2025](https://baymard.com/research-articles/current-state-product-list-and-filtering) |
| 3+ thumbnails | 80% | same |
| Price per unit | 67% | same |
| Multi-select within a filter type | 14% | same |
| 5 essential filters (price, rating, color, size, brand) | 51% | same |
| Applied filters overview | 20% | same |
| 4 essential sorts (price, rating, best-selling, newest) | 69% | same |

Horizontal toolbars are flagged as "2 reasons to be cautious".

**Other filtering statistics**
- Promote important filters: 61% don't. — [Baymard](https://baymard.com/research-articles/promoting-product-filters)
- Filters for displayed list-item info: 38% don't (42% in 2015). — [Baymard](https://baymard.com/research-articles/have-filters-for-list-item-info)
- Explain industry-specific filters: 66% don't. — [Baymard](https://baymard.com/research-articles/desktop-ux-ecommerce)
- About two-thirds of sites use unclear filter terms. Use thumbnails for visually distinct attributes. — [Baymard: Visual Filters](https://baymard.com/research-articles/use-visual-filters-for-visually-distinct-product-attributes)
- Overcategorization: 75% get it wrong. — [Baymard](https://baymard.com/research-articles/ecommerce-over-categorization)

**Search statistics**
- Search UX "mediocre or worse" on 46% of desktop sites, 58% of mobile sites and 64% of apps. — [Baymard Search Query Types](https://baymard.com/research-articles/ecommerce-search-query-types)
- Autocomplete is present on 80% of sites, but only 19% get it fully right. — [Baymard](https://baymard.com/research-articles/autocomplete-design)
- Misspelling support in autocomplete: 69% fail. — [Baymard](https://baymard.com/research-articles/offer-autocomplete-suggestions-for-misspellings)
- No-results recovery: about 50% fail. — [Baymard](https://baymard.com/research-articles/no-results-page)

**Layout**
- Grid for visually driven products, list for spec-heavy ones, dynamic layouts for mixed catalogs. — [Baymard PLP guide, 2026](https://baymard.com/blog/product-listing-page-plp-ux)
- Quick views: provide for visually driven products (50% don't), avoid for spec-driven (21% don't). — [Baymard](https://baymard.com/research-articles/mobile-desktop-quick-views)

**NN/g**
- Faceted search needs simple controls and simultaneous display of facets and results. On mobile, a tray overlay keeps results visible. — [NN/g Mobile Faceted Search](https://www.nngroup.com/articles/mobile-faceted-search/)
- NN/g distinguishes *filters* (narrowing by a single attribute) from *faceted navigation* (multiple attribute dimensions that comprehensively describe the set). — [NN/g Filters vs Facets](https://www.nngroup.com/articles/filters-vs-facets/)

### Inferences
- **Priority order for the redesign**, highest documented impact per effort:
  1. Exact, SKU and abbreviation search with synonyms.
  2. Category-specific filters with result counts, plus an applied-chip overview.
  3. The 4 sorts.
  4. Unit price and pack info on cards.
  5. Load-more with position restore.
  6. Autocomplete capped at about 8–10 suggestions with category-scope suggestions.
  7. A zero-results page with alternatives and a contact/RFQ option.
- The catalog is mixed. Drinks, pantry, cleaning and office goods are spec- and pack-driven, while merchandise is visual. Baymard's "adapt dynamically for mixed catalogs" supports a grid default with a richer attribute line, or list layout for some categories.

### Gaps
- Baymard's specific list-vs-grid test statistics, mobile filter-drawer statistics and "add to cart from list" guidance are paywalled. Only the summary recommendations are public.

---

## 7. How B2B catalogs show listings when prices are tier-based or require login ("Login untuk melihat harga"; MOQ on cards)

### Takeaway
B2B platforms show tiers and MOQ on the card in one of three ways:
- a range plus MOQ (Alibaba "$0.69–0.76 · Min. order: 5 meters")
- explicit tier rows (1688 "2~49件 / 50~199件 / ≥200件", Ralali "≥ qty Rp")
- a "from" price (Mbizmarket "Mulai dari Rp…")

The full tier table lives on the product page. Login-gating comes in three flavours:
1. **Account-specific prices:** a public price for everyone, with special business prices for verified accounts (Amazon Business).
2. **Public price, gated transaction:** prices visible, but ordering requires login, RFQ and quotation (Mbizmarket, Ralali RFQ, Bhinneka corporate accounts).
3. **Fully closed catalog:** no prices until registered or approved (distributor portals such as Wahana Surya, and wholesalers abroad).

I found no published usability statistics on "login to see price".

### Cited Findings
**Global marketplaces**
- **Alibaba.com:** a listing "has no single price and no single quantity". The card price is a span from the cheapest to the dearest tier, the real ladder is only on the product page, and the MOQ is "a number plus a unit the supplier typed by hand" (pieces, meters, sets, bags; not normalised). — [ReefAPI Alibaba docs](https://reefapi.com/docs/alibaba)
- Alibaba cards show e.g. "Min. order: 300 meters" with "$2.82–2.96". — [Apify Alibaba.com scraper](https://apify.com/dami_studio/alibaba-com-scraper)
- Alibaba search filters include an MOQ range (`min_order` / `max_order`). Max MOQ is "the practical filter for buyers who cannot commit to a container load". — [ReefAPI](https://reefapi.com/docs/alibaba)
- **1688:** cards carry price tiers by quantity range ("2~49件", "50~199件", "≥200件"), repurchase rate and sales labels. Search can filter by maximum MOQ. — [Apify 1688 Search Scraper](https://apify.com/datacach/1688-search-scraper)
- **Amazon Business:**
  - Business prices and quantity-discount tiers (up to 5 tiers, percent or fixed) are visible only to registered Amazon Business buyers. Other consumers see the standard price, and products stay visible to everyone. — [Amazon SP-API samples: B2B pricing](https://github.com/amzn/selling-partner-api-samples/blob/main/use-cases/pricing-b2b/docs/blog-post/AB-pricing-blog-post.md)
  - Business customers see a quantity-discount table on the product page. — [Sell on Amazon: B2B prices and quantity discounts, 2025](https://sell.amazon.com/blog/amazon-b2b-prices)
  - A "Business Savings Blue Badge" can appear on **search results**, with messages like "Save 5% on 10+ units". Eligibility: at least 5% off for a single-unit business price, or at least 3% off on the first quantity tier, plus a rating of 4★ or more. — [same](https://sell.amazon.com/blog/amazon-b2b-prices)
  - If the business price is higher than the standard price, only the standard price is shown. — [Amazon Business Developer Guide (PDF)](https://m.media-amazon.com/images/G/03/B2B/Amazon_Business_Developer_Guide._CB1198675309_.pdf)

**Indonesian B2B**
- **Mbizmarket:**
  - Public catalog cards show "Mulai dari Rp…", tax tags (PPN/PPh/PKP), seller verification and business scale. — [Mbizmarket catalog search](https://www.mbizmarket.co.id/catalog/search)
  - Transacting requires registering the person and the company. The flow is cart → "Membuat Permintaan RFQ" → seller quotation → buyer PO → invoice and payment. — [Mbizmarket Cara Transaksi](https://www.mbizmarket.co.id/cara-transaksi)
  - Positioning: "harga satuan, grosiran, hingga harga dengan PPN khusus", plus Term of Payment. — [Mbizmarket home](https://www.mbizmarket.co.id/)
- **Ralali:**
  - Sellers set tier pricing ("harga bertingkat") by quantity and a minimum purchase quantity. Buyers can use RFQ when they can't find a product or a suitable price, and chat to ask about wholesale prices. — [Ralali news, 2023](https://news.ralali.com/dapatkan-harga-terbaik-hanya-di-ralali-com/)
  - Products may carry an MOQ, or none (buyable singly). — [Ralali news, 2017](https://news.ralali.com/harga-grosir/)
  - Listing cards include tier rows, "Per {unit}", and "Nego" when no price is set. — [Ralali search page template](https://www.ralali.com/search/kopi)
- **Bhinneka:** corporate accounts get "Fitur RFQ & Instant Quotation" and Corporate Purchase (renewal and subscription). Higher tiers add tender submission, contract and umbrella agreements, API procurement integration and custom payment tenor. — [Bhinneka Akun](https://www.bhinneka.com/akun-bhinneka)
- **Indonetwork** (B2B directory): flow is login → search or browse categories → contact the seller by phone ("Kontak") or Direct Chat to negotiate price. — [Indonetwork: Cara Beli Produk](https://www.indonetwork.co.id/page/cara-beli-produk)
- **Wahana Surya grosir portal** (closed distributor catalog): buyers log in with credentials issued by their area sales rep and are then taken to the catalog, with search and category navigation and add-to-cart with quantity. — [Wahana Surya: Cara Order](https://grosir.wahanasurya.com/cara-order/)
- **Tokoplas** (Indonesian plastics B2B): entering a tonnage quantity makes "the system adjust the price according to the tonnage tier" before "Tambah ke Truk". RFQ and sample orders are also offered. Accounts (company or personal) are approved before login. — [Tokoplas User Guide](https://help.tokoplas.com/id)
- **Shopee "Grosir":** tier prices are seen on the product page ("Harga Grosir > Kuantitas dan Harga Satuan") and applied automatically in the cart once the quantity is reached. The grosir label and price are hidden while another promo runs. — [Finansialfokus, 2025](https://finansialfokus.com/detail/46383/cara-beli-barang-grosir-di-shopee-dan-keuntungannya)
- **Blibli app (2025 release note):** seller catalogs now show "Produk terlaris, terbaru, dan pernah dibeli" (best-selling, newest and previously bought products) directly. — [Blibli App Store listing](https://apps.apple.com/id/app/blibli/id1034231507?l=id)

**Login-gated price examples outside Indonesia** (for contrast)
- **Mayorista El Indio (Argentina):** "Los precios solo están visibles para usuarios registrados y logueados". Visitors can browse without an account but see no prices and cannot buy. — [El Indio Mayorista: Cómo comprar](https://www.elindiomayorista.com/como-comprar/)
- **Peruvian Link:** approved wholesale logins see wholesale pricing, and everyone else sees retail prices. — [Peruvian Link wholesale portal](https://peruvianlink.com/pages/wholesale-homepage)
- **Oilflow B2B trade store:**
  - Personalised pricing after login.
  - Browse by category, brand or pack size.
  - Search by name, keyword or SKU.
  - "Reorder previous orders".
  - Prices by pack size via "select option".
  - [Oilflow B2B Trade Store](https://www.oilflow.co.za/b2btradestore/)

### Inferences
- For a B2B shop that hides prices from guests:
  - Keep the card's layout identical for guests and signed-in buyers, but put a clear "Masuk untuk lihat harga" link in the price slot, so the grid doesn't reflow and the call to action sits where people look for the price.
  - Keep name, pack size, MOQ and stock or availability visible to guests, so the catalog still works for discovery and SEO (El Indio / Wahana Surya vs Mbizmarket contrast).
  - Hide price sort and price filters for guests, as noted in section 3.
- **Tiers for signed-in buyers:** show the unit price at quantity 1, or the buyer's contract price, plus one compact tier hint ("mulai Rp X untuk ≥ N"), or Alibaba-style "Rp X – Rp Y" plus MOQ. Put the full tier table and a quantity stepper on the product page. Amazon Business shows its tier table on the product page and only a "Save X% on N+ units" badge in search.
- **MOQ:** show it on the card only when it is above 1, with a normalised unit (pcs, dus, rim). Alibaba's unnormalised units make comparison hard.
- **Unavailable or no-price items:** "Minta penawaran" or "Nego" (Ralali, Mbizmarket) works as a card action for products without a list price and on zero-results pages.
- **Reorder:** "Pernah dibeli" (Blibli), "Reorder previous orders" (Oilflow) and SKU search are the B2B-specific discovery features worth adding beyond marketplace patterns.

### Gaps
- I found no usability research or A/B data on "login to see price" (e.g. its effect on bounce, sign-ups or SEO).
- I could not confirm which Indonesian distributor sites literally use the phrase "Login untuk melihat harga". The searches returned closed portals (login first) rather than public catalogs with masked prices.
- Bhinneka's listing card (business vs personal pricing display) could not be captured because the search URL returned 404.
