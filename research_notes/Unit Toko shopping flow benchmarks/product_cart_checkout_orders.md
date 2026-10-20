# Purchase path UX: product page → keranjang → checkout → orders/tracking (Shopee, Tokopedia, Lazada, Amazon + Baymard/NN/g evidence)

Research date: 21 Oct 2026. Sources: official help centres (Shopee ID/SG/MY, Tokopedia Care, Lazada helpcenter, Amazon Customer Service, Amazon Business), live Tokopedia product pages, Baymard Institute public articles (updated 2024–2026), NN/g articles and report pages. Where only third-party or older sources exist, that is flagged inline.

---

## 1. Product detail page (PDP): layout, purchase controls, add-to-cart feedback

### Takeaway
Indonesian marketplaces put the full purchase control set in one place: on desktop, a sticky side panel (Tokopedia calls it "Atur jumlah dan catatan") with variant, quantity stepper, stock, minimum order, a live subtotal, and two CTAs. On mobile the same controls sit in a bottom sheet opened from a fixed bottom bar with two CTAs ("+ Keranjang"/"Masukkan Keranjang" and "Beli Langsung"/"Beli Sekarang"). NN/g evidence: add-to-cart feedback must persist and say exactly what was added (image, variant, quantity, price), with a badge on the cart icon.

