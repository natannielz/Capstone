# Browse like a marketplace, buy like procurement

Unit Toko's buyer flow should take its front half from Indonesian marketplaces and its back half from business procurement platforms. From Shopee, Tokopedia and Lazada it should take open browsing, a two-tier header with the logo at top left, search-led discovery, square product cards, a sticky purchase panel and a keranjang that saves itself. From Amazon Business, Mbizmarket, Bhinneka and LKPP's e-Katalog it should take prices that belong to the signed-in account, a checkout button that turns into "Ajukan persetujuan" when a spending rule applies, a waiting state with a deadline, an approver queue, and order tracking inside the site. The owner's decision to hide prices from guests is stricter than every Indonesian consumer benchmark, all of which show guests prices and ask for an account only at the cart or checkout. It does match closed B2B catalogues and Amazon Business's account-price model. NN/g finds price is the information business buyers want most, so the gate has to earn its cost: every card keeps a price slot that reads "Masuk untuk lihat harga", and signing in returns the visitor to the same product, as Shopee's `next=` and Lazada's `target=` parameters do, instead of to the top of the shop. Navigation needs two separations that run in opposite directions. Branch buyers should get one storefront that changes mode when they sign in. Cooperative staff should get a separate back office, entered through the same obvious "Masuk" button, which is how every marketplace runs its seller centre. Unit Toko currently does roughly the reverse: buyer pages carry three different headers, none of them shows "Masuk", the logo sometimes opens the account page, and buyers and staff share one account page as home. The plan at the end puts one storefront header and the public-catalogue sign-in loop first, a persistent cart and an approval-aware one-page checkout second, and search, reorder and tracking depth third.

## Every Indonesian marketplace shows guests prices, so Unit Toko's gate must explain itself

### The benchmarks gate commitment, not information

