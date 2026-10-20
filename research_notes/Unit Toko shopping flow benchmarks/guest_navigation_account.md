# Guest vs signed-in navigation, header and account entry points: Indonesian marketplaces plus Amazon and Alibaba

Research date: 21 October 2026. Live pages were fetched without signing in. Shopee, Blibli, Amazon and the Shopee and Lazada seller portals either blocked the fetcher (HTTP 403, bot check or empty JS shell) or render client-side, so their headers come from crawled copies and help pages, not from a live render. Bukalapak no longer sells physical goods (see Q1). It is a weak benchmark for a goods shop.

## Q1. What can a guest see, where is login required, and what does the login interruption look like?

### Takeaway
Every Indonesian marketplace lets guests browse the home page, categories, search results and product pages with prices, discounts, ratings and sold counts. All of them require an account before checkout: Shopee and Blibli say so explicitly in their help centres, and Shopee's web cart is gated even before checkout. The documented interruption is a redirect to a full-page login that carries the return path (Shopee's `next=` parameter, Lazada's `target=`). No platform offers guest checkout. Amazon reportedly has none either; Alibaba requires a buyer account for messaging, inquiries and orders.

### Cited Findings
**Shopee Indonesia**
- On the web, a guest's mini-cart shows "Please log in to view cart / add items to cart". It links to `/buyer/login?from_source=mini_cart_btn&next=`, so the cart is behind login and the login URL carries a return-path parameter (`next`) and a source tag (`from_source`). — [Shopee Mart page (crawled)](https://shopee.co.id/m/mart); same header on [Shopee Run page](https://shopee.co.id/m/shopee-run)
- Shopee ID help: "Pastikan Anda telah membuat akun Shopee dan menambahkan alamat pengiriman Anda terlebih dahulu" and "Pastikan Anda sudah membuat akun Shopee sebelum melakukan checkout." Buying paths are Tambahkan Keranjang → Checkout, or Beli Sekarang → straight to shipping and payment. Maximum 40 products per checkout. — [Shopee ID Help: cara pembelian](https://help.shopee.co.id/portal/9/article/115340-[Baru-di-Shopee]-Bagaimana-cara-melakukan-pembelian-di-Shopee-Lite)
- Shopee PH help states plainly that you cannot place an order or check out without a Shopee account. — [Shopee PH Help](https://help.shopee.ph/portal/4/article/82303)
- Before logging in, shoppers can search and compare price, rating and reviews, and distance to the shop. "Chat Sekarang" leads to bargaining ("Tawar") when the seller allows it. — [Inspirasi Shopee](https://shopee.co.id/inspirasi-shopee/ingin-tahu-cara-belanja-di-shopee-yuk-ikuti-langkahnya/amp/)
- Login methods: phone number with OTP, email and password, Google, and a "Log in dengan QR" option on web, where the signed-in phone app scans a QR code shown on the PC. — [Kompas Tekno, Aug 2024](https://tekno.kompas.com/read/2024/08/08/15350037/cara-login-akun-shopee-di-web-pakai-scan-praktis-tanpa-masukkan-e-mail)
- A low-quality blog says guests can search and read product details and reviews, and that pressing "Beli Sekarang" or add-to-cart triggers the login or registration prompt. — [cardsplug blog (low reliability)](https://dash.cardsplug.com/cardsplug-news/cara-belanja-di-shopee-tanpa-akun-1764804290)

**Tokopedia (now part of TikTok Shop)**
- A guest search page (`/find/barang-unik-terbaru`) shows the full results to a signed-out visitor: price (e.g. Rp19.932, struck-through Rp100.000, 80% off), a "Hemat s.d 8% Pakai Bonus" label, rating 5.0, "12 terjual", shop badge, category filters, sort ("Urutkan: Paling Sesuai"), a result count ("Menampilkan 103 produk… 1 - 60 dari 103") and a breadcrumb starting at "Tokopedia". — [Tokopedia find page (fetched)](https://www.tokopedia.com/find/barang-unik-terbaru)
- Help-centre buying flow: Tambah Ke Keranjang → Lihat Keranjang → Beli → Checkout page (check address, choose courier). Payments for products from different shops can be combined. — [Tokopedia Care: cara bayar](https://www.tokopedia.com/help/article/cara-bayar-di-tokopedia)
- Since integration, product name, description, stock, rating, reviews and price shown in the Tokopedia app come from the seller's TikTok Shop listing. The "Diskusi" (product Q&A) section "will be dismissed from the Tokopedia app". — [Tokopedia Seller University: Integration to Tokopedia App](https://seller-id.tokopedia.com/university/essay?knowledge_id=8392477993174785&lang=en); [Seller Center Directory After Upgrade](https://seller-id.tokopedia.com/university/essay?knowledge_id=2405512621639440&lang=en)

**Lazada Indonesia**
- The desktop header gives guests a "LACAK PESANAN" (track order) box that takes an order number ("Nomor pesanan"). Separately, "login" goes to `member.lazada.co.id/user/login` and "DAFTAR" to `member.lazada.co.id/user/register`, both full pages on an auth subdomain. — [Lazada.co.id home (fetched)](https://www.lazada.co.id/)
- Account pages sit on another subdomain (`my.lazada.co.id`: orders, wishlist, reviews, returns). Auth is on `member.lazada.co.id`. — [Lazada.co.id home (fetched)](https://www.lazada.co.id/)

**Blibli**
- Blibli FAQ: "Apakah saya bisa berbelanja di Blibli jika belum memiliki akun? Tidak bisa. Kamu wajib memiliki akun sebelum bisa berbelanja di Blibli." — [Blibli FAQ: Verifikasi akun](https://www.blibli.com/faq/topic/registrasi-info/verifikasi-akun/)
- Step 1 of "cara belanja" is to have an account and be signed in. Next comes "Beli sekarang" (straight to checkout) or "Tambah ke Bag" (Blibli calls the cart "Bag") → Checkout → address and shipping → "Bayar sekarang". — [Blibli FAQ: Cara belanja](https://www.blibli.com/faq/topic/belanja/cara-belanja/)
- Login goes through an "Akun Blibli Tiket": one account with one verified phone number and email signs in to Blibli, tiket.com and RANCH. The login steps are: choose "Masuk", enter email or phone plus password. — [Blibli FAQ: Login](https://www.blibli.com/faq/topic/registrasi-info/login/)
- Play Store reviews from September 2026 complain about a transaction PIN demanded before paying and OTP dependence when a phone number is lost. This is anecdotal evidence of extra gates at payment. — [Blibli on Google Play](https://play.google.com/store/apps/details?id=blibli.mobile.commerce&hl=id)

**Bukalapak (changed in 2025)**
- Bukalapak shut its physical-goods marketplace and now sells only virtual products. New product uploads were disabled on 1 Feb 2025, the last physical orders were taken on 9 Feb 2025 at 23:59 WIB, unprocessed orders were auto-cancelled on 2 Mar 2025, and complaints were handled until 30 Jun 2025. — [Bukalapak official blog](https://blog.bukalapak.com/feature-updates/transformasi-bukalapak-123520); [Reuters, 8 Jan 2025](https://www.reuters.com/business/retail-consumer/indonesia-e-commerce-firm-bukalapak-stop-selling-physical-goods-competition-2025-01-08/)
- The current homepage greets guests with "Selamat Datang! 👋🏻 Silakan login/register terlebih dahulu." above virtual-product widgets (Pulsa, Paket Data, Listrik Prabayar, E-Wallet, BukaEmas, Lapakgaming). — [Bukalapak.com (fetched)](https://www.bukalapak.com/)

**Amazon (contrast)**
- Econsultancy's UK guest-checkout test reported that Amazon has no guest checkout and that the next step asks the shopper to create a password. — [Econsultancy guest checkout test](https://www.econsultancy.com/guest-checkout-test-which-of-these-30-uk-retailers-force-users-to-register/). Some low-quality blogs claim a guest option exists for some products. — [thetechylife (low reliability)](https://thetechylife.com/can-you-buy-stuff-on-amazon-without-an-account/). The sources conflict, and no Amazon help page was found.
- The live amazon.com fetch returned only a bot interstitial ("Click the button below to continue shopping"), so the header could not be checked. — [amazon.com (fetched)](https://www.amazon.com/)

**Alibaba.com (B2B contrast)**
- Header strings embedded in the live homepage show what is gated: "Sign in to view message details", "Sign in to view inquiry details", "Manage orders as a buyer", and order states "Waiting for payment" and "Waiting for confirmation". — [Alibaba.com home (fetched)](https://www.alibaba.com/)
- Alibaba's sourcing guide says to "register for a free buyer account" first, and that suppliers check buyer credentials (company name, website, business email). Buyers then either "Start Order" (ready-to-ship items) or "Contact Supplier" (inquiry or negotiation), or post an RFQ. — [Alibaba: How to Source](https://activity.alibaba.com/page/how_to_source.html); [Alibaba Buyer Central: How it works](https://buyer.alibaba.com/page/HowItWorks/Page.html)
- Registration asks for a trade role, so the user picks "buyer". The MOQ filter ("Min. Order" = 1) finds single-unit items. Items without "Ready to Ship" need "Contact Supplier". — [Alibaba Reads: buy one item](https://reads.alibaba.com/buy-1-item/)
- The cart flow is Add to Cart → Go to Cart → Check Out. Multi-supplier carts are split into one order per supplier at checkout. — [Alibaba Help: shopping cart](https://helpcenter.alibaba.com/s/buyer/knowledge?category=9207644&categoryId=9207644&knowledge=1000101830&language=en&pageId=128&questionId=55bc9f6b616e4ddf8dd16130359a7698sgvpc278077001&questionId2=1000101830)

### Comparison matrix (synthesised from the findings above)
| Platform | Guest browse and search | Guest sees price | Login wall at | Interruption style seen | Guest checkout |
|---|---|---|---|---|---|
| Shopee ID | Yes | Yes | Cart (web mini-cart), checkout | Full-page `/buyer/login?…&next=` | No |
| Tokopedia | Yes | Yes (incl. discount, sold count) | Checkout (cart gating not verified) | Not verified | No (inferred, not stated) |
| Lazada ID | Yes; order tracking by order number | Yes | Account pages | Full page on `member.lazada.co.id` | Not verified |
| Blibli | Not verified (site returned 403) | Not verified | Shopping requires an account | "Masuk" page | No (FAQ: "Tidak bisa") |
| Bukalapak | Virtual products only since Feb 2025 | n/a | Homepage asks to log in | Inline greeting | n/a |
| Amazon | Not verified | Not verified | Checkout | Not verified | No, per Econsultancy (conflicting blogs) |
| Alibaba | Yes | Not verified | Messages, inquiries, orders, favourites | "Sign in to view…" prompts | No (buyer account needed) |

### Inferences
- In Indonesia the standard is open browsing with prices visible plus a mandatory account at the point of commitment (cart or checkout). Shoppers know this pattern. A cooperative shop that hides prices from guests would be stricter than every B2C benchmark here. Alibaba gates communication and orders, but price visibility for guests was not verified (see Gaps).
- Login links that carry the origin and a return URL (`next=`, `target=`) are the established mechanism for "return to where I was" after login.
- No Indonesian platform offers guest checkout, so requiring an account fits local norms. Western UX research (Q6) still warns that forced sign-up costs conversions, so the cost should be cut with fast phone OTP or Google login, as Shopee and Blibli do.

### Gaps
- Whether Tokopedia and Lazada let guests add to cart (as a local cart) or demand login on the click could not be confirmed. The live product pages are JS-rendered.
- Whether login appears as a modal or a full page on Tokopedia, Blibli and in the apps could not be seen without a live browser session.
- Whether guests see stock counts and reviews on Tokopedia, Lazada and Blibli product pages was not checked directly. Shopee guests see reviews per the blog and Inspirasi Shopee; Tokopedia's search card shows sold count and rating.
- Whether Alibaba.com shows product prices and MOQ to signed-out guests was not confirmed from a primary source.

## Q2. How is the header laid out on desktop and mobile for guests vs signed-in users?

### Takeaway
Desktop headers share a two-tier layout. A thin utility bar (download app, about, "become a seller", help, track order) sits above a main bar with the logo top-left linking to home, a central search bar with trending keyword chips, a cart icon, and "Masuk" plus "Daftar" for guests. For signed-in users, an account dropdown replaces the login buttons. Lazada's markup lists that dropdown: Panel Akun, Pesanan Saya, Wishlist, Ulasan Saya, Pengembalian & Pembatalan, Logout. Alibaba's header shows message, inquiry and order counters that turn into "Sign in to view…" prompts for guests.

### Cited Findings
- **Tokopedia desktop (guest):** the utility bar reads "Download Tokopedia App | Tentang Tokopedia | Mulai Berjualan | Promo | Tokopedia Care". The main bar has the logo linking to `https://www.tokopedia.com`, "Kategori", and buttons "Masuk" and "Daftar". Search result pages add a breadcrumb rooted at "Tokopedia". — [Tokopedia home (fetched)](https://www.tokopedia.com/); [Tokopedia find page (fetched)](https://www.tokopedia.com/find/barang-unik-terbaru)
- **Lazada desktop (guest):** the top bar holds "Masukan" (feedback), a "LEBIH MURAH DI APP" download promo, "MENJADI SELLER", a customer-care dropdown (Pusat Bantuan, Order & Pembayaran, Pembatalan Pesanan, Pengiriman, Pengembalian Barang & Dana, 24-hour Live Chat), "LACAK PESANAN", "login" and "DAFTAR". The page markup also contains the signed-in account menu: Panel Akun, Pesanan Saya, Wishlist dan Toko yang Saya Ikuti, Ulasan Saya, Pengembalian & Pembatalan, Logout. — [Lazada.co.id home (fetched)](https://www.lazada.co.id/)
- **Shopee desktop:** a row of trending search keywords sits under the search bar (e.g. "Baju Kemeja Korean Style", "Tumbler Botol Minum…"). The cart icon shows a login prompt for guests ("Please log in to view cart / add items to cart"). — [Shopee Mart page (crawled)](https://shopee.co.id/m/mart)
- **Alibaba desktop:** the current header bundles message, inquiry and order widgets with signed-out states ("No new messages", "Sign in to view message details", "Sign in to view inquiry details") plus a ship-to ZIP field and WhatsApp connect. — [Alibaba.com home (fetched)](https://www.alibaba.com/). An older captured header shows "Sign In | Join Free", a "My Alibaba" menu (Message Center, Manage RFQ, My Orders, My Account), "Submit RFQ", an Orders counter (Pending Payment, Pending Confirmation, Pending Actions) and a Favorites counter with "Sign In to manage and view all items". — [Alibaba One-Stop Service page header](https://activity.alibaba.com/onetouch/one-stop-service.html)
- **Mobile apps (layout study of Bukalapak, Shopee, Tokopedia and Lazada):** all four put search at the top of the home screen, then a large promo banner carousel, then colourful shortcut icons (vouchers, bill payment, games). Top and bottom navigation icons are simple monochrome icons for wallet, notifications, messages, cart and account. Shopee and Bukalapak placed the cart icon at the top. Tokopedia and Lazada placed it in the bottom navigation. The product page has Chat, Add to cart and Buy in a bottom bar. Study date not shown; probably around 2020. — [Dzulfikar & Prajarini, AKSA journal](http://www.aksa.stsrdvisi.ac.id/index.php/aksa/article/download/37/35)
- **Tokopedia app "Menu Utama" (2021):** a hamburger entry in the top bar on every main page holds the wallet (membership, balance), a "Back to Home" button, active order status, wishlist, favourite shops, reviews, categories, and Tokopedia Care and complaints. Header icons were unified across the browsing funnel by urgency, with the Menu Utama icon present on every page. — [Life at Tokopedia (Medium), Apr 2021](https://medium.com/life-at-tokopedia/introducing-menu-utama-on-tokopedia-3cbeaf54045c)
- **Tokopedia web vs app (2019):** the app's bottom tabs shorten access to account and transactions. On the website, account and order menus sit at the top. — [caratokopedia.com, 2019 (dated)](https://www.caratokopedia.com/2019/01/perbedaan-menggunakan-tokopedia-hp-laptop.html)

### Inferences
- Shared desktop recipe: logo at top-left linking to the root URL; a large central search box with suggestion chips; a cart icon; "Masuk" (secondary) and "Daftar" (primary) for guests, replaced by an avatar or name dropdown with orders, wishlist, reviews, returns and logout after login; a utility bar for seller recruitment and help.
- Lazada exposes order tracking in the header without login (by order number). This pattern suits a cooperative whose members may phone in or order offline.

### Gaps
- No live render of signed-in headers was possible. Avatar menu contents for Shopee, Tokopedia and Blibli (e.g. "Akun Saya", "Pesanan Saya", "Log Out") are not confirmed from 2025–2026 sources.
- Whether cart icons show item counts for guests, and notification bell placement on desktop, were not verified.
- Amazon's header ("Hello, sign in / Account & Lists / Returns & Orders / Cart") could not be verified because the page was blocked.

## Q3. How are seller and staff portals separated from the buyer site?

### Takeaway
All platforms run the seller back office on a separate subdomain with its own login: seller-id.tokopedia.com, sellercenter.lazada.co.id via login.lazada.com, seller.blibli.com, and seller.shopee.co.id. The buyer site links to them through a single recruitment link in the top bar ("Mulai Berjualan", "MENJADI SELLER") or the footer ("Jual di Lazada"), never through the buyer account menu. Tokopedia's seller back office moved into the "Tokopedia & TikTok Shop Seller Center" during 2025.

### Cited Findings
- **Tokopedia:** "Mulai Berjualan" in the top bar links to seller education at `seller.tokopedia.com/edu/...`. — [Tokopedia home (fetched)](https://www.tokopedia.com/). The integrated "Tokopedia & TikTok Shop Seller Center" (seller-id.tokopedia.com) runs both shops from one dashboard, and old Tokopedia Seller dashboard operations "will automatically be redirected" there. — [Shop Integration Guidelines, Jul 2025](https://seller-id.tokopedia.com/university/essay?knowledge_id=1189016095426305)
- The integrated Seller Center opened to all sellers on 8 April 2025. — [Kompas Money, 2 Jun 2025](https://money.kompas.com/read/2025/06/02/184927026/penjual-tokopedia-dialihkan-ke-tiktok-shop-begini-penjelasan-tiktok). From 9 June 2025, sellers still on the old Tokopedia Seller Center can trade and edit products but cannot advertise or add new products. — [CNBC Indonesia, 19 Jun 2025](https://www.cnbcindonesia.com/tech/20250619165237-37-642370/seller-tokopedia-ramai-ramai-migrasi-begini-nasib-yang-enggan-pindah); [Kompas Money, 19 Jun 2025](https://money.kompas.com/read/2025/06/19/164453126/integrasi-seller-center-tiktok-perlahan-hapus-fitur-di-tokopedia)
- Module mapping after the upgrade: Chat → Buyer Messages; Pengaturan Admin → User Management (admin data copied over); Performa Toko → Shop Health; Pusat Bantuan Penjual → Help Center; TokoMember and Diskusi are not available. — [Seller Center Directory After Upgrade](https://seller-id.tokopedia.com/university/essay?knowledge_id=2405512621639440&lang=en)
- **Lazada:** "MENJADI SELLER" links to `sellercenter.lazada.co.id/apps/register/index`, and the footer has "Jual Di Lazada" (`lazada.co.id/marketplace/`). — [Lazada.co.id home (fetched)](https://www.lazada.co.id/). An unauthenticated request to `sellercenter.lazada.co.id` redirects to `login.lazada.com/tbpass/jump?group=lazada-seller&target=https://sellercenter.lazada.co.id/`. That is a separate seller login group with a return target. Buyers log in at `member.lazada.co.id`. — observed redirect when fetching [sellercenter.lazada.co.id](https://sellercenter.lazada.co.id/)
- **Blibli:** Blibli Seller Center has its own login at `seller.blibli.com/sign-in` (email and password, or Facebook or Google). Internal staff emails on "gdn-commerce.com" are routed to SSO. The page links to "Belum jadi seller Blibli? Daftar Sekarang!" and to the Blibli Seller App. — [Blibli Seller login](https://seller.blibli.com/sign-in); [Blibli Seller login page variant](https://seller.blibli.com/learning/guide/article/ART-00119/menambahkan-alamat-toko-gudang)
- **Shopee:** sellers' livestream console has a separate PC login at `live.shopee.co.id/pc/login`. — [Kompas, Oct 2023](https://www.kompas.com/tren/read/2023/10/20/093000665/cara-live-di-shopee-via-ponsel-dan-pc?page=all). `seller.shopee.co.id` exists but returned an empty JS shell to the fetcher. — [seller.shopee.co.id](https://seller.shopee.co.id/)
- **Bukalapak:** agent and reseller business (Mitra Bukalapak) runs on `mitra.bukalapak.com`. — [Bukalapak.com (fetched)](https://www.bukalapak.com/)
- **Alibaba:** the trade role (buyer) is chosen at registration, and the header labels order management "as a buyer". — [Alibaba Reads](https://reads.alibaba.com/buy-1-item/); [Alibaba.com home (fetched)](https://www.alibaba.com/)

### Inferences
- Proven pattern for a cooperative: keep the staff or admin console on a separate route or subdomain with its own login page. Expose only one discreet link on the buyer site (top-bar or footer text link), and keep staff functions out of the buyer avatar menu. Blibli's split of staff SSO versus seller password is a precedent for internal staff signing in differently from members.
- A return target on the seller login (Lazada `target=`) supports deep links into the back office.

### Gaps
- The exact Shopee buyer-site link labels ("Seller Centre", "Mulai Jual") were not visible in fetched content because of JS rendering.
- Whether Tokopedia buyers and sellers now share one TikTok-linked identity, and whether buyers can log in with a TikTok account, was not confirmed.

## Q4. What happens when a signed-out user deep-links to an account-only page (orders, cart)?

### Takeaway
The evidence points to redirect-to-login with the original destination encoded in the URL. Shopee's cart link becomes `/buyer/login?from_source=…&next=…`, and Lazada's seller center becomes `login.lazada.com/…&target=<original URL>`. Alibaba instead renders inline "Sign in to view…" placeholders in header widgets. Direct tests of `/cart` or `/order-list` URLs on Tokopedia, Shopee or Blibli could not be run.

### Cited Findings
- The Shopee guest cart link goes to `/buyer/login?from_source=mini_cart_btn&next=`, with a return-path parameter and a source label for analytics. — [Shopee Mart page (crawled)](https://shopee.co.id/m/mart)
- An unauthenticated `sellercenter.lazada.co.id` redirects to `login.lazada.com/tbpass/jump?group=lazada-seller&target=https%3A%2F%2Fsellercenter.lazada.co.id%2F&hybrid=1&tbpm=1`. — observed redirect when fetching [sellercenter.lazada.co.id](https://sellercenter.lazada.co.id/)
- The Alibaba Favorites widget shows "Sign In to manage and view all items", linking to the favourites URL with `actionType=login`. Message and inquiry widgets show "Sign in to view message details" and "Sign in to view inquiry details". — [Alibaba header capture](https://activity.alibaba.com/onetouch/one-stop-service.html); [Alibaba.com home (fetched)](https://www.alibaba.com/)
- Lazada's account pages live on `my.lazada.co.id` (`/customer/order/index/`, `/wishlist/index`, `/customer/myReview/...`, `/customer/returns/...`) and auth lives on `member.lazada.co.id`. This domain split implies the account pages redirect to the auth host. — [Lazada.co.id home (fetched)](https://www.lazada.co.id/)

### Inferences
- Copyable pattern: an account-only route returns a redirect to `/masuk?next=<encoded path>` (validated as same-site to avoid open redirects), and the login page sends the user back after success. Add a `from_source` tag if funnel analytics are wanted. Inline "Masuk untuk melihat…" placeholders (Alibaba style) suit header widgets such as cart count and notifications.

### Gaps
- No direct observation of what Tokopedia, Shopee or Blibli show for a signed-out request to `/cart`, the order list or the account page (redirect, modal or empty state). This needs a live browser test.
- Whether the post-login return also restores the pending action (e.g. auto-adds the item that triggered login) is undocumented for all platforms.

## Q5. Mobile bottom tab bar patterns and what the "Saya/Akun" tab shows guests

### Takeaway
All major Indonesian apps use a bottom tab bar ending in an account tab ("Saya" on Shopee, "Akun" elsewhere). Shopee's "Saya" tab sits bottom-right, its video and live tab sits mid-bar, and notification settings live under Saya. The rest of the current 2025–2026 tab sets, and the guest view of the account tab, could not be confirmed from citable sources.

### Cited Findings
- Shopee's "Saya" tab is at the bottom-right of the home screen. Notification settings are reached via Saya → gear icon → Pengaturan Notifikasi. — [HalloGSM, Jun 2024](https://www.hallogsm.com/cara-menonaktifkan-notifikasi-shopee/); [Shopee ID Help: notifikasi](https://help.shopee.co.id/portal/4/article/72899-[Akun-Saya]-Bagaimana-cara-mengelola-notifikasi/pemberitahuan-saya-di-Aplikasi-Shopee)
- Shopee Video sits "pada bagian tengah bawah" (bottom-centre) of the home navigation. — [Inspirasi Shopee, 2022](https://shopee.co.id/inspirasi-shopee/shopee-video-fitur-terbaru-di-shopee-yang-wajib-kamu-coba/). A June 2025 Play Store review says the app auto-plays video from the "live & video menu", so a combined Live & Video tab still existed in 2025. — [Shopee Indonesia on Google Play](https://play.google.com/store/apps/details?hl=en_US&id=com.shopee.id)
- The Shopee app login button appears "di bagian atas atau bawah layar" (top or bottom of the screen). Login is by phone number with OTP or a Google account. — [Kompas Tekno, Aug 2024](https://tekno.kompas.com/read/2024/08/08/15350037/cara-login-akun-shopee-di-web-pakai-scan-praktis-tanpa-masukkan-e-mail)
- Across Bukalapak, Shopee, Tokopedia and Lazada, the bottom navigation stays in the thumb zone. Tokopedia and Lazada put the cart in the bottom bar. Shopee and Bukalapak keep the cart at the top. — [AKSA layout study](http://www.aksa.stsrdvisi.ac.id/index.php/aksa/article/download/37/35)
- A 2025 student UX-writing case study on Tokopedia added a visible home button to the Feed page and removed redundant back arrows because the bottom bar already gives that navigation. This is a concept, not Tokopedia's shipped design. — [Medium case study, Oct 2025](https://medium.com/@alexa.aman10/ux-writing-case-study-tokopedia-mobile-copy-improvement-5b0dbd041228)

### Inferences
- A common 4–5 tab model: Beranda (home), a discovery or content tab (Mall, Video, Feed), Notifikasi, sometimes Keranjang or Transaksi, and Saya/Akun last. For a cooperative shop, a 4-tab mobile bar (Beranda, Kategori or Katalog, Keranjang, Akun) fits the pattern. The Akun tab for guests should show a sign-in or register prompt plus public items (help, about, order tracking).

### Gaps
- Exact current (2025–2026) bottom-tab labels for Shopee, Tokopedia after integration, Lazada and Blibli were not found in a primary or dated source. App store screenshots could not be retrieved.
- What the Saya/Akun tab shows to a guest (login card, greyed menu, or full login screen) is undocumented in the sources found.

## Q6. Published UX guidance (Baymard, NN/g) on forced login, login walls and logo-to-home

### Takeaway
Baymard's current data: 18% of US shoppers who abandoned for a reason other than just browsing did so because "the site wanted me to create an account". 62% of benchmarked sites fail to make guest checkout the most prominent option. NN/g says forcing registration loses sales, recommends optional registration (a password field in checkout), and finds a logo placed top-left makes users 6 times more likely to reach home in one click than a centred one. NN/g also recommends a text "Home" link or breadcrumb in addition to the clickable logo.

### Cited Findings
- Baymard's average documented cart abandonment rate is 70.22%, from 50 studies. 42% of US shoppers have abandoned because "I was just browsing / not ready to buy". Excluding that group: 40% extra costs, 20% slow delivery, 19% didn't trust the site with card info, **18% "The site wanted me to create an account"**, 17% checkout too long, 17% errors. — [Baymard: Cart Abandonment Rate Statistics 2026](https://baymard.com/lists/cart-abandonment-rate). A search summary reported 19% for "did not want to create an account" in one version of Baymard's 2025 checkout article. Not verified; treat 18% as the current figure.
- 62% of sites don't make "Guest Checkout" the most prominent option. Test participants misread "New Customer" or "Returning Customer" as a forced account ("Why do I have to create an account?"). — [Baymard: Checkout UX 2025](https://baymard.com/blog/current-state-of-checkout-ux)
- 84% of sites don't delay account creation. Baymard recommends delaying it so users aren't forced to consider fields like "Create a Password" or "Birthdate" before ordering. — [Baymard: 5 Ways to Minimize Form Fields](https://baymard.com/blog/checkout-flow-average-form-fields)
- NN/g (Amy Schade): "Forcing registration causes lost sales." Sites that add guest checkout "immediately realize increased sales". Offering optional password fields inside checkout made users who had complained about forced registration "happily register". — [NN/g: Don't Force Users to Register Before They Can Buy](https://www.nngroup.com/articles/optional-registration/)
- NN/g (Hoa Loranger): logos should link to the homepage, a long-standing convention that is still "not understood by all users, especially less technical audiences". Users are "6 times more likely to successfully navigate to the homepage in one click when the logo is left-aligned than when it is centered". Offer both clickable logos and an actual "Home" link (global nav or breadcrumbs), placed top-left. — [NN/g: Homepage Links Remain a Necessity](https://www.nngroup.com/articles/homepage-links/)
- NN/g video: a site logo linking home "is not enough". A home link on every page helps new and lost visitors. — [NN/g video: Why You Need a Home Link](https://www.nngroup.com/videos/why-you-need-home-link/)

### Inferences
- For a cooperative whose members may be less technical, give the logo a top-left link to the root. Add "Beranda" in the mobile bottom bar and a breadcrumb rooted at "Beranda" on catalogue pages, as Tokopedia does with its breadcrumb rooted at "Tokopedia".
- If an account is mandatory, as for member pricing or credit, Baymard and NN/g suggest cutting the cost: let users browse and fill a cart before login, ask for login only at commit, return them to their place, and keep sign-up minimal (phone OTP).

### Gaps
- NN/g's "login walls" article could not be fetched, and no NN/g or Baymard figure specific to login walls on browsing (as opposed to checkout) was obtained.
- No Indonesia-specific published UX research (Baymard-style) on forced login was found. The Indonesian academic studies found are heuristic or EUCS comparisons of Shopee and Tokopedia (e.g. Shopee 84.0% vs Tokopedia 83.2% average UX satisfaction, n=74) with no login-specific data. — [Nilasari et al., IPSIKOM 2024](https://doi.org/10.58217/ipsikom.v12i2.305)