### Cited Findings
**Tokopedia PDP (desktop web, live pages)**
- The purchase panel heading is "Atur jumlah dan catatan". It shows the selected variant ("Terpilih: KR02 PURPLE, BUBBLE"), a "jumlah" stepper, stock ("Stok: 255"), a minimum-order line ("Min. pembelian 3 pcs") and a live "Subtotal" (Rp20.400 = 3 × Rp6.800). Variant chips are grouped ("Pilih warna:", "Pilih ukuran:"), and the info area has tabs "Detail" / "Panduan". The Detail list includes "Etalase", "Min. Pemesanan: 3 Buah" and "Kondisi: Baru" — [Tokopedia product page (JNSSHOP KR02)](https://www.tokopedia.com/jnssby/keranjang-serbaguna-plastik-kr02-keranjang-plastik-storage-box-kr02-purple-bubble)
- Low stock appears as "Stok Total: Sisa 2". Other pages show "Detail"/"Spesifikasi" tabs — [Tokopedia product page (Mega Bloks)](https://www.tokopedia.com/duniakidzz/mega-bloks-9689-dragons-metal-ages-ramparts-victory)
- Pre-order products show "Waktu Preorder: 20 Hari" in the detail list, next to "Min. Pemesanan" — [Tokopedia product page (Lilygo)](https://www.tokopedia.com/nocturnalgoods/lilygo-t-rgb-esp32-s3-2-1-inch-round-display-st7701s-lcd)
- Official buying steps: read the description, make sure the right variant is chosen, optionally add a note for the seller ("catatan untuk penjual"), then click "+Keranjang" or "Beli Langsung" → "Cek Keranjang" → "Beli" → "Pilih Pembayaran" → choose method → "Bayar". You must pay before the deadline or the order is cancelled. Maximum 400 units of quantity per checkout, rolled out in stages — [Tokopedia Care: Cara Belanja](https://www.tokopedia.com/help/article/bagaimana-cara-belanja-di-tokopedia)
- After "Tambah Ke Keranjang", a confirmation appears. The user can close it with the "Silang" (X) at top right and keep shopping, or click "Lihat Keranjang". These are 2020-era screenshots; the newer article uses the label "Cek Keranjang" — [Tokopedia Care: Cara Bayar](https://www.tokopedia.com/help/article/cara-bayar-di-tokopedia)
- "Beli Langsung" is an express path for buying one product. Address, payment and shipping duration come from a saved preference profile; clicking "Bayar" goes straight to the "Thank You" page. The variant is chosen on the PDP; quantity and seller notes can be changed on the default purchase page. Preferences are managed from Akun → Beli Langsung — [Tokopedia Care: Beli Langsung](https://www.tokopedia.com/help/article/beli-langsung)

**Shopee PDP (mobile app primarily)**
- Express path: tap "Beli Sekarang" (bottom right of the product page) → choose variation if any → "Beli Sekarang" → "Opsi Pengiriman" → apply Voucher Shopee and Koin Shopee → "Metode Pembayaran" → "Buat Pesanan" — [Shopee Help: cara checkout](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee); position "di bagian bawah kanan" per [Tempo, Jan 2024](https://www.tempo.co/ekonomi/cara-belanja-di-shopee-yang-mudah-untuk-pemula-93953)
- Cart path: "Tambahkan Keranjang" → choose variation in a sheet → confirm "Masukkan Keranjang" → open the cart. You need an account and a saved shipping address before checkout — [Shopee Help: cara checkout](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee)
- Stock is shown under the price inside the add-to-cart pop-up. Sold-out colour/size chips cannot be tapped, and fully sold-out products carry a "Habis" label. This comes from an undated third-party guide; low confidence on current visuals — [Tomtekno](https://www.tomtekno.com/2021/01/cara-mengecek-stok-barang-atau-produk-di-shopee.html)
- Out-of-stock items cannot be added to the cart, and there is no back-in-stock notification. This is a user-research pain point in a 2023 case study — [Medium case study (Prasetya, 2023)](https://medium.com/@lingga.pras100/improving-the-user-experience-of-the-shopee-shopping-cart-a-ui-ux-case-study-b6f61563431f)
- Discount prices apply only up to a purchase limit or a limited discount stock. If the quantity exceeds the limit, the PDP shows a reminder that the discount will not apply — [Shopee Help: harga berubah di keranjang](https://help.shopee.co.id/4/article/72354-[Baru-di-Shopee]-Mengapa-harga-produk-berubah-setelah-saya-menambahkannya-ke-keranjang)
- Price negotiation from the PDP: "Chat Sekarang" → "Tawar" → enter a price → "Simpan" → make the offer. Once accepted, the buyer checks out from "Keranjang Saya". This only works if the seller enabled "Izinkan Penawaran Harga" — [Shopee Inspirasi: Cara Belanja](https://shopee.co.id/inspirasi-shopee/ingin-tahu-cara-belanja-di-shopee-yuk-ikuti-langkahnya/amp/)
- In Shopee Malaysia the express CTA is labelled "Beli Dengan Baucar" (buy with voucher), so the voucher is surfaced at the moment of commitment — [Shopee MY Help](https://help.shopee.com.my/portal/4/article/143630)

**Amazon**
- "Add to cart" adds items. The help page says to sign in so the cart is saved. "Buy Now" is an express option that still lets the buyer review and change payment method and delivery speed before placing the order — [Amazon: How to Place an Order](https://www.amazon.com/gp/help/customer/display.html?nodeId=TM0z2tvxdI4nu36ypt)
- Amazon Business PDPs and stores offer business pricing, "Quantity Discounts" on bulk buys, and "Request for Quantity Discounts"/custom quotes from several sellers. India shows an "excl. GST" price on the product page — [Amazon Business India: Key Features](https://business.amazon.in/en/key-features); [Amazon Business: guide to buying](https://business.amazon.com/en/blog/guide-buying-amazon-business)

**Evidence on add-to-cart feedback and PDP purchase controls**
- NN/g: confirm add-to-cart with (a) a number badge on the cart icon, (b) a persistent overlay, a top banner, or an interstitial page with "View cart / Checkout / Continue shopping", and (c) a changed Add to Cart button. Avoid overlays that fade on their own: participants raced to check size and colour and could not. The confirmation should show a correctly-optioned product image, name, price, quantity added and selected options — [NN/g: Cart Feedback (2018)](https://www.nngroup.com/articles/cart-feedback/)
- NN/g: many participants added the same item twice. Recommended fix: a salient "already in cart" message next to the button, with the label changed to "Add Another". Interstitial pages suit sessions where users buy only 1–2 items; overlays and banners suit multi-item sessions — [NN/g: Cart Feedback](https://www.nngroup.com/articles/cart-feedback/)
- Baymard (grocery): dynamically turn the "Add to Cart" button into a quantity selector after the item is added. Baymard found that with static buttons users "lose track of what they had already added" — [Baymard: Grocery add-to-cart buttons](https://baymard.com/blog/grocery-add-to-cart-buttons); [Baymard: Grocery UX 2026](https://baymard.com/blog/online-grocery-ecommerce-ux-2026)
- Baymard: 97% of benchmarked sites fail to "use buttons or buttons plus an open text field for updating cart quantity", i.e. a stepper (−/+) plus a typeable field — [Baymard: Checkout UX 2025](https://baymard.com/blog/current-state-of-checkout-ux)
- Baymard on delivery messaging, which applies equally to the PDP shipping estimate: use a "delivery date" instead of a "delivery speed" (48% of sites don't), and show the order cutoff as a countdown (83% don't). Participants stopped to do calendar and time-zone arithmetic with "4–7 business days" text — [Baymard: Checkout UX 2025](https://baymard.com/blog/current-state-of-checkout-ux)
- NN/g Product Pages report: 108 guidelines; the cart/checkout report covers "Add-to-cart feedback" and "Minicart as feedback, not cart replacement" — [NN/g Ecommerce UX reports](https://www.nngroup.com/reports/ecommerce-user-experience/); [NN/g Vol. 04](https://www.nngroup.com/reports/ecommerce-ux-shopping-carts-checkout-registration/)

### Inferences
- Canonical Indonesian PDP blocks, top to bottom on mobile: gallery carousel → price block (strike-through and discount where relevant) → title, rating, sold count → voucher/promo row → shipping row → variant summary → store card (name, location, chat) → tabs/accordions (Detail, Spesifikasi, Panduan/Info Penting) → reviews → recommendations. A fixed bottom bar holds chat, add-to-cart and the buy-now CTA; either CTA opens a bottom sheet with variant chips, stepper, stock and minimum order. Desktop moves that sheet into a sticky right-hand "Atur jumlah dan catatan" card with a live subtotal. Element names are confirmed above; the exact vertical order is from common observation of the apps, not a cited source.
- For a B2B shop serving bank branches, the Tokopedia pattern maps directly:
  - Start the stepper at the MOQ and print "Min. pembelian N <unit>" under it.
  - Show stock as a number or "Sisa N" when low.
  - Compute the subtotal live in the panel.
  - Put the seller-note field ("catatan") on the PDP or in the cart.
  - Show packages/bundles as variant chips.
  - For a public catalogue that hides prices (a current owner decision in this project), replace the price block and subtotal with a sign-in prompt, but keep stock and MOQ visible.
- An "express" path (Beli Langsung/Beli Sekarang) is low value where most orders are multi-item replenishment. A single primary "Tambah ke Keranjang" plus an in-cart state ("Sudah di keranjang · Tambah lagi", or turning the button into a stepper) follows NN/g and Baymard guidance more closely.

### Gaps
- Exact current Shopee and Lazada desktop PDP layouts, and Shopee's shipping-estimate wording (e.g. "Garansi Tepat Waktu", "Estimasi tiba"), could not be verified from an official or dated source this session.
- Amazon buy-box specifics (delivery date with "order within" countdown, "In Stock", quantity dropdown, "Ships from / Sold by") are widely observed but were not confirmed against a source here.
- The wording and duration of Shopee's and Tokopedia's current add-to-cart toast or bottom sheet (e.g. "Berhasil ditambahkan") are unverified. Tokopedia evidence is from 2020 screenshots.

---

## 2. Cart (keranjang): grouping, selection, editing, saving, summary, persistence

### Takeaway
Indonesian carts are multi-seller holding areas. Items are grouped by store, each item and store has a checkbox, the buyer checks out only the ticked subset, vouchers apply per store and per platform, and the cart shows dynamic warnings (price changed, out of stock, purchase limit) with invalid items pushed to a separate list at the bottom. NN/g and Baymard back the same ideas: a full dedicated cart page, clear Remove, steppers without an "Update" button, cross-device persistence, and saving items for later.

### Cited Findings
- Shopee: open the cart (top right), tick the checklist for the products to check out, check variants and notes, apply vouchers and coins, then "Checkout" → "Metode Pembayaran" → "Buat Pesanan" — [Tempo, Jan 2024](https://www.tempo.co/ekonomi/cara-belanja-di-shopee-yang-mudah-untuk-pemula-93953); [Shopee Help](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee)
- Shopee vouchers per checkout: at most 1 Voucher Toko, 1 Voucher Gratis Ongkir and 1 Voucher Shopee. A shop voucher auto-applies once the selected items qualify. If they don't, the shop-voucher row shows the extra amount needed. Users can swap Shopee vouchers one at a time to compare discounts. The voucher icon sits on the "Keranjang Saya" page — [Shopee Help: Voucher saat checkout](https://help.shopee.co.id/portal/4/article/73183-[Voucher]-Bagaimana-cara-menggunakan-Voucher-saat-checkout%3F)
- Shopee cart states:
  - Prices can change in the cart if the edited quantity exceeds a purchase limit or the discounted stock runs out; a reminder is shown.
  - Sold-out items move to a list of invalid products at the bottom of the cart.
  - With several variations of one product, whether each variation is selected affects which price it shows.
  - [Shopee Help: harga berubah](https://help.shopee.co.id/4/article/72354-[Baru-di-Shopee]-Mengapa-harga-produk-berubah-setelah-saya-menambahkannya-ke-keranjang)
- Shopee checkout size limit, conflicting: 40 products per checkout ([Shopee ID Help](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee); [Shopee SG Help](https://help.shopee.sg/portal/4/article/76785)) vs 50 products per payment ([Shopee Inspirasi blog](https://shopee.co.id/inspirasi-shopee/ingin-tahu-cara-belanja-di-shopee-yuk-ikuti-langkahnya/amp/); [Tempo 2024](https://www.tempo.co/ekonomi/cara-belanja-di-shopee-yang-mudah-untuk-pemula-93953)). The help centre (40) is the more authoritative source.
- Shopee cart capacity is reportedly 200 in-stock items, and items deleted by the seller disappear from the cart. This is a third-party 2022 source and unverified — [PintarJualan](https://pintarjualan.id/cara-menghapus-keranjang-di-shopee/)
- Shopee: products cannot be added to an order after payment; the buyer must create a new order — [Shopee Help](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee)
- Tokopedia: items from different shops can go into one combined payment ("Menggabungkan Pembayaran Sekaligus"): add several items → "Lihat Keranjang" → check products and quantities → "Beli" — [Tokopedia Care: Cara Bayar](https://www.tokopedia.com/help/article/cara-bayar-di-tokopedia). Cap is 400 units per checkout — [Tokopedia Care: Cara Belanja](https://www.tokopedia.com/help/article/bagaimana-cara-belanja-di-tokopedia)
- Generic Shopee cart behaviour (per-item checkbox, +/− stepper or typed quantity, Remove with a confirmation, "Save for Later"/"Move to Wishlist", promotions shown in cart) is described by a low-quality aggregator page about Shopee Malaysia. Treat it as indicative only — [thecable aggregator, Dec 2025](https://cp.thecable.ng/quick-brains/shopee-cart-manage-your-shopping-on-shopee-malaysia-1764797809)
- Amazon: open the cart via the Cart icon, change quantities and add/remove items as often as needed, then "Proceed to Checkout". Signing in saves the cart — [Amazon: How to Place an Order](https://www.amazon.com/gp/help/customer/display.html?nodeId=TM0z2tvxdI4nu36ypt)
- NN/g:
  - Many shoppers use the cart as a holding area or "dressing room" for final decisions.
  - Provide a dedicated full cart page, not only a minicart.
  - Show a clear image, name, selected attributes and price.
  - Make removal easy ("Remove" is clearer to users than "Delete") and allow setting quantity to 0.
  - [NN/g: Decision Making in the Shopping Cart (2014)](https://www.nngroup.com/articles/shopping-cart/)
- NN/g mobile:
  - Tapping the cart icon should go straight to the full cart, not a mini-cart.
  - Show a clear Remove button per item.
  - Don't require an "Update" button to commit quantity changes; users forget it.
  - Make the cart resumable on any device once logged in; consider labels showing when and on which device an item was added.
  - Crate & Barrel's empty-cart page tells users that signing in gives access to the cart from all devices.
  - [NN/g: Mobile Checkout (2018)](https://www.nngroup.com/articles/mobile-checkout-ux/)
- Baymard: cart contents should persist across sessions, and changes to cart items should be communicated clearly — [Baymard: Audit checkout for hidden friction](https://baymard.com/blog/audit-checkout-flow-hidden-friction). In 2026 grocery testing, a participant could not save a cart item for later (guideline #622) — [Baymard: Grocery UX 2026](https://baymard.com/blog/online-grocery-ecommerce-ux-2026)
- Baymard: 70% of sites show a coupon/promo field, and some participants left checkout to search for codes. Recommendation: collapse the coupon field behind a link (35% of sites don't) — [Baymard: Minimize Form Fields (2024)](https://baymard.com/blog/checkout-flow-average-form-fields)
- Baymard: 12% of shoppers abandoned because they "couldn't see / calculate total order cost up-front" — [Baymard: Cart Abandonment Stats](https://baymard.com/lists/cart-abandonment-rate)
- Amazon Business: once a cart is submitted for approval, its prices and quantity availability are held for 7 days — [Amazon Business: Buying tools (Jun 2025)](https://business.amazon.com/en/blog/purchase-controls)

### Inferences
- Recommended B2B cart anatomy, adapting the marketplace pattern:
  - Group lines by fulfilment source/vendor (or just one group if the shop is single-vendor).
  - Header row: "Pilih Semua (n)", plus a bulk "Hapus" action for selected lines.
  - Each line: checkbox, thumbnail, name, variant/package, unit price, −/+ stepper with a typeable field that enforces MOQ and stock, line subtotal, "Hapus", "Simpan untuk nanti".
  - Inline warnings: "Stok tersisa 3", "Harga berubah", "Minimal pembelian 10".
  - Unavailable items in a separate "Tidak dapat diproses" section at the bottom.
  - A sticky summary showing selected-item count, subtotal and estimated shipping, with the primary "Checkout (n)" button.
- Because bank-branch users authenticate, the cart should be server-side and account-bound (same cart on desktop and phone, per NN/g). The empty state should offer "Beli lagi" from recent orders and links to the catalogue, rather than just "keranjang kosong".
- Voucher stacking rules are consumer-marketplace specific. A B2B shop would more likely show contract/price-list pricing and quantity-tier pricing (Amazon Business "Quantity Discounts") than vouchers.

### Gaps
- No official Tokopedia or Lazada source this session confirmed current cart features: shop-level checkbox, "Pilih Semua", move-to-wishlist, a "saved for later" section, or cart item caps.
- No source documented Indonesian marketplaces' empty-cart screens or cross-device cart behaviour (assumed account-bound, since login is required to shop).
- Baymard's specific "save for later" and persistent-cart guideline text is behind its Premium paywall.

---

## 3. Checkout: address, delivery, notes, voucher, payment, summary, final step, steps and errors

### Takeaway
Shopee and Tokopedia use a single-page checkout: pre-filled default address, delivery option per store, notes, vouchers, payment method, and a sticky total with "Buat Pesanan" (Shopee) or "Pilih Pembayaran" → "Bayar" (Tokopedia). After that come a payment-instructions/deadline screen and a "Belum Bayar"/"Menunggu Pembayaran" state. Amazon uses address → payment → shipping → review → "Place your order". Amazon Business adds PO/cost-centre fields and swaps the CTA for "Submit order for approval". Baymard: the average checkout has 5.1 steps and 11.3 fields, but field count matters more than step count.

### Cited Findings
**Indonesian marketplaces**
- Shopee checkout sequence: (default address) → "Opsi Pengiriman" → Voucher Shopee and Koin Shopee → "Metode Pembayaran" → "Buat Pesanan". Requires an account with a shipping address — [Shopee Help: cara checkout](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee)
- Shopee: the buyer can leave a note for the seller in a "Tinggalkan pesan" field at checkout (e.g. "please add bubble wrap"). A successful payment moves the order to "Dikemas" — [Ginee guide](https://ginee.com/id/insights/tips-membeli-shopee/)
- Shopee COD has no minimum, a maximum of Rp3,000,000, and requires the address to be inside the courier's service area. This is Katadata citing the Shopee help centre, via a search summary — [Katadata](https://katadata.co.id/intan/digital/6195c7b43cc43/cara-belanja-di-shopee-dengan-mudah-dan-praktis/)
- Reasons Shopee will not place an order at checkout (error states): account limited; no shipping options available; payment method failed; courier weight/dimension limits exceeded for bulky multi-item orders; more than 40 products; seller per-buyer quantity limits — [Shopee SG Help](https://help.shopee.sg/portal/4/article/76785)
- Tokopedia checkout: on the "Checkout" page re-check the recipient address, choose a shipping service, click "Pilih Pembayaran", choose a method, then "Bayar". Payment must happen before the deadline shown or the order is cancelled — [Tokopedia Care: Cara Bayar](https://www.tokopedia.com/help/article/cara-bayar-di-tokopedia); [Tokopedia Care: Cara Belanja](https://www.tokopedia.com/help/article/bagaimana-cara-belanja-di-tokopedia)
- Tokopedia: instant-payment verification can take up to 1×24 working hours. Buyers check the "Menunggu Pembayaran" and "Menunggu Konfirmasi" menus in Daftar Transaksi Belanja and can cancel with "Batalkan Transaksi" — [Tokopedia Care: Instant Payment](https://www.tokopedia.com/help/article/pembayaran-pesanan-via-instant-payment-belum-diverifikasi)
- Lazada gives a maximum of 24 hours to pay; unpaid orders auto-cancel. This is a third-party source (2024) — [Helokepo](https://www.helokepo.com/marketplace/lazada/cara-membatalkan-pesanan-di-lazada/)

**Amazon / Amazon Business**
- Amazon checkout: Cart → "Proceed to Checkout" → select or add a delivery address → select a payment method (gift cards, promo codes, vouchers, points go here) → select a shipping option → review → "Place your order". A passkey may be requested for high-risk payments — [Amazon: How to Place an Order](https://www.amazon.com/gp/help/customer/display.html?nodeId=TM0z2tvxdI4nu36ypt)
- Amazon Business checkout:
  - Select a group to buy on behalf of; ship cart items to every address in that group.
  - "Business order information" fields: PO number, GL code, cost center, location, department, comments for the approver.
  - Individual or shared payment methods.
  - For orders needing approval, "Submit order for approval" replaces "Place your order".
  - Leaving checkout and returning within 15 minutes resumes from the last completed step.
  - The review page allows changing address and payment and verifying the PO number.
  - Placing an order shows a confirmation page plus a confirmation email and later status updates.
  - [Amazon: Check out with Amazon Business](https://www.amazon.com/gp/help/customer/display.html?nodeId=201633090)
- Amazon Business approvals:
  - Can be required for all orders or above a per-order spending limit.
  - The requisitioner can add a comment for the approver and add an extra approver (e.g. IT).
  - Approvers approve or reject from email, can edit orders and can delegate.
  - Requests must be approved within 7 days or they cancel; the buyer can resubmit.
  - [Amazon Business Approvals guide (2015 PDF)](https://images-na.ssl-images-amazon.com/images/G/01/BISS/B2B/pdfs/AmazonBusinessAccounts_Approvals._V288121526_.pdf); [Amazon Business buying tools (2025)](https://business.amazon.com/en/blog/purchase-controls)

**Evidence (Baymard, NN/g)**
- The average checkout in 2024 has 5.1 steps (cart through order review, new user) and 11.3 form fields. Step count has been "largely unchanged" since 2012; most checkouts have 5–6 steps; 1–2-step checkouts barely exist. Baymard: the number of fields matters more than the number of steps — [Baymard: Minimize Form Fields (Jun 2024)](https://baymard.com/blog/checkout-flow-average-form-fields)
- An ideal checkout can be 12–14 form elements (7–8 fields), versus a benchmark average of 23.48 elements (14.88 fields), so a 20–60% reduction is possible. These are older benchmark figures and conflict with the 2024 average of 11.3 fields — [Baymard: Cart Abandonment Stats](https://baymard.com/lists/cart-abandonment-rate)
- Baymard's 5 field-reduction tactics (4 recovered; the first heading was not retrieved):
  - Hide "Address Line 2" behind a link: 75% of sites don't, and 30% of participants stalled at it.
  - Hide the coupon code field: 35% don't.
  - Hide billing address by default ("same as shipping"): 24% of sites show separate billing by default.
  - Delay account creation: 84% don't.
  - [Baymard](https://baymard.com/blog/checkout-flow-average-form-fields)
- Baymard's 10 checkout best practices (2025 benchmark), with the share of sites that fail each:
  1. Guest checkout as the most prominent option (62%)
  2. Avoid complex password rules (65%)
  3. Delivery date instead of speed (48%)
  4. Cutoff time as a countdown (83%)
  5. Buttons, or buttons plus a text field, for cart quantity (97%)
  6. All fulfilment options in the shipping selector (52%)
  7. Mark both required and optional fields (61%)
  8. Right interface type for optional inputs (32%)
  9. Adaptive error messages matching the specific sub-issue (94%)
  10. Explain why the phone number is required (49%)

  64% of desktop and 63% of mobile sites score "mediocre" or worse; apps score better but 46% are still mediocre or worse — [Baymard: Checkout UX 2025](https://baymard.com/blog/current-state-of-checkout-ux)
- NN/g mobile: unexpected cost increases during checkout cause abandonment. Place the order summary (subtotal, taxes, fees, discounts, shipping) prominently at the top of mobile checkout pages, not collapsed at the bottom — [NN/g: Mobile Checkout](https://www.nngroup.com/articles/mobile-checkout-ux/)
- NN/g: guest checkout is a long-standing guideline because first-time buyers are not yet committed enough to register. In B2B testing, professionals "rebel" when asked for information too early — [NN/g: Workflow Expectations](https://www.nngroup.com/articles/workflow-expectations/)
- NN/g's cart/checkout report has 137 recommendations from studies of 350+ sites in the US, UK, Denmark, India and China. Topics include "No surprises", "Easy information entry", "Sufficient payment options", "Completing the order: summary and confirmation" — [NN/g Report Vol. 04](https://www.nngroup.com/reports/ecommerce-ux-shopping-carts-checkout-registration/)

### Inferences
- Recommended B2B checkout for branches: one page (as Shopee/Tokopedia) with sections in this order:
  1. Alamat pengiriman: branch address pre-selected from the user's profile, with "Ubah" opening an address-book sheet.
  2. Items grouped by shipment, each with a delivery option shown as a date ("Tiba 24–26 Okt"), plus "Catatan untuk penjual".
  3. Order information: PO number, cost centre, budget code. Show these only if the institution requires them, mark required vs optional (Baymard #7), and explain why each is needed (Baymard #10).
  4. Payment method: typically invoice/transfer/virtual account for B2B.
  5. Sticky order summary with total and the primary CTA: "Buat Pesanan", or "Ajukan Persetujuan" when an approval rule applies (Amazon Business pattern).
- That is effectively 2 screens after the cart (checkout → confirmation/payment instructions), below Baymard's 5.1-step average. Field count is near zero for repeat users because address, payer and cost centre come from the account.
- Guest checkout does not apply to an authenticated institutional shop. The relevant Baymard lesson is "don't force account work mid-checkout": provision accounts and branch addresses via admin or SSO beforehand.
- Error handling worth copying from Shopee's failure reasons: block "Buat Pesanan" with a specific inline message for no shipping option to the address, items over weight or volume limit, quantity above stock or per-order limit, and payment failure. Use adaptive messages (Baymard #9), not a generic "Gagal membuat pesanan".

### Gaps
- No official, dated source described Shopee's or Tokopedia's current checkout page layout in detail: exact section order, how the address-change sheet looks, whether shipping is chosen per store, or the sticky bar contents. The order above comes from help-article step order.
- No reliable source on the Shopee/Tokopedia payment deadline duration (commonly observed as about 24 hours for virtual account) beyond "pay before the deadline shown". Lazada's 24 hours is third-party only.
- No public numeric study on progress indicators in checkout was found (see §5).

---

## 4. After the order: confirmation, order list tabs, detail and tracking, reorder, invoice, cancel/return

### Takeaway
- **Shopee**: status tabs Belum Bayar → Dikemas → Dikirim → Selesai, plus Dibatalkan and Pengembalian, with explicit rules for instant vs seller-approved cancellation and return windows.
- **Tokopedia**: one filterable "Daftar Transaksi" (Dalam Proses / Berhasil / Tidak Berhasil, date and category filters, search, 5-year history), with a detailed status on each card and courier milestones with pickup/delivery photos.
- **Amazon**: order cards with "Track package / Return or replace items / invoice / Buy it again" and plain delivery statuses.
- **Baymard**: order tracking is the most-wanted account feature, and in-site tracking must show 6 elements.

### Cited Findings
**Shopee (buyer)**
- "Pesanan Saya" (tab "Saya") groups orders as:
  - Belum Bayar: not yet paid.
  - Dikemas: paid, not yet shipped by the seller.
  - Dikirim: shipped by the seller.
  - Selesai: delivered.
  - Dibatalkan: cancelled.
  - Pengembalian: refund/return requested.

  Orders shipped with Shopee-supported couriers have a tracking number trackable in-app; otherwise the buyer contacts the seller — [Shopee Help: status pesanan](https://help.shopee.co.id/portal/4/article/72518-[Pelacakan-Pesanan]-Di-mana-saya-melihat-status-pesanan)
- Seller-side statuses mirror these: Belum Bayar, Perlu Dikirim, Dikirim, Selesai (received and confirmed by the buyer), Pengembalian/Pembatalan, and Pengiriman Gagal (courier could not deliver, e.g. COD refused or wrong address) — [Shopee Seller Edu: Menyelesaikan Pesanan Pertama](https://seller.shopee.co.id/edu/article/11968/Menyelesaikan-Pesanan-Pertama-Anda)
- Shopee cancellation types:
  - Instant: the buyer taps "Batalkan Pesanan" before the seller arranges shipping or enters a tracking number. For instant/same-day couriers this is limited to 1 hour after payment verification; for regular couriers it is allowed any time before shipping is arranged.
  - Non-instant: the seller has 1×24 h to respond, then the order auto-cancels.
  - Automatic: the seller misses the packing window ("Masa Pengemasan"), the tracking number is not verified within 2 days after it, or the seller hasn't logged in for 7 days.
  - [Shopee Help: jenis pembatalan](https://help.shopee.co.id/portal/4/article/87564-[Pembatalan-Pesanan]-Apa-saja-jenis-pembatalan-pesanan-di-Shopee)
- A cancellation request can be submitted only once. After shipment, cancel/return actions sit under the Dikirim tab ("Ajukan Pengembalian") and require seller approval. These are news summaries via search, medium confidence — [Bisnis.com](https://finansial.bisnis.com/read/20230214/563/1627795/begini-cara-membatalkan-pesanan-di-shopee-yang-sudah-dikirim); [iNews](https://www.inews.id/finance/bisnis/cara-membatalkan-pesanan-di-shopee-yang-sudah-dikirim)
- Return/refund windows:
  - 2 days after arrival for "toko lainnya".
  - 7 days after "Pesanan Tiba" for non-Mall stores.
  - 15 days for Shopee Mall.

  An electronic proof of delivery (ePOD) is shown automatically when the buyer reports "Semua pesanan tidak sampai" — [Shopee Help: belum menerima produk](https://help.shopee.co.id/portal/4/article/73145-[Pengembalian-Barang%2FDana]-Apa-yang-harus-dilakukan-jika-saya-belum-menerima-produk-namun-status-pesanan-telah-diterima?previousPage=secondary+category)
- Refunds for cancelled orders depend on the payment method. Buyers are notified in-app and by email and can see refund status under Notifikasi → Status Pesanan — [Shopee Help: refund pembatalan](https://help.shopee.co.id/portal/4/article/73208-[Pembatalan-Pesanan]-Bagaimana-proses-pengembalian-dana-untuk-pesanan-yang-dibatalkan%3F)

**Tokopedia (buyer)**
- "Daftar Transaksi" is reached from the Akun menu, with shortcuts "Dalam Proses" and "Semua Transaksi". It covers 5 years of history.
  - Filters: date (30 days, 90 days, custom), category (Belanja, Top-up & Tagihan, Travel & Entertainment, …), status (Dalam Proses, Berhasil, Tidak Berhasil, E-tiket & E-voucher Aktif).
  - Reset with "x"; a search bar sits at the top.
  - The detailed status shows at the top right of each order card.
  - [Tokopedia Care: Daftar Transaksi](https://www.tokopedia.com/help/article/cara-melihat-daftar-pesanan)
- Courier-tracking milestones for Kurir Rekomendasi:
  - Menunggu Konfirmasi: waiting for the seller.
  - Pesanan Diproses: seller confirmed and packing.
  - Sedang Dikirim: courier picked up; pickup photo shown.
  - Sampai Tujuan: arrived; delivery photo shown.

  Proof photos before and after shipping are a stated benefit. This is the help page's FAQ, partly via search summary — [Tokopedia Care: Kurir Rekomendasi](https://www.tokopedia.com/help/article/faq-seputar-kurir-rekomendasi)
- Complaints about damaged or wrong items must be filed within 2×24 hours after the status shows arrived. Buyers should check the order hasn't been marked "Selesai" — [Tokopedia Care: pesanan belum sampai](https://www.tokopedia.com/help/article/pesanan-saya-belum-sampai) (via search summary)
- If the buyer doesn't confirm receipt within 1×24 h, the system treats the transaction as complete and pays the seller. This comes from a 2024 academic case paper; low confidence on the current timer — [JPTAM 2024](https://jptam.org/index.php/jptam/article/download/12517/9615/22959)
- Beli Langsung payment lands on a "Thank You" page — [Tokopedia Care](https://www.tokopedia.com/help/article/beli-langsung)

**Lazada (buyer)**
- Akun → Pesanan Saya → "Lihat Semua". Orders can be filtered as paid, in delivery or completed. A "Sedang Dikirim" order shows a tracking number and latest status. Once delivered it changes to "Telah Diterima", with the time received and the recipient's name. This is Kumparan 2023 citing lazada.co.id — [Kumparan](https://kumparan.com/tips-dan-trik/cara-melihat-pesanan-di-lazada-dan-status-ordernya-20pMkjj7Wem)
- Cancel from the order detail with "Batal"/"Batalkan Pesanan" → choose a reason → submit. Once the parcel is with the courier, only a return is possible. Orders that used a free-shipping voucher with several items cannot be partially cancelled. Third-party sources — [Helokepo 2024](https://www.helokepo.com/marketplace/lazada/cara-membatalkan-pesanan-di-lazada/); [Ginee](https://ginee.com/id/insights/cara-membatalkan-pesanan-lazada-yang-tidak-bisa-dibatalkan/)
- Official refund policy (updated Jul 2026):
  - Orders cancelled while in "dikemas" status are refunded within 24 h of the request.
  - Lazada/seller processing takes up to 10 working days.
  - Refunds to Lazada Saldo take 1×24 h.
  - Cards, e-wallets and PayLater take an extra 5–10 working days.
  - Disputes must be raised within 5 working days.
  - [Lazada Helpcenter: Kebijakan Pengembalian Dana](https://helpcenter.lazada.co.id/s/faq/knowledge?country=ALL&language=th_TH&questionId=k0200000000000000000000000000a55)

**Amazon**
- Your Account → "Your Orders: Track, return, cancel an order, download invoice or buy again" — [Amazon: Your Account](https://www.amazon.com/your-account)
- Tracking: Your Orders → "Track package" → "All updates" for delivery updates and tracking numbers.
  - Multi-item orders can have separate delivery dates and tracking.
  - Status labels: "Arriving [date]", "Out for delivery", "Delivered", "Undeliverable" (auto-refund on return).
  - Most late parcels arrive within 48 h of the estimate.
  - Shipped orders can't be cancelled.
  - [Amazon: Tracking your package](https://www.amazon.com/gp/help/customer/display.html?nodeId=TWTXojCfgLWBBdZTAG)
- Returns start from Your Orders → "Return or Replace Items" — [Amazon: Returns](https://www.amazon.com/gp/help/customer/display.html?nodeId=G4KFSMKGWZPYNW9X). Right after ordering, "Returns and Orders" shows the new order with options to edit and track — [Amazon: How to Place an Order](https://www.amazon.com/gp/help/customer/display.html?nodeId=TM0z2tvxdI4nu36ypt)
- Selecting an in-transit order opens a progress-tracker page; a delivered order opens order details. The order detail offers track package, invoice, return, get help and buy again — [Amazon Buy with Prime blog (2023)](https://buywithprime.amazon.com/blog/all-of-your-prime-orders-all-in-one-place)
- "Returns & Orders" lists every item ever ordered, each with a "Buy it again" button and receipt/track/return options — [Business Insider](https://www.businessinsider.com/reference/how-to-reorder-items-on-amazon)
- Amazon Business: after an approved order the buyer gets a confirmation email and the order routes to fulfilment. Unapproved requests expire after 7 days — [Amazon Business buying tools](https://business.amazon.com/en/blog/purchase-controls). Analytics can filter on 75+ fields including PO number, order status and invoice status — [Amazon Business guide](https://business.amazon.com/en/blog/guide-buying-amazon-business)

**Evidence on order tracking**
- Baymard (updated May 2026): 50% of respondents said order tracking is the most important account feature, yet 67% of test sites failed to consistently provide all key tracking details. 25% of test sites failed to reliably give an expected delivery date, which pushed users to carrier sites or to guessing. Sending users to third-party tracking sites often fails their information and navigation needs — [Baymard: 6 Key Order-Tracking Details](https://baymard.com/research-articles/integrate-tracking-info)
- Baymard's 6 order-tracking details: (1) expected delivery date, (2) order status progress bar, (3) carrier name, (4) linked tracking number, (5) detailed shipping history, (6) package contents summary — [Baymard](https://baymard.com/research-articles/integrate-tracking-info)
- Vendor claim: "where is my order" contacts are 30–40% of support volume. This is vendor marketing, unverified — [LateShipment blog](https://www.lateshipment.com/blog/where-is-my-order-cx/)

### Inferences
- Status tabs for a bank-branch B2B shop, using Shopee's tab model plus Amazon Business approvals:
  - Menunggu Persetujuan (only if approvals are enabled)
  - Menunggu Pembayaran / Belum Bayar (only if pre-payment applies)
  - Diproses / Dikemas
  - Dikirim
  - Selesai, after receipt is confirmed by the branch or auto-confirmed after N days
  - Dibatalkan
  - Pengembalian

  A Tokopedia-style filter bar (date range, status, search by order number/PO) fits a long institutional history better than tabs alone; consider both (tabs plus search/date filter).
- Order card: order number, date, branch, item thumbnails, count, total, status badge, and the next action ("Lacak", "Konfirmasi Diterima", "Beli Lagi", "Unduh Invoice", "Ajukan Pengembalian").
- Order detail page should carry Baymard's 6 tracking elements in-site:
  - status stepper: Dibuat → Disetujui → Diproses → Dikirim → Diterima
  - expected delivery date
  - courier name
  - linked tracking number (resi)
  - dated shipment history
  - package contents
  - photo proof of delivery / ePOD, as Tokopedia and Shopee do
- Cancellation rules by state follow Shopee: self-service instant cancel before the vendor processes the order, a request with an SLA after processing starts, no cancel once shipped (return path instead), each with a clear explanation on the order page.
- Confirmation screen after "Buat Pesanan": order number, status, expected delivery date, next steps (approval pending or payment instructions with deadline), "Lihat Pesanan" and "Lanjut Belanja", plus an email. This matches Amazon's confirmation page plus email and Tokopedia's Thank You page.

### Gaps
- Shopee's and Tokopedia's current "Beli Lagi", "Pesanan Diterima"/"Selesaikan Pesanan" and invoice ("Lihat Invoice"/"Unduh Invoice") buttons were not confirmed by an official 2025–26 source this session, though widely present in the apps.
- Shopee's auto-complete timer after delivery and Tokopedia's current auto-confirm timer were not verified (Tokopedia's 1×24 h is from an academic paper only).
- Lazada's current buyer tab names (e.g. "Belum Bayar / Dikemas / Dikirim / Beri Ulasan / Pengembalian & Pembatalan", or "To Pay / To Ship / To Receive / To Review" in English markets) could not be verified from an official source.
- No source described Shopee's post-"Buat Pesanan" confirmation or payment-instruction screen in detail.

---

## 5. Evidence base: field counts, guest checkout, progress indicators, abandonment, tracking expectations

### Takeaway
- Average documented cart abandonment is about 70%. Excluding "just browsing", the top fixable causes are extra costs (40%), slow delivery (20%), trust in card handling (19%), forced account creation (18%), long or complicated checkout (17%) and site errors (17%).
- The average checkout has 5.1 steps and 11.3 fields, but fields matter more than steps.
- Order tracking is the most important account feature for half of shoppers.
- Strong public numeric evidence on checkout progress indicators was not found.

### Cited Findings
- Average documented abandonment rate is 70.22%, the mean of 50 studies (last updated 22 Sep 2025) — [Baymard: Cart Abandonment Stats](https://baymard.com/lists/cart-abandonment-rate)
- 42% of US shoppers have abandoned because they were "just browsing / not ready to buy". Excluding that group, reasons were:
  - Extra costs too high: 40%
  - Delivery too slow: 20%
  - Didn't trust the site with card info: 19%
  - Site wanted me to create an account: 18%
  - Too long / complicated checkout: 17%
  - Site errors/crashes: 17%
  - Returns policy: 13%
  - Couldn't see total cost up-front: 12%
  - Card declined: 10%
  - Not enough payment methods: 9%

  [Baymard](https://baymard.com/lists/cart-abandonment-rate)
- Better checkout design can raise conversion by 35.26% at the average large e-commerce site, which Baymard puts at $260B of recoverable US+EU orders. The average site has 39 potential checkout improvements ([abandonment page](https://baymard.com/lists/cart-abandonment-rate)) or 32 ([research page](https://baymard.com/research/checkout-usability)), so the two pages differ. In the 344-site benchmark, 65% of sites are "mediocre" or worse and only 2% are "good" — [Baymard: Checkout Usability](https://baymard.com/research/checkout-usability)
- Field and step counts: 5.1 steps and 11.3 fields (2024); ideal 7–8 fields (12–14 elements); older average 14.88 fields (23.48 elements). 17% abandon over complexity — [Baymard 2024](https://baymard.com/blog/checkout-flow-average-form-fields); [Baymard stats](https://baymard.com/lists/cart-abandonment-rate)
- Guest checkout: 62% of sites don't make guest checkout the most prominent option. Participants at Room & Board thought they had to create an account and missed "Checkout as Guest". 84% of sites don't delay account creation, and 65% impose overly complex password rules — [Baymard: Checkout UX 2025](https://baymard.com/blog/current-state-of-checkout-ux); [Baymard 2024](https://baymard.com/blog/checkout-flow-average-form-fields)
- Progress indicators: NN/g recommends a progress bar or a wizard that walks users through the steps for multi-step processes, as opposed to skeleton screens, which are only for page loads — [NN/g: Skeleton Screens 101](https://www.nngroup.com/articles/skeleton-screens/). Baymard's step-count research implies that splitting checkout into steps is not itself harmful: "the number of form fields" is what drives usability — [Baymard 2024](https://baymard.com/blog/checkout-flow-average-form-fields)
- Order-tracking expectations: 50% rank tracking as the most important account feature; 67% of sites fall short; 25% omit the expected delivery date; 6 required elements — [Baymard 2026](https://baymard.com/research-articles/integrate-tracking-info)
- Cart behaviour: shoppers use the cart as a holding or comparison area, so editing and removal must be easy — [NN/g 2014](https://www.nngroup.com/articles/shopping-cart/). Carts must persist across devices once signed in — [NN/g 2018](https://www.nngroup.com/articles/mobile-checkout-ux/)
- B2B: NN/g's B2B report (188 guidelines) covers "B2B shopping cart and checkout", "Support collaborative purchasing processes", "help your customers justify purchases to leadership", "Displaying complex pricing scenarios" and "Supporting and maintaining customers after the sale" — [NN/g B2B Website Usability](https://www.nngroup.com/reports/b2b-websites-usability/). NN/g notes that B2B consumables, supplies and spare parts suit traditional e-commerce, while custom-priced items need quote flows — [NN/g: B2B Usability](https://www.nngroup.com/articles/b2b-usability/)

### Inferences
- For a branch-supply shop, the abandonment drivers that still apply are surprise costs (show shipping and tax in the cart summary early), slow or unclear delivery (show dates, not ranges), site errors (adaptive messages), and too-long checkout (pre-fill everything from the account). Account creation and card trust largely disappear for invoiced, authenticated B2B buyers.
- On a one-page checkout, a step indicator adds little. The meaningful "progress" UI is the post-order status stepper (Baymard tracking element #2) and the approval state.
- Collaborative purchasing (NN/g B2B) plus Amazon Business approvals suggest adding "Bagikan keranjang" or "Ajukan ke atasan", with comments to the approver, if branch staff need sign-off.

### Gaps
- No public 2024–2026 quantitative study was found isolating the effect of checkout progress indicators (count of steps shown, labelled vs numbered). Baymard's specific guideline is likely Premium-only.
- Baymard's abandonment reasons are US survey data; no comparable Indonesian survey of abandonment reasons was found this session.
- NN/g's B2B and checkout report contents (individual guidelines) are paywalled; only the topic lists were available.