Across Indonesian marketplaces the line between guest and member falls at the moment of commitment. A signed-out visitor on a Tokopedia search page sees the full result set: price, the struck-through original price, the discount, rating, units sold ("12 terjual"), category filters, a sort control and a result count ([Tokopedia](https://www.tokopedia.com/find/barang-unik-terbaru)). Shopee guests can search and compare price, rating and reviews ([Inspirasi Shopee](https://shopee.co.id/inspirasi-shopee/ingin-tahu-cara-belanja-di-shopee-yuk-ikuti-langkahnya/amp/)). Shopee's web mini-cart, however, asks guests to log in and links to `/buyer/login?from_source=mini_cart_btn&next=`, a login URL that carries both a return path and an analytics source tag ([Shopee](https://shopee.co.id/m/mart)). Shopee's help centre requires an account before checkout ([Shopee Help](https://help.shopee.co.id/portal/9/article/115340-%5BBaru-di-Shopee%5D-Bagaimana-cara-melakukan-pembelian-di-Shopee-Lite)), and Blibli's FAQ says plainly that shopping without an account is not possible ([Blibli](https://www.blibli.com/faq/topic/registrasi-info/verifikasi-akun/)). **No Indonesian platform offers guest checkout.** Econsultancy found none at Amazon either ([Econsultancy](https://www.econsultancy.com/guest-checkout-test-which-of-these-30-uk-retailers-force-users-to-register/)). Alibaba.com's header turns its messages, inquiries and order widgets into "Sign in to view…" prompts for guests ([Alibaba.com](https://www.alibaba.com/)).

| Platform | Guest can browse | Guest sees price | Sign-in required at | Return mechanism observed |
|---|---|---|---|---|
| Shopee | Yes | Yes | Cart (web) and checkout | `next=` on the login URL ([Shopee](https://shopee.co.id/m/mart)) |
| Tokopedia | Yes | Yes, with discount and units sold | Checkout (cart gate not verified) | Not observed |
| Lazada | Yes, plus order tracking by order number | Yes | Account pages, on a separate auth host | `target=` on the seller login ([Lazada](https://sellercenter.lazada.co.id/)) |
| Blibli | Not verified (site blocked the fetch) | Not verified | Any purchase | Not observed |
| Alibaba.com | Yes | Not verified | Messages, inquiries, orders, favourites | Inline "Sign in to view…" prompts |
| Amazon Business | Yes | Standard price for all; business price and tiers only for registered buyers ([Amazon SP-API samples](https://github.com/amzn/selling-partner-api-samples/blob/main/use-cases/pricing-b2b/docs/blog-post/AB-pricing-blog-post.md)) | Business pricing, checkout | Not observed |
| Unit Toko (owner decision, 20 Oct) | Yes, staff-approved assortments only | No; price, stock and ordering need sign-in | Price, cart, orders, checkout | Spec returns every sign-in to `/shop` |

Western research warns that forced registration costs sales. Of US shoppers who abandoned a purchase for a reason other than "just browsing", **18% left because the site wanted them to create an account** ([Baymard](https://baymard.com/lists/cart-abandonment-rate)), and NN/g reports that sites which add guest checkout see sales rise straight away ([NN/g](https://www.nngroup.com/articles/optional-registration/)). That evidence concerns first-time consumers. Unit Toko's buyers are provisioned branch accounts, and the Indonesian norm already makes an account normal. The lesson that transfers is narrower: never make anyone do account work in the middle of a purchase, and keep sign-in itself fast.

### Unit Toko's choice belongs to the closed-catalogue family

The owner's decision puts Unit Toko in a different family: closed B2B catalogues. An Argentine wholesaler lets visitors browse but shows prices only to registered, signed-in users ([El Indio Mayorista](https://www.elindiomayorista.com/como-comprar/)). The Indonesian distributor Wahana Surya hands out catalogue logins through its area sales reps ([Wahana Surya](https://grosir.wahanasurya.com/cara-order/)). Amazon Business sits between the two models. Everyone sees the product and a standard price, and registered business buyers see business prices and quantity tiers on top. The cost of hiding prices is well documented. When NN/g asked business users to rank 28 kinds of B2B site information, **price came first, scoring 29% higher than availability in second place**, and NN/g calls missing prices the most user-hostile element of B2B sites ([NN/g](https://www.nngroup.com/articles/b2b-usability/)). It has repeatedly watched business users leave price-hiding sites for competitors ([NN/g](https://www.nngroup.com/articles/show-price/)).

The owner has strong reasons on the other side. The prices are BNI contract prices, and every buyer view of a private product photo takes a lease and writes audit rows, so anonymous traffic must never reach that path (local codebase observation, `docs/superpowers/specs/2026-10-20-public-catalog-design.md`). Unit Toko's market also weakens NN/g's competitive argument. Its buyers are a known set of BNI branches with provisioned accounts. The public catalogue's main audience is therefore not a shopper comparing suppliers. It is a branch officer who has not signed in yet: on a new phone, after following a shared link, or before a first login. Prospective branches and management make up the rest. For that audience the gate costs little in lost sales and a lot in friction, because the hard part is getting back to the same product after signing in. That is an inference, but it changes the design priority: **the return path, not the price mask, decides whether the gate feels cheap.**

### Three details turn a price wall into a doorway

Practitioner guidance on "login to see price" agrees on a short list. Keep search, filters, specifications and case sizes usable before login. Put the login call to action where the price would be. Say what signing in unlocks, for example "contract price, stock and reorder history". Keep cart and filter state across login. Give new buyers a separate path to request an account ([Ecom Design Pro](https://ecomdesignpro.com/login-to-see-price/)). For Unit Toko that becomes three rules.

First, **keep the card and product-page layouts identical for guests and buyers**, and put "Masuk untuk lihat harga" in the exact box where the price appears. Nothing reflows, and the prompt sits where people look for the number. The guest card then leans on what the owner allows: photo, name, pack line, category and minimum order. Stock stays hidden by the owner's rule. That is stricter than NN/g's advice to leave enough information outside barriers for users to judge fit, but it is defensible, because stock levels are operational information about what branches are buying.

Second, **signing in must land on the signed-in version of the same page.** Today the public-catalogue spec sends every sign-in to `/shop` (local codebase observation), which throws away the product the visitor had chosen. The login route already accepts a `returnPath` parameter, so the fix is to pass the current path, validated as same-site to prevent open redirects, and map `/katalog/<id>` to the priced product page. No benchmark documents restoring the action that triggered the login, such as the quantity a guest had typed. Doing it would put Unit Toko ahead of the marketplaces rather than level with them.

Third, **hide price sorting and price filtering from guests.** Sorting by price would leak relative prices even with the numbers masked.

"Daftar" should not appear anywhere. Indonesian headers pair "Masuk" with "Daftar" because anyone may register on a marketplace. Unit Toko's footer says the service is for BNI branches and approved business accounts (local codebase observation), so a "Daftar" button would promise a self-service sign-up that does not exist. The honest equivalent is a quiet "Belum punya akses?" link to the landing page's access section. That matches the separate request-account path the guidance recommends.

Finally, the research found **no published usability or A/B data on "login to see price" at all**. Unit Toko should instrument the loop from the first pilot week, using the metrics the practitioner guide suggests: the share of public-catalogue visits that click "Masuk", login completion from the catalogue, add-to-cart in the same session after sign-in, and price questions arriving through WhatsApp or support.

## Buyers need one storefront that changes on sign-in; staff need a separate door

### The Indonesian header recipe is stable and easy to copy

Desktop headers share a two-tier layout. Tokopedia's guest header has a thin utility bar (app download, about, "Mulai Berjualan", promotions, Tokopedia Care) above a main bar with the logo linking home, a single "Kategori" trigger, the search box, and "Masuk" and "Daftar" buttons ([Tokopedia](https://www.tokopedia.com/)). Lazada's top bar adds "LACAK PESANAN", a customer-care menu, login and "DAFTAR". Its markup also contains the signed-in account menu: Panel Akun, Pesanan Saya, Wishlist, Ulasan Saya, Pengembalian & Pembatalan and Logout ([Lazada](https://www.lazada.co.id/)). Shopee shows trending keywords under its search box and puts a login prompt behind the guest cart icon ([Shopee](https://shopee.co.id/m/mart)).

The logo's position matters more than it looks. NN/g found users **six times more likely to reach the homepage in one click when the logo is left-aligned than when it is centred**. It also warns that the logo-as-home convention is still not understood by less technical users, and recommends an explicit home link or breadcrumb as well ([NN/g](https://www.nngroup.com/articles/homepage-links/)). Tokopedia's search pages carry a breadcrumb rooted at "Tokopedia" ([Tokopedia](https://www.tokopedia.com/find/barang-unik-terbaru)).

On phones the apps converge on a bottom tab bar that ends in an account tab ("Saya" on Shopee, "Akun" elsewhere) and keeps navigation in the thumb zone. On product pages the tabs give way to a bar of chat, add-to-cart and buy buttons. Shopee and Bukalapak keep the cart icon at the top of the screen; Tokopedia and Lazada put it in the bottom bar ([AKSA layout study](http://www.aksa.stsrdvisi.ac.id/index.php/aksa/article/download/37/35)). Tokopedia unified its header icons across the browsing funnel and added a "Menu Utama" with an explicit button back to the home screen on every main page ([Life at Tokopedia](https://medium.com/life-at-tokopedia/introducing-menu-utama-on-tokopedia-3cbeaf54045c)). The research could not verify the current 2025–2026 tab labels, or what the account tab shows a guest, so a Unit Toko tab bar is an informed adaptation rather than a copy.

### Marketplaces wall off sellers; procurement platforms merge buyers

**Every marketplace runs its back office as a separate site with its own login.** Tokopedia's seller operations moved into the integrated Tokopedia & TikTok Shop Seller Center at seller-id.tokopedia.com during 2025 ([Tokopedia Seller University](https://seller-id.tokopedia.com/university/essay?knowledge_id=1189016095426305)). An unauthenticated visit to Lazada's seller centre redirects to a separate seller login group on login.lazada.com, with a `target=` return URL, while buyers sign in at member.lazada.co.id ([Lazada Seller Center](https://sellercenter.lazada.co.id/)). Blibli's Seller Center has its own sign-in page and routes internal staff email domains to single sign-on ([Blibli Seller](https://seller.blibli.com/sign-in)). The buyer site reaches the back office through one recruitment link in the utility bar or footer ("Mulai Berjualan", "MENJADI SELLER"), never through the buyer's account menu ([Lazada](https://www.lazada.co.id/); [Tokopedia](https://www.tokopedia.com/)).

Buyers inside a business account get the opposite treatment. Amazon Business keeps one storefront and changes its state. An "Account for [business]" dropdown offers Switch Accounts ([Amazon](https://www.amazon.com/gp/help/customer/display.html?nodeId=TCthycvaJ6zmE5NlON)), the business account menu has carried "Approve Orders" ([Amazon Business Approvals guide](https://images-na.ssl-images-amazon.com/images/G/01/BISS/B2B/pdfs/AmazonBusinessAccounts_Approvals._V288121526_.pdf)), and business prices and policy messages appear inside the normal shopping flow. Staples Advantage shows pending approvals on the home page and under Account ([Staples](https://marketingassets.staples.com/m/5d59618f9c39b46b/original/Administrator-s-Quick-Reference-Guide.pdf)). Bhinneka uses one site and one login, and asks at sign-in whether to continue with the corporate account ([ITB Bhinneka guide](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/03/Corporate-Purchase-Bhinneka.com_.pdf)). B2B portal practitioners describe the portal as a view over the same catalogue, with prices, visibility and terms loaded per account at login ([Hunchbite](https://hunchbite.com/guides/b2b-self-service-portal-industrial)). NN/g found that segment splits confuse business users who do not know which segment they belong to ([NN/g](https://www.nngroup.com/articles/b2b-vs-b2c/)).

The Tokopedia–TikTok Shop merger is a warning about two storefronts drifting apart. When the integration took full effect in June 2025, users complained of confusing navigation ([Akurat](https://akurat.co/umkm/795509/tokopedia-x-tiktok-shop-terintegrasi-umkm-dan-pengguna-terdampak)). Unit Toko's public `/katalog` and buyer `/shop` are two projections of one catalogue, and they should look like one.

### Unit Toko today mixes up the two separations

The current code explains each of the owner's three requests (local codebase observation, 21 October 2026). Buyer-facing pages render three different headers:

| Header | Links | Logo target |
|---|---|---|
| Pilot header | Katalog toko, Permintaan saya, Cara mengajukan, Akun saya | `/` |
| Checkout header | Katalog kemasan, Draf pesanan, Akun saya | `/` |
| Priced catalogue's own header | — | "Unit Toko" link to `/identity/account` |

The account masthead and the staff workspace sidebar also point the logo at `/identity/account`. None of the headers offers "Masuk". The only sign-in link sits in the footer as "Masuk ke akun", next to a "Ruang kerja petugas" link. That link, like `/workspace`, leads to the same `/identity/account` page buyers use.

The result: buyers meet inconsistent navigation, while buyers and staff share a home. The fix inverts both. Every buyer page gets one storefront header. Staff enter the back office through the same "Masuk" button, and the back office never borrows the storefront's chrome. The final section specifies the header contents for guests, buyers, approvers and staff.

## Search carries discovery; ten categories need chips, not mega-menus

### A small shelf needs shallow categories and forgiving search

Category browsing comes second on Indonesian marketplaces. Tokopedia exposes one "Kategori" trigger and a three-level taxonomy built for millions of SKUs, for example Makanan & Minuman → Minuman → Kopi Bubuk ([browse.sh](https://www.browse.sh/skills/tokopedia.com/compare-products-by-price-rating-deliver-b82t29)). In a small 2019 Shopee study, all four participants went straight to search, and one scrolled past the categories without noticing them ([Prototypr](https://blog.prototypr.io/a-ux-case-study-on-shopee-and-my-redesign-of-it-cb0cabd0d8d0)).

Baymard's research fits a catalogue of 250–2,000 products in about ten categories. Show product categories as top-level navigation on mobile, which 33% of sites fail to do; on small-catalogue desktop sites, a single "Shop" entry did not hinder users ([Baymard](https://baymard.com/research-articles/main-navigation-product-categories)). Do not split product types that share attributes into separate subcategories, a mistake **75% of benchmarked sites make**; offer the types as filters instead ([Baymard](https://baymard.com/research-articles/ecommerce-over-categorization)). If a category page sits between the menu and the product list, make it about subcategories, not promotions ([Baymard](https://baymard.com/research-articles/ecommerce-sub-category-pages)). Unit Toko therefore needs two levels at most: a "Kategori" panel listing the ten categories on desktop, a horizontal chip row with a "Semua kategori" sheet on mobile, and subcategory chips at the top of each category list instead of separate landing pages.

Search does the heavy lifting, and it is where most sites underperform:

| Area | Baymard finding |
|---|---|
| Autocomplete | Present on 80% of sites, but only 19% get every detail right. Stop at about ten suggestions on desktop and four to eight on mobile. Style category-scope suggestions ("di Minuman") differently from query suggestions ([Baymard](https://baymard.com/research-articles/autocomplete-design)) |
| Misspellings | 69% of sites suggest nothing for a close misspelling ([Baymard](https://baymard.com/research-articles/offer-autocomplete-suggestions-for-misspellings)) |
| Exact names and codes | Must resolve, or users quickly conclude the site does not stock the item |
| Category words | A query that matches a category should go straight to it |
| Abbreviations and symbols | Fail at 54% of sites ([Baymard](https://baymard.com/research-articles/ecommerce-search-query-types)) |

Pantry, office and cleaning goods are full of exactly those forms. Units come as ml and L, gr and kg, rim, lusin, dus, A4 and F4, and brand names double as generic nouns: Aqua for bottled water, Tipp-Ex for correction fluid. A synonym and unit table, plus exact and partial matching on Unit Toko's own item codes, is worth more than any visual polish (inference).

Zero results need the same care. About half of sites leave a zero-result search as a dead end. The recovery strategies that work are related categories, alternative queries with product previews, popular products and direct contact options; search tips on their own are rarely read ([Baymard](https://baymard.com/research-articles/no-results-page)). Indonesian B2B platforms put the empty page to commercial use: Ralali's offers its tender, or request-for-quotation, form ([Ralali](https://www.ralali.com/search/kopi)). Unit Toko's version should offer a typo-corrected alternative, related categories, "Minta barang di luar katalog" and a WhatsApp link. A former Tokopedia search designer reports that unifying search cut no-result searches by 70% ([LinkedIn](https://linkedin.com/in/azwaradli)). The figure is self-reported, but it shows how much is at stake.

### Filters and sorts should shed marketplace baggage

Marketplace sort lists are short and nearly identical. Tokopedia offers Paling Sesuai (the default), Ulasan, Terbaru, Harga Tertinggi and Harga Terendah ([browse.sh](https://www.browse.sh/skills/tokopedia.com/compare-products-by-price-rating-deliver-b82t29)). Shopee offers Terkait, Terbaru, Terlaris and Harga ([Shopee Seller Education](https://seller.shopee.co.id/edu/article/17519)). Alibaba.com has no price sort at all; it bounds price with a minimum and maximum instead ([ReefAPI](https://reefapi.com/docs/alibaba)). Baymard names price, rating, best-selling and newest as the four essential sorts, and **69% of mobile sites lack at least one** ([Baymard](https://baymard.com/research-articles/current-state-product-list-and-filtering)).

Unit Toko's buyer catalogue today offers only "Terbaru ditambahkan" and "Nama A–Z", and the public-catalogue spec repeats that pair (local codebase observation). A single seller with few reviews gains little from a rating sort. "Terlaris", or "Paling sering dipesan cabang", is the B2B stand-in for social proof. For signed-in buyers, "Pernah dibeli" (previously bought) is the most useful sort a replenishment buyer can have; Blibli added it to seller catalogues in 2025 ([Blibli App Store listing](https://apps.apple.com/id/app/blibli/id1034231507?l=id)).

The most prominent marketplace filters (seller location, shop tier, courier, cash on delivery) exist because thousands of sellers ship from hundreds of cities ([Lazada](https://www.lazada.co.id/tag/kasur-the-erland-120x200/)). They mean nothing for one cooperative. Baymard's five essential filter types are price, rating, colour, size and brand, and 51% of sites miss at least one ([Baymard](https://baymard.com/research-articles/current-state-product-list-and-filtering)). Adapted to office and pantry supplies, they become Kategori, Merek, Kemasan or ukuran, availability, and category attributes such as paper size or volume, with price shown only to signed-in buyers. Mbizmarket's price filter can include or exclude tax ([Mbizmarket](https://www.mbizmarket.co.id/catalog/search)), a useful touch for buyers who answer to finance.

The interaction details have unusually strong evidence:

| Detail | Evidence |
|---|---|
| Hero options on the first filter screen, price slider in the thumb zone, explicit apply button | Tokopedia's 2019 redesign; users rejected an auto-applying sheet. Reported result: **26.6% more Android add-to-cart conversions** ([Tokopedia Design](https://medium.com/tokopedia-design/small-things-matter-redesign-filter-of-tokopedia-app-c6dbe6ccae6e)) |
| Applied filters as removable chips | 28% of sites lack them ([Baymard PLP guide](https://baymard.com/blog/product-listing-page-plp-ux)) |
| Result counts beside each option; several values in one filter combine with OR | [Baymard PLP guide](https://baymard.com/blog/product-listing-page-plp-ux) |
| Most important filters promoted to the top | 61% of sites fail to do it ([Baymard](https://baymard.com/research-articles/promoting-product-filters)) |

The payoff is large. On the same product-finding tasks, sites with mediocre product lists saw **67–90% abandonment, against 17–33%** for slightly optimised ones ([Baymard](https://baymard.com/research/ecommerce-product-lists)). With five to seven filters, a desktop left rail and a horizontal bar both work; Baymard cautions against horizontal bars past about eight filters. On mobile, a "Filter" button should open a tray that leaves results visible behind it, as NN/g recommends ([NN/g](https://www.nngroup.com/articles/mobile-faceted-search/)).

### Cards should answer "what pack, what price, can I order it"

Indonesian marketplace cards are dense: a square image with a discount badge, a two-line name, a bold price with the original struck through, rating, units sold, seller city and shop badges ([Tokopedia](https://www.tokopedia.com/find/barang-unik-terbaru)). B2B cards trade social proof for ordering facts. Alibaba.com shows a price range and a minimum order ([Apify Alibaba scraper](https://apify.com/dami_studio/alibaba-com-scraper)). 1688 prints its quantity tiers on the card ([Apify 1688 scraper](https://apify.com/datacach/1688-search-scraper)). Mbizmarket shows "Mulai dari Rp…" with tax tags ([Mbizmarket](https://www.mbizmarket.co.id/catalog/search)), and Ralali shows tier rows and a per-unit price ([Ralali](https://www.ralali.com/search/kopi)).

Tokopedia's own research points the same way. Users rated much card metadata, including the inline add-to-cart button and promotional labels, as unimportant. The redesign made the final price explicit, **cut card height by 17%** and reported 3.05% more clicks; these figures are self-reported in a designer portfolio ([Galuh Akhdandika](https://galuh-akhdandika.framer.website/tokopedia-product-card)). Baymard adds that 67% of sites fail to show a per-unit price when pack sizes vary, and 42% fail to combine a product's variants into one list item ([Baymard](https://baymard.com/research-articles/current-state-product-list-and-filtering)).

Unit Toko's card should therefore stack:

1. A square image.
2. A two-line name.
3. A pack line ("Isi 24 botol × 600 ml").
4. The price per selling unit, plus a per-item price when packs vary.
5. One tier hint ("Rp4.500/botol mulai 48 botol").
6. The minimum order, only when it exceeds one.

For guests, the price and tier lines collapse into the single "Masuk untuk lihat harga" slot.

Whether cards should carry an add button is a real tension. Baymard's hit-area research finds that cards with several actions cause misclicks unless the link area and the actions are clearly grouped; 76% of sites lack unified hit areas ([Baymard](https://baymard.com/research-articles/list-items-hover-and-hit-area)). Its grocery testing recommends turning "add" into a quantity stepper once an item is in the cart ([Baymard](https://baymard.com/blog/grocery-add-to-cart-buttons)). Tokopedia's finding and Baymard's grocery advice are each right for a different moment. A browsing grid should stay clean. Lists built for repeat buying, such as "Pernah dibeli" and a branch's routine list, should get inline steppers. Two columns is the mobile norm; desktop takes four to five with a filter rail, within Baymard's cap of five to eight ([Baymard](https://baymard.com/research-articles/responsive-upscaling)).

For paging, Tokopedia shows 60 results per page with a range and total ([Tokopedia](https://www.tokopedia.com/find/barang-unik-terbaru)). Baymard prefers a "load more" button with lazy-loading over both pagination and infinite scroll, and its 2024 update accepts pagination when the details are right ([Baymard](https://baymard.com/research-articles/product-finding-2024-launch)). NN/g likewise finds that "show more" buttons test well ([NN/g](https://www.nngroup.com/articles/alternatives-pagination-listing-pages/)). Whatever the pattern, the list must restore the user's position after Back. That matters most on mobile, where none of Baymard's recent test participants opened products in new tabs ([Baymard](https://baymard.com/research-articles/mobile-desktop-quick-views)). The public API's page size of at most 48 (local codebase observation) suits a "Muat lebih banyak" button with a count line such as "Menampilkan 48 dari 212 produk".

One vocabulary point follows from the benchmarks. Unit Toko's buyer screens call products "kemasan" (Katalog kemasan, Rincian kemasan, Cari kemasan), while every benchmark speaks of "produk" or "barang" (local codebase observation; [Tokopedia](https://www.tokopedia.com/find/barang-unik-terbaru)). Branch staff search for things, not packages. "Kemasan" belongs in the pack line.

## Product page to approved order should take two screens and one honest button

### The product page is a purchase panel

Tokopedia's desktop product page groups every purchase control in one card headed "Atur jumlah dan catatan". It holds the chosen variant, a quantity stepper, stock, a minimum-order line ("Min. pembelian 3 pcs") and a live subtotal, 3 × Rp6.800 = Rp20.400 in the example ([Tokopedia](https://www.tokopedia.com/jnssby/keranjang-serbaguna-plastik-kr02-keranjang-plastik-storage-box-kr02-purple-bubble)). On phones the same controls sit in a bottom sheet opened from a fixed bar holding Tokopedia's "+ Keranjang" and "Beli Langsung", or Shopee's "Masukkan Keranjang" and "Beli Sekarang" ([Tokopedia Care](https://www.tokopedia.com/help/article/bagaimana-cara-belanja-di-tokopedia); [Shopee Help](https://help.shopee.co.id/portal/4/article/71961-%5BBaru-di-Shopee%5D-Bagaimana-cara-melakukan-checkout-di-Shopee)).

NN/g's cart-feedback research says the add-to-cart confirmation must persist rather than fade, must show the correctly optioned image, name, price and quantity, and must update a badge on the cart icon. It should also warn when the item is already in the cart, because many participants added the same item twice ([NN/g](https://www.nngroup.com/articles/cart-feedback/)). Baymard's grocery work turns the add button into a stepper after the first add, and **97% of sites fail to offer a stepper plus a typeable quantity field** ([Baymard](https://baymard.com/blog/current-state-of-checkout-ux)).

Express buying suits consumers, not branches. Tokopedia's "Beli Langsung" uses saved address and payment preferences to skip the cart for a single product ([Tokopedia Care](https://www.tokopedia.com/help/article/beli-langsung)). That helps a consumer buying one item and adds little for a branch replenishing twenty lines. Unit Toko should keep a single primary "Tambah ke keranjang", which changes to "Di keranjang · 24 botol" with a stepper once the item is added.

B2B platforms add pricing depth to the page:

| Platform | What it adds |
|---|---|
| Amazon Business | A quantity-discount table on the product page; in search, only a "Save X% on N+ units" badge ([Sell on Amazon](https://sell.amazon.com/blog/amazon-b2b-prices)) |
| Shopee | Wholesale tiers on the product page, applied automatically once the cart quantity qualifies ([Finansialfokus](https://finansialfokus.com/detail/46383/cara-beli-barang-grosir-di-shopee-dan-keuntungannya)) |
| Mbizmarket | Prices shown both with and without tax ([Mbizmarket](https://www.mbizmarket.co.id/)) |

Unit Toko already stores and displays a minimum order and an order increment ("Minimum pembelian", "Kelipatan jumlah") (local codebase observation). Its stepper should start at the minimum and step by the increment. The tier table belongs under the price, and the subtotal should show DPP and PPN. The delivery promise should be a date, not a speed: 48% of sites quote speeds such as "4–7 business days" that force users into calendar arithmetic, and 83% omit an order-cutoff countdown ([Baymard](https://baymard.com/blog/current-state-of-checkout-ux)). The guest version of the page keeps the same structure, swaps the price block for the sign-in prompt, and turns the button into "Masuk untuk memesan".

### The keranjang should persist without a save button

Indonesian carts are holding areas with selection. Shopee's cart has a checkbox on each product, and the buyer checks out only the ticked items ([Tempo](https://www.tempo.co/ekonomi/cara-belanja-di-shopee-yang-mudah-untuk-pemula-93953)). When a quantity passes a promotion limit, the cart price changes and a reminder appears; sold-out items drop into a separate invalid list at the bottom ([Shopee Help](https://help.shopee.co.id/4/article/72354-%5BBaru-di-Shopee%5D-Mengapa-harga-produk-berubah-setelah-saya-menambahkannya-ke-keranjang)). LKPP's e-Katalog groups unbuyable items under "Tidak bisa diproses", with a reason for each ([INAPROC](https://bantuan.inaproc.id/hc/id-id/articles/8167455220367-Memulai-Pesanan)). Bhinneka's corporate cart lets buyers request an item the site does not list ([ITB Bhinneka guide](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/03/Corporate-Purchase-Bhinneka.com_.pdf)).

NN/g treats the cart as a decision area. It calls for a full cart page, not only a mini-cart; a visible Remove; quantity changes that commit without an "Update" button, which users forget; and a cart that follows a signed-in user across devices ([NN/g](https://www.nngroup.com/articles/shopping-cart/); [NN/g](https://www.nngroup.com/articles/mobile-checkout-ux/)). Amazon Business holds a cart submitted for approval at locked prices and quantities for seven days ([Amazon Business](https://business.amazon.com/en/blog/purchase-controls)). Mbizmarket's "Seleksi di Keranjang" lets buyers choose which cart lines become a transaction ([Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718)).

Unit Toko today redirects `/cart` to `/ordering/drafts`, where the cart is a "Draf pesanan" kept through a "Konfirmasi simpan draf" step (local codebase observation). An explicit save is the forgotten "Update" button in another form.

The redesign should give each branch **one automatically saved keranjang**, bound to the account on the server. It needs:

- a checkbox on each line, with "Pilih semua" in the header;
- steppers that enforce the minimum order and increment;
- inline warnings such as "Harga berubah" and "Stok tidak cukup";
- a "Tidak dapat diproses" section for unbuyable lines;
- a sticky summary showing the selected lines and total, with "Lanjutkan (n)".

Named drafts survive as "Daftar belanja". These are reorder lists, which Amazon Business implements as shareable lists that keep items visible after purchase ([Amazon Business](https://business.amazon.com/en/learn/business-lists)). The empty cart should offer "Pesan lagi" from recent orders rather than a dead end, following NN/g's rule that an empty state links directly to the action that fills it ([NN/g](https://www.nngroup.com/articles/empty-state-interface-design/)).

### Checkout is one page whose button tells the truth about approval

Shopee and Tokopedia both run single-page checkouts: default address, delivery option, notes, vouchers, payment, and a sticky total. Shopee ends in "Buat Pesanan"; Tokopedia uses "Pilih Pembayaran" and then "Bayar" ([Shopee Help](https://help.shopee.co.id/portal/4/article/71961-%5BBaru-di-Shopee%5D-Bagaimana-cara-melakukan-checkout-di-Shopee); [Tokopedia Care](https://www.tokopedia.com/help/article/cara-bayar-di-tokopedia)). Amazon Business adds "Business order information" fields (PO number, GL code, cost centre, location, department, comments for the approver). For orders that need approval it **replaces "Place your order" with "Submit order for approval"** ([Amazon](https://www.amazon.com/gp/help/customer/display.html?nodeId=201633090)).

Baymard's 2024 benchmark puts the average checkout at 5.1 steps and 11.3 fields, and finds that field count, not step count, drives difficulty ([Baymard](https://baymard.com/blog/checkout-flow-average-form-fields)). Its 2025 benchmark adds three common failures ([Baymard](https://baymard.com/blog/current-state-of-checkout-ux)):

| Failure | Share of sites |
|---|---|
| No adaptive error messages | 94% |
| Required and optional fields not both marked | 61% |
| No explanation of why a phone number is needed | 49% |

NN/g puts the order summary at the top of mobile checkout pages, so costs never surprise ([NN/g](https://www.nngroup.com/articles/mobile-checkout-ux/)), and reports that business professionals push back when asked for information too early ([NN/g](https://www.nngroup.com/articles/workflow-expectations/)). Shopee's list of reasons it refuses to place an order is a ready-made catalogue of specific error messages: no shipping option, courier weight limits, too many products, per-buyer quantity limits and payment failure ([Shopee Help](https://help.shopee.sg/portal/4/article/76785)).

The approval mechanics are well established abroad and in Indonesia:

| Platform | How approval works |
|---|---|
| Amazon Business | Triggered for all orders or above a spending limit, set per group or per person. Up to ten approvers per level, all notified. Approvers can reject single items with a comment, and the buyer gets a link to rebuild a rejected cart. Unapproved requests cancel after seven days ([Amazon Business](https://business.amazon.com/en/learn/spending-limit); [Amazon Business](https://business.amazon.com/en/learn/approving-requests)) |
| Staples Advantage | Declining requires a reason. "Global approvers" cover absences. Approvers may change the PO, budget, payment method and delivery date, but never the ship-to address ([Staples](https://marketingassets.staples.com/m/5d59618f9c39b46b/original/Administrator-s-Quick-Reference-Guide.pdf)) |
| Bhinneka | Roles of Power User, Requestor and Approver, with budget limits per department ([Bhinneka](https://www.bhinneka.com/blog/kelebihan-bhinneka-e-procurement/)) |
| Mbizmarket | "Manajemen Persetujuan" lets finance staff approve online from anywhere ([Mbiz Insight](https://insight.mbiz.co.id/2020/04/21/bagaimana-karyawan-saya-bisa-menggunakan-e-procurement-mbiz/)) |
| LKPP e-Katalog | Orders cancel automatically when either side fails to respond within three working days ([INAPROC](https://bantuan.inaproc.id/hc/id-id/articles/14461422000143-Syarat-dan-Ketentuan-Katalog-Elektronik-Versi-6-Versi-1-5)) |

Unit Toko already has spending policies, an order-review workspace and statuses such as "Menunggu persetujuan belanja" (local codebase observation). The design gap is timing: **the requester should know before submitting that approval applies and who gives it.** The checkout summary should state the rule ("Perlu persetujuan Penyetuju cabang · pesanan di atas Rp5.000.000") and name the approver, and the button should read "Ajukan persetujuan" instead of "Buat pesanan". After submission, the confirmation page names the approver and the deadline, and says whether prices are held until then. A rejection shows the reason and offers "Perbaiki dan ajukan ulang", which restores the cart. Whether Unit Toko holds prices during approval is a business decision; Amazon's seven days is a benchmark, not a rule.

Approvers need:

- a header badge that counts only orders waiting on them;
- a queue with approve, reject (reason required) and per-line rejection;
- a backup approver for absences;
- no ability to change the delivery address.

Approval must work on a phone.

One requirement has no marketplace precedent but matters for BNI: tax. Under PMK 8/2021, a BUMN collects and remits the PPN on purchases from PKP suppliers itself, unless the payment is at most **Rp10,000,000 including tax** and is not split from a larger transaction ([JDIH Kemenkeu](https://jdih.kemenkeu.go.id/dok/8-pmk-03-2021/summary)). BUMNs also collect PPh 22 at 1.5% above the same threshold ([DJP](https://pajak.go.id/id/pemungutan-pajak-penghasilan-pasal-22)). Since January 2025, ordinary deliveries carry an effective 11% PPN through DPP nilai lain ([DJP](https://www.pajak.go.id/en/node/114038)). Indonesian B2B platforms surface these rules. Bhinneka's corporate checkout has an internal reference-number field and a "dengan faktur pajak" checkbox ([ITB Bhinneka guide](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/03/Corporate-Purchase-Bhinneka.com_.pdf)). Mbizmarket's RFQ form asks the buyer to choose WAPU or non-WAPU payment ([ITB Mbizmarket guide](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/05/Mbizmarket-Alur-Transaksi.pdf)). Blibli lets buyers request a faktur pajak from the order page ([Blibli](https://www.blibli.com/faq/topic/pembayaran/)).

If the cooperative is PKP, Unit Toko's checkout summary should show DPP, PPN and the amount payable separately, and take BNI's NPWP and NITKU from the organisation profile rather than from each requester. It should never nudge requesters to split orders below the threshold, whether to avoid tax collection or approval. The research could not confirm whether BNI remains a designated collector after the 2025 changes to BUMN law, or whether the cooperative is PKP. BNI's tax function needs to confirm both.

### Order tracking is the account feature buyers value most

Baymard's May 2026 research finds that **50% of respondents name order tracking as the most important account feature**. Yet 67% of test sites fail to give all key tracking details consistently, and 25% fail to give an expected delivery date. Baymard lists six required elements: expected delivery date, a status progress bar, the carrier, a linked tracking number, a detailed shipping history and a summary of contents ([Baymard](https://baymard.com/research-articles/integrate-tracking-info)).

The benchmarks show how orders are organised:

| Platform | Order list and tracking |
|---|---|
| Shopee | "Pesanan Saya" sorts orders into Belum Bayar, Dikemas, Dikirim, Selesai, Dibatalkan and Pengembalian ([Shopee Help](https://help.shopee.co.id/portal/4/article/72518-%5BPelacakan-Pesanan%5D-Di-mana-saya-melihat-status-pesanan)) |
| Tokopedia | "Daftar Transaksi" keeps five years of history, with date, category and status filters plus search ([Tokopedia Care](https://www.tokopedia.com/help/article/cara-melihat-daftar-pesanan)). Its recommended couriers publish pickup and delivery photos at each milestone ([Tokopedia Care](https://www.tokopedia.com/help/article/faq-seputar-kurir-rekomendasi)) |
| Amazon | Each order card offers tracking, returns, the invoice and "Buy it again" ([Amazon](https://www.amazon.com/your-account)) |
| LKPP | A fixed status sequence from "Menunggu Konfirmasi Penyedia" through "Dalam Pengiriman" and "Pesanan Tiba" to "Selesai", with time limits that cancel stalled orders ([INAPROC](https://bantuan.inaproc.id/hc/id-id/articles/12048176695823-Panduan-PPK-Membuat-dan-Mengelola-Pesanan)) |

LKPP shows how Indonesian institutional buyers expect statuses to read. Shopee's cancellation model is also worth copying: instant self-service before the seller arranges shipping, a request the seller must answer within 24 hours after that, and only a return once the parcel has shipped ([Shopee Help](https://help.shopee.co.id/portal/4/article/87564-%5BPembatalan-Pesanan%5D-Apa-saja-jenis-pembatalan-pesanan-di-Shopee)).

Unit Toko's "Pesanan saya" should have six tabs: Semua, Perlu persetujuan, Diproses, Dikirim, Selesai, Dibatalkan. Tabs give the quick view. A search by order or PO number and a date filter serve long institutional histories, as Tokopedia's do. Each card should show:

- order number and date;
- branch;
- item thumbnails and total;
- a status pill;
- the next action ("Lacak", "Konfirmasi diterima", "Pesan lagi", "Unduh faktur").

The detail page should carry:

- a five-step stepper: Diajukan → Disetujui → Diproses → Dikirim → Diterima;
- Baymard's six elements, adapted to the cooperative's own delivery, with the driver in place of a courier and the surat jalan number in place of a resi when no external courier is used;
- photo proof of delivery;
- the documents BNI finance needs: invoice, surat jalan or receipt, and faktur pajak.

Repeat buying belongs on these screens too. A distributor-portal vendor reports that one-click reorder on past orders is how returning customers place most orders ([Ask the Ledger](https://asktheledger.com/b2b-web-portal.html)). Mbizmarket offers "Repeat Order" ([Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718)), and B2B practitioners recommend a quick-order pad, linked from the main navigation, that accepts typed or pasted codes and quantities ([Chykalophia](https://www.chykalophia.com/insights/ui-ux-best-practices-b2b-e-commerce)). Unit Toko already has "Pesan ulang sebagai draf" and a bulk SKU entry, "Masukkan banyak SKU" (local codebase observation). The redesign should move them out of the drafts workspace and onto the order card and the header, as a "Pesan cepat" link for signed-in buyers. Lazada's guest tracking by order number ([Lazada](https://www.lazada.co.id/)) should not be copied, because branch orders are institutional data.

## Design for a 360-pixel Android and a laptop in the branch office

The device context points two ways at once. Android holds **over 90% of Indonesia's mobile OS share** ([Statista](https://www.statista.com/statistics/262205/market-share-held-by-mobile-operating-systems-in-indonesia)), and 2024 smartphone growth came mainly from ultra-low-end phones ([IDC](https://www.idc.com/?p=103955)). Median mobile download speed was 29.06 Mbps at the start of 2025 by Ookla's measure ([DataReportal](https://datareportal.com/reports/digital-2025-indonesia)) and 50.77 Mbps in Speedtest's November 2025 index ([Bisnis.com](https://teknologi.bisnis.com/read/20251222/101/1938772/kecepatan-internet-seluler-ri-november-2025-lebih-cepat-dari-kamboja-dan-laos)); the two use different methods. The most common mobile viewport is 360×800 ([StatCounter](https://gs.statcounter.com/screen-resolution-stats/mobile/indonesia)). Yet desktop carries about 35% of web traffic, and more than half of internet users also use a laptop or desktop ([DataReportal](https://datareportal.com/reports/digital-2026-indonesia)). Branch staff will order from office PCs as often as from phones, and approvers will often decide on a phone, so both layouts are first-class.

Performance work pays off measurably in this market. Tokopedia's programme of selective third-party scripts, code splitting, adaptive images, lazy-loading and a bundle budget enforced in CI raised **click-through by 35% and conversion by 8%** ([web.dev](https://web.dev/case-studies/tokopedia)). Server-rendering and preloading the largest image improved LCP by 55% and average session duration by 23% ([web.dev](https://web.dev/case-studies/vitals-business-impact)). Serving a 500 px WebP instead of an 800 px JPEG saved 50 kB on a single image ([Tokopedia Engineering](https://medium.com/tokopedia-engineering/getting-content-painted-under-2-seconds-on-the-mobile-web-7b3bbaca32cb)). The targets are LCP ≤ 2.5 s, INP ≤ 200 ms and CLS ≤ 0.1 at the 75th percentile ([web.dev](https://web.dev/articles/vitals)).

The public-catalogue spec stores one rendition per photo: a JPEG of up to 800 px and 400 KB (local codebase observation). A 48-card page served at that ceiling would approach 19 MB. Cards therefore need a slot-sized rendition, around 300 px in WebP, beside the 800 px product-page image, plus fixed 1:1 boxes that hold the layout still. Marketplace image rules support that box. Tokopedia asks sellers for square photos of at least 600 × 600 px on white, with the product filling at least 60% of the frame ([Tokopedia Seller Center](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en); [Tokopedia Seller Center](https://seller-id.tokopedia.com/university/essay?knowledge_id=7065663894374161)). Shopee asks for white cover photos with the product filling 70% ([Shopee Seller Education](https://seller.shopee.co.id/edu/article/350)).

Interaction patterns:

- **Sticky action bar:** on product, cart and checkout pages, a thumb-zone bar with the price or total on the left and the primary action on the right. Hide the tab bar on those pages, as marketplace product pages do ([AKSA layout study](http://www.aksa.stsrdvisi.ac.id/index.php/aksa/article/download/37/35)). Sticky add-to-cart evidence is mixed, with vendor A/B tests reporting both lifts and drops ([cleancommit](https://cleancommit.io/ab-tests/sticky-mobile-add-to-cart-button/)), so measure it rather than assume it.
- **Bottom sheets:** for quantity and filters, with a visible close button, because the swipe gesture is ambiguous and inaccessible to some users ([NN/g](https://www.nngroup.com/articles/bottom-sheet/)), and an explicit "Terapkan".
- **Tap targets:** Apple's 44 pt and Android's 48 dp, above WCAG's 24 px minimum ([W3C](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html)).

Marketplace colours are not a model for text. Tokopedia's green #03AC0E reaches about **3.0:1** on white and Shopee's orange #EE4D2D about 3.7:1, both below WCAG AA's 4.5:1 for body text (researcher calculation on brand values from [LogoLabs](https://www.logolabs.org/logos/tokopedia) and [ColorCodeHub](https://colorcodehub.com/brand/shopee)). Unit Toko's primary teal #087f8c passes with white text, narrowly, at 4.75:1. Its status badges measure 3.3–3.8:1 and its muted text 2.9–3.3:1 (local measurement of `globals.css`, 19 October 2026). Prices should be bold near-black: 16 px on cards, 20–24 px on product pages. Discounts and warnings need darker shades, and every status pill must pair a word and an icon rather than rely on colour ([NN/g](https://www.nngroup.com/articles/error-message-guidelines/)).

Loading, empty and error states follow NN/g:

- **Loading:** skeletons shaped like the real cards for full-page loads under ten seconds, spinners for single modules, and never a skeleton that shows only the page frame ([NN/g](https://www.nngroup.com/articles/skeleton-screens/)).
- **Empty:** say what the state is, teach, and link to the fix ([NN/g](https://www.nngroup.com/articles/empty-state-interface-design/)).
- **Errors:** network errors appear inline, with "Coba lagi", and keep the cart and inputs intact.
- **Toasts:** "Berhasil ditambahkan ke keranjang · Lihat" sits above the sticky bar, never over it.
- **Motion:** about 100 ms for feedback and 200–300 ms for sheets ([NN/g](https://www.nngroup.com/articles/animation-duration/)).

Trust signals differ for a closed cooperative. Indonesian shoppers name coupons (52.3%), reviews (48.2%), free shipping (47.4%) and easy checkout (45.5%) as purchase drivers ([GoodStats](https://data.goodstats.id/statistic/6-faktor-ini-mendorong-pembelian-dalam-proses-belanja-online-di-indonesia-nxDZ9)). 98% read reviews before buying, though most report having been misled by them at some point ([Kompas.id](https://www.kompas.id/artikel/mayoritas-konsumen-toko-daring-merasa-pernah-tertipu-ulasan-2)). WhatsApp is the country's most-used social platform ([Databoks](https://databoks.katadata.co.id/en/technology-telecommunications/statistics/69959bb37aa8b/10-most-used-social-media-platforms-in-indonesia-in-q2-2025)). Most consumer levers do not apply to Unit Toko. The credible signals are:

- a final contract price that is identical on the card, in the cart and on the invoice;
- a delivery date;
- clear return and complaint rules;
- order proof in the account;
- a "Tanya via WhatsApp" link in a consistent place, whose prefilled text never carries prices.

Bukalapak's design principles say to "default to standard and innovate only when it's worth it" ([Bukalapak Design](https://medium.com/bukalapakproductdesign/evolving-product-design-principles-at-bukalapak-366ab45ae5a6)), and marketplace promotional clutter is exactly what Tokopedia's own card research stripped out. Unit Toko already addresses users as "Anda" consistently (local codebase observation). It should keep that register and adopt the verbs Indonesian shoppers already know: "Masuk", "Tambah ke keranjang", "Buat pesanan", "Pesan lagi", "Lacak", "Terapkan", "Lihat semua".

## Conclusion

The research moves the core question from "how much should guests see" to "how fast does a known user get back to where they were". Because Unit Toko's buyers are provisioned branch staff, its public catalogue is mostly a doorway for people who already hold accounts. Three details decide whether the owner's price gate feels like a door or a wall: the return path that lands on the same product, the absence of a misleading "Daftar", and a header that makes "Masuk" impossible to miss. The benchmarks also split cleanly at the cart. Indonesian marketplaces are the right model for everything before it, because branch staff shop on those apps after work and arrive with those habits. Procurement platforms are the right model after it, because a branch order is a commitment that someone else signs off and finance must reconcile. What does not transfer is telling: vouchers, express buying, star ratings, seller-location filters and guest tracking by order number. The additions only B2B needs are small on screen but large in consequence: a button that names the approval before it happens, a tax summary that respects WAPU, and reorder on every past order.

The evidence has gaps that a pilot can close faster than more desk research. Nobody has published usability data on login-to-see-price. No Indonesian B2B storefront research exists. Current marketplace tab labels and signed-in headers could not be observed. BNI's tax-collector status is unconfirmed. Unit Toko should instrument the sign-in loop, approval turnaround and WhatsApp "where is my order" contacts from the first pilot week. Those numbers, not benchmark screenshots, should settle the open choices, such as the sticky add-to-cart bar and the mobile tab bar.

## Prioritised redesign: one header, one sign-in loop, then the purchase path

### The target buyer flow, step by step

| Step | Guest (tamu) | Branch buyer (pemesan) | Approver (penyetuju) |
|---|---|---|---|
| Catalogue list (`/katalog` for guests, `/shop` signed in) | Same grid and card as buyers. The price slot reads "Masuk untuk lihat harga". Sorts: Paling sesuai (search), Terbaru, Nama A–Z. No price sort or filter. Category chips and a "Semua kategori" sheet | Contract price per unit, one tier hint, minimum order when above one. Extra sorts: Terlaris, Pernah dibeli, Harga. Filters: Merek, Kemasan, availability, category attributes, Harga. Applied-filter chips. "Muat lebih banyak" in batches of 48 (desktop) or 24 (mobile), with a count line and the position restored on Back | Same as buyer |
| Product page | Photo, name, pack contents, attributes, minimum order. The price block is replaced by the sign-in prompt. Button: "Masuk untuk memesan", which carries the chosen quantity through sign-in | Contract price, tier table, DPP and PPN, stock, a stepper from the minimum by the increment, a live subtotal, a delivery date. "Tambah ke keranjang" becomes "Di keranjang · n" with a stepper. Persistent confirmation and cart badge | Same as buyer |
| Sign in (`/identity/login?returnPath=`) | One page that allows password managers and paste. A "Belum punya akses?" link to the access section. Returns to the priced version of the same page | — | Staff without a returnPath land on `/workspace` |
| Keranjang (`/cart`) | The tab or icon opens a sign-in prompt sheet | One auto-saved cart per branch. Line checkboxes and "Pilih semua". Inline warnings. A "Tidak dapat diproses" section. Sticky summary with "Lanjutkan (n)". The empty state offers "Pesan lagi" | Same as buyer |
| Checkout (one page) | — | Branch address with "Ubah", delivery date, notes, "Nomor PO / referensi", payment or terms. The DPP, PPN and total summary sits at the top on mobile. An approval line names the rule and the approver. Button: "Buat pesanan", or "Ajukan persetujuan" when a rule applies | Their own orders still go to someone else; the approve button is replaced by who must approve |
| After submitting | — | Confirmation with order number, status, approver and deadline (or the next step), "Lihat pesanan" and "Lanjut belanja". On rejection: the reason plus "Perbaiki dan ajukan ulang" | Header badge and notification. Queue with approve, reject with a required reason, and per-line rejection. Delivery address read-only. Backup approver |
| Orders and tracking (`/ordering/orders`) | — | Tabs (Semua, Perlu persetujuan, Diproses, Dikirim, Selesai, Dibatalkan), search by order or PO number, date filter. Cards with the next action. Detail page with the five-step stepper, delivery date, driver or courier, surat jalan number, history, contents, photo proof, documents, "Pesan lagi", "Konfirmasi diterima" | The approval queue is reached from the header badge and the account menu |

### Header and navigation by audience

| Element | Guest | Branch buyer | Approver | Cooperative staff |
|---|---|---|---|---|
| Logo | Top left, links to `/` (landing) | Same | Same | Same in the back office, with "Pekerjaan saya" as the first sidebar item, so home and work are both one click away |
| Utility bar (desktop only, thin) | "Cara pemesanan cabang", "Bantuan · WhatsApp", "Ruang kerja petugas" (to `/identity/login?returnPath=/workspace`) | "Cara pemesanan cabang", "Bantuan · WhatsApp" | Same as buyer | Not shown in the back office |
| Main bar | "Kategori" panel, search box ("Cari barang, merek, atau kode"), **"Masuk"** as the filled primary button | "Kategori", search, a branch chip if the user serves more than one branch, "Pesan cepat", cart with item count, account menu showing name and branch | Adds "Persetujuan" with a count of orders waiting on this person only | On the storefront: "Kategori", search and a "Ruang kerja" button, with no cart |
| Account menu | — | Pesanan saya, Daftar belanja, Alamat cabang, Bantuan, Keluar | Adds Persetujuan and Aturan belanja (read-only) | The back office has its own menu; no staff items ever appear in a buyer menu |
| Mobile top bar | Logo mark (to `/`), search field, "Masuk" text button | Logo mark, search field, cart with badge | Same as buyer | Back-office top bar with a sidebar toggle |
| Mobile bottom tabs (hidden on product, cart and checkout pages) | Katalog, Keranjang, Pesanan, Akun. Keranjang and Pesanan open a sign-in sheet; Akun shows "Masuk", help and how to get access | Katalog, Keranjang, Pesanan, Akun | Same as buyer; the Akun tab shows a badge and opens with "Persetujuan (n)" | None; the back office uses its sidebar |
| Landing after sign-in | The returnPath | `/shop` when there is no returnPath | `/shop`, with the approvals badge visible | `/workspace` ("Pekerjaan saya") |

The staff back office keeps the existing domain-grouped workspace frame, but as a separate route group. That gives it its own shell, no storefront header, and none of the storefront's CSS or JavaScript. Staff reach it through the same login page; the role-based landing sends them to `/workspace`, which today only redirects to `/identity/account` and should become the real staff home. A "Lihat toko" link opens the storefront in its public state. A single login page with role-based landing is preferable to Lazada-style separate login groups here, because Unit Toko has one identity system, and two login pages would recreate the confusion the owner wants removed.

### Priorities and acceptance checks

| # | Change | Why (evidence) | Done when |
|---|---|---|---|
| **P0.1** | One `StoreHeader` for every buyer page (catalogue, product, cart, checkout, orders, account), in guest, buyer and approver states, plus the mobile tab bar. It replaces the pilot header, the checkout header and the priced catalogue's header | Tokopedia and Lazada header recipe; NN/g's 6× logo finding | Every buyer route renders it. A repository search finds no logo link to anything but `/`. Playwright at 390 and 1440 px shows "Masuk" in the first viewport when signed out, and the cart badge when signed in |
| **P0.2** | Staff back-office door: `/workspace` becomes the staff home inside the workspace frame. Staff land there after login unless a returnPath points elsewhere. "Ruang kerja petugas" link in the utility bar and footer | Separate seller centres with their own logins (Lazada, Blibli, Tokopedia) | No staff journey renders `StoreHeader`. No buyer menu contains staff items. Footer and utility-bar links point to login with `returnPath=/workspace` |
| **P0.3** | Public-catalogue sign-in loop: the "Masuk untuk lihat harga" slot on public cards and product pages; a "Masuk untuk memesan" button; returnPath set to the priced version of the same page, quantity included. A signed-in visit to `/katalog/<id>` redirects to the priced page. A guest on a non-public `/shop/<id>` goes to login with a returnPath. No price sort or filter for guests | Shopee `next=`; practitioner login-to-see-price guidance; NN/g on hidden prices | End-to-end test: a guest opens public product X, taps "Masuk", signs in and lands on priced X with the chosen quantity. The guest DOM contains no price sort option |
| **P0.4** | Retire the duplicate `/catalog/browse` route (redirect to `/shop`). Adopt "produk/barang" as the product noun and keep "kemasan" for the pack line. Drop "Daftar" | Baymard on clear terms; benchmark vocabulary; provisioned accounts | One buyer catalogue route. String lint enforces the glossary |
| **P1.1** | Keranjang as a real `/cart` page: auto-saved per branch, stepper with a typeable field, minimum and increment enforced inline, line selection, an unprocessable section, sticky summary, persistent add-to-cart confirmation and badge. Drafts become "Daftar belanja" | NN/g cart and feedback research; Baymard's 97% stepper gap; Shopee and LKPP cart states | `/cart` renders without a redirect. A quantity change persists with no save step. The same cart appears on a second device. Adding an item already in the cart shows "Di keranjang" |
| **P1.2** | One-page checkout with an approval-aware button. Summary first on mobile. "Nomor PO / referensi" field. DPP, PPN and total shown separately. Delivery shown as a date. One message per refusal reason | Amazon Business "Submit order for approval"; Baymard field and error data; Shopee's refusal reasons; PMK 8/2021 | An above-threshold order names the rule and approver before submission, and the button reads "Ajukan persetujuan". A below-threshold order reads "Buat pesanan". Each refusal reason has its own copy |
| **P1.3** | Approval states for both sides: confirmation with approver and deadline; reminders and expiry; rejection with reason and one-tap restore to the cart. Approver queue with a badge, a required reject reason, per-line rejection, read-only address, a backup approver, and a phone layout | Amazon Business, Staples, Bhinneka, Mbizmarket, LKPP | End-to-end tests cover approve, reject without a reason (blocked), per-line reject, expiry, restore after rejection, and a requester's own order showing who must approve |
| **P1.4** | "Pesanan saya" with status tabs, search and date filter. Order cards with the next action. Detail page with the stepper, Baymard's six elements, photo proof, documents, "Pesan lagi" and "Konfirmasi diterima" | Baymard tracking research (50% / 67% / 25%); Shopee, Tokopedia, Amazon, LKPP | A shipped test order renders all six elements. Documents download. "Pesan lagi" refills the cart at current prices and flags changed lines |
| **P2.1** | Search depth: a synonym and unit table, exact and partial code matching, autocomplete capped at 8 on mobile and 10 on desktop with category-scope rows, typo tolerance, category auto-direct, and a zero-result page with alternatives, "Minta barang di luar katalog" and WhatsApp | Baymard search statistics; Ralali tender pattern | A set of 50 real branch queries (codes, units, brand-as-generic terms, typos) returns the intended item in the top three |
| **P2.2** | Filters and sorts as specified per audience, with option counts, removable chips, "Hapus semua", and a mobile tray with "Terapkan" and a close button | Tokopedia filter redesign; Baymard filter and sort statistics; NN/g tray | Playwright checks chip removal, counts, and that results stay visible behind the mobile tray |
| **P2.3** | Card stack and paging: square image, name, pack line, unit price, tier hint, minimum order; "Muat lebih banyak" with a count; position restored on Back | Tokopedia card research; Baymard unit-price, variant and paging guidance | Visual baselines at 360 and 1440 px. Going Back from a product restores the batch and the scroll position |
| **P2.4** | Repeat buying: "Pernah dibeli" and a curated "Daftar belanja rutin" per branch with inline steppers; a "Pesan cepat" header link opening the existing bulk SKU entry | Blibli "pernah dibeli"; Amazon Business lists; quick-order-pad practice | A routine restock of 15 lines can go from the list to submission without a catalogue search |
| **P3.1** | Card-size image renditions (about 300 px WebP) beside the 800 px rendition; fixed 1:1 boxes; route budgets | Tokopedia performance case studies; Core Web Vitals | p75 LCP ≤ 2.5 s and CLS ≤ 0.1 on catalogue and product routes in a mid-range Android lab profile |
| **P3.2** | Funnel measurement: guest "Masuk" click rate, login completion from the catalogue, add-to-cart after sign-in, approval turnaround, expiries, and WhatsApp order-status contacts | No published login-to-see-price data; practitioner metrics | A dashboard exists and is reviewed after the second pilot week, and the sticky-bar and tab-bar decisions are revisited against it |

Before P1 ships, five moderated tasks with real branch officers on their own phones and office PCs would test the riskiest assumptions in under a day:

1. Find an item by its code.
2. Sign in from a public product page and land back on it.
3. Restock from a past order.
4. Submit an order that needs approval.
5. Find a shipped order's delivery date.

If any task needs help, the design changes before the code does.
