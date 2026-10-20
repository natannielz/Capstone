# B2B and corporate procurement e-commerce UX: how business buyers with multiple users, approvals and contract prices shop online

Scope note: these notes are for a cooperative (koperasi) shop selling office and pantry supplies to BNI bank branches. Sources were read on 21 October 2026. Platforms covered: Amazon Business, Alibaba.com, Staples Business Advantage, Mbizmarket, Bhinneka Bisnis, Ralali, Erajaya/Eraspace Enterprise, Blibli/Tokopedia/Shopee, and LKPP Katalog Elektronik v6 / INAPROC. Also covered: UX research from NN/g and Baymard, practitioner articles, and Indonesian tax rules (DJP, Kemenkeu, Ortax).

## 1. Price visibility: hidden contract prices, negotiated catalogs, tiered/volume pricing, MOQ

### Takeaway
Most platforms keep the catalog open to everyone and change only the price layer. Guests see a list or "from" price, or a sign-in / quote prompt. Signed-in buyers see their own price (business price, contract price, or a negotiated quote) on every surface. Indonesian B2B platforms put negotiation into the flow itself: cart, then RFQ or "Nego", then quotation, then PO. They also show prices with and without PPN. UX research (NN/g) warns strongly against hiding prices with no explanation: price is the information business buyers want most.

### Cited Findings
- NN/g: when business users ranked 28 kinds of B2B site information, price came first by far, scoring 29% higher than product availability in second place. NN/g calls the lack of pricing the most user-hostile element of B2B sites. — [NN/g, B2B Usability](https://www.nngroup.com/articles/b2b-usability/)
- NN/g has repeatedly seen business users get frustrated and leave sites that hide prices, often for competitors. If exact prices can't be shown, it recommends sample prices, price ranges or MSRP, and typical-scenario prices rather than complex calculators. — [NN/g, Pricing information gives B2B sites a competitive advantage](https://www.nngroup.com/articles/show-price/); [NN/g, B2B vs B2C](https://www.nngroup.com/articles/b2b-vs-b2c/)
- NN/g also advises keeping enough product information outside registration barriers that users can judge fit, because users resist registration and lead-gen forms. — [NN/g, B2B Usability](https://www.nngroup.com/articles/b2b-usability/)
- Practitioner guidance (2026) on "login to see price":
  - Use the gate for contract pricing, dealer/reseller channels and custom items. Usually skip it for commodity items with stable prices.
  - Keep search, filters, specs and case sizes usable before login, and put the login CTA next to the price area.
  - Say what signing in unlocks, e.g. "log in to view your contract price, stock and reorder history".
  - Keep cart and filters intact after login, and give new buyers a separate "request account / contact sales / quote" path.
  - Suggested metrics: login completion, logged-out quote requests, add-to-cart after sign-in, and support tickets about missing prices.
  — [Ecom Design Pro, Login to See Price UX](https://ecomdesignpro.com/login-to-see-price/)
- B2B UX practice:
  - After sign-in, every price (search results, product page, cart) should be the account price. Showing a list price the buyer knows is wrong undermines trust.
  - Show volume breaks next to the quantity field (e.g., "1–49: $4.20, 50+: $3.85").
  - Decide deliberately what anonymous visitors see: list price, a "Sign in for your price" prompt, or sample prices.
  — [Chykalophia, B2B ecommerce UX best practices](https://www.chykalophia.com/insights/ui-ux-best-practices-b2b-e-commerce)
- A distributor-portal vendor describes per-customer settings: contract pricing on every screen, including quick order, cart and order history. Tier pricing shows on item pages when lower tiers exist, along with the customer's last ordered quantity and date. Admins also control whether a customer sees stock levels and price breaks and whether their catalog is restricted to authorised items. — [Ask the Ledger, B2B web portal](https://asktheledger.com/b2b-web-portal.html)
- Amazon Business:
  - Signed-in business customers see business-only prices, marked with a blue badge. Product pages have a "Business Price" section.
  - "Quantity Discounts" appear as "Buy More, Save More" offers.
  - Other business-exclusive pricing types: quantity discounts, progressive discounts and business-only offers.
  — [Amazon Business, Savings, Credit & Pay By Invoice](https://business.amazon.com/en/discover-more/blog/savings-and-credit); [Amazon Business UK, 20 Questions](https://static-assets.business.amazon.com/assets/gb/marketing-assets/guides/20_Questions_About_Amazon_Business.pdf)
- Amazon Business UK offers VAT-exclusive pricing for business buyers. — [Amazon Business UK, 20 Questions](https://static-assets.business.amazon.com/assets/gb/marketing-assets/guides/20_Questions_About_Amazon_Business.pdf)
- Amazon.in shows a "Business Invoice" badge on items whose sellers issue business (tax) invoices. Buyers are told to stay signed in to the business account while browsing so eligible products are flagged. — [Amazon.in, Ordering for Business](https://www.amazon.in/gp/help/customer/display.html?nodeId=Tc5tliFYOwPD7TPHVH)
- Prime Business customers can have "Your Catalog", a custom catalog of commonly bought items curated by admins. — [Amazon Business, Business Lists](https://business.amazon.com/en/learn/business-lists)
- Alibaba.com:
  - Search results can be filtered by minimum order quantity (MOQ).
  - Only "Ready to Ship" items have a "Start Order" button.
  - Samples, orders below MOQ, lower prices or customisation all require "Contact Supplier" / chat.
  - Buyers can post a Request for Quotation (RFQ) that sellers bid on.
  — [Alibaba, How to Buy from Alibaba (official, 2020)](https://seller.alibaba.com/businessblogs/how-to-buy-and-source-from-alibaba-px65f6s8); [Alibaba Buyer Central, How it works](https://buyer.alibaba.com/page/HowItWorks/Page.html)
- Alibaba's quote guidance asks buyers to request one itemised format from every supplier (unit price, MOQ, sample fee, tooling, packaging, shipping, lead time) and compare total landed cost. — [Alibaba Trade Tools, accurate supplier price quote](https://trade-tools.alibaba.com/help/price_quote/how-to-get-an-accurate-supplier-price-quote)
- Mbizmarket:
  - Home page advertises unit, wholesale ("grosiran") and special-PPN prices for PKP sellers.
  - Product pages show prices both including and excluding tax, plus TKDN/BMP values for government buyers.
  - Buyers can compare up to three products.
  - "Kontraktual" pricing gives buyers long-term agreed prices.
  - Decimal quantities are allowed for units such as kg, litre and metre.
  — [Mbizmarket home](https://www.mbizmarket.co.id/); [Mbizmarket deck, OECD/SlideShare](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718)
- Mbizmarket's standard flow: add to cart, create an RFQ ("Membuat Permintaan RFQ"), receive the seller's quotation, approve it by creating a PO, receive the invoice, pay, then confirm receipt. — [Mbizmarket, Cara Bertransaksi](https://www.mbizmarket.co.id/cara-transaksi)
- Mbizmarket's "Transaksi Ringkas" lets users order without the RFQ and quotation steps. — [Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718)
- Bhinneka (current account tiers, 2025–26):
  - Personal accounts.
  - "Korporasi Basic": RFQ & Instant Quotation, plus Corporate Purchase (renewal and subscription).
  - "Korporasi Essential", via sales contact: advanced corporate purchase, tender submission, makloon and "kontrak payung" (umbrella contracts), API integration, priority sales, and a personalised payment tenor.
  — [Bhinneka, Akun Pengguna](https://www.bhinneka.com/akun-bhinneka); [Bhinneka register page](https://accounts.bhinneka.com/register)
- Bhinneka's older e-procurement product has a "Nego" button: the buyer enters an offer price or quantity, Bhinneka sales replies within 1x24 hours, and the approved quotation goes back to the requestor. It also advertises corporate pricing and volume discounts on selected products. — [Bhinneka blog, 9 Kelebihan e-Procurement](https://www.bhinneka.com/blog/kelebihan-bhinneka-e-procurement/); [Bhinneka blog, Bhinneka Bisnis](https://www.bhinneka.com/blog/penuhi-kebutuhan-korporasi-dengan-cerdas-efisien-melalui-bhinneka-bisnis/)
- Ralali: products are wholesale-priced and may carry an MOQ, though some can be bought singly. Buyers can contact the seller for a special offer ("Hubungi Penjual"), and an RFQ feature has existed since 2017. — [Ralali news, Harga Grosir](https://news.ralali.com/harga-grosir/); [Ralali, Cara Belanja](https://www.ralali.com/carabelanja); [Ralali, Tentang Kami](https://www.ralali.com/tentangkami)
- Erajaya's B2B arm (enterprise.eraspace.com, "Enterprise Solutions") sells to companies with "Harga dan Paket Spesial" through a contact form rather than a self-serve price list. Its stated customers include Korporasi, Koperasi, SMB and schools. — [Enterprise Eraspace](https://enterprise.eraspace.com/); [Erajaya Enterprise & Education, LinkedIn](https://www.linkedin.com/posts/erajaya-enterprise-and-education-solution-289a71273_sudah-tahukah-kamu-di-erajaya-terdapat-divisi-activity-7118519905192185856-gWHQ)
- LKPP Katalog v6: negotiated prices cannot exceed the displayed catalogue price. Negotiation covers the DPP (pre-tax) price of the product, any add-on services and shipping. — [INAPROC, Syarat dan Ketentuan Katalog v6](https://bantuan.inaproc.id/hc/id-id/articles/14461422000143-Syarat-dan-Ketentuan-Katalog-Elektronik-Versi-6-Versi-1-5); [LKPP/Kemlu guide PP](https://ukpbj.kemlu.go.id/uploads/laporan/20231101122627-panduan-pp.pdf)

### Inferences
- For a coop selling to known BNI branches, the strongest-supported pattern is an open catalog (photos, specs, pack size) with prices shown only to signed-in branch users. The guest prompt should say what is gained ("Masuk untuk melihat harga kontrak BNI, stok, dan riwayat pesanan") and offer a second path for unregistered branches ("Daftarkan cabang / Hubungi pengurus"). A bare "login to see price" with no explanation is the main anti-pattern.
- With contract prices, the signed-in price must be the same on the card, product page, cart, order history and invoice. Showing PPN-inclusive and PPN-exclusive prices side by side (Mbizmarket's pattern) suits buyers whose finance staff reconcile DPP and PPN.
- MOQ and pack-size rules (e.g., a box of 12) belong next to the quantity stepper, with inline rounding notes. Volume tiers can be shown as a small table under the price.
- Indonesian B2B platforms treat negotiation and quotation as a normal step (RFQ, Nego, Minta Penawaran). A coop could keep this optional, e.g. "Minta penawaran" for unusual quantities or non-catalog items, and use direct checkout at contract price by default, as Mbizmarket's "Transaksi Ringkas" does.

### Gaps
- Baymard's B2B guidelines on price display are paywalled (270+ guidelines; Staples and Office Depot are among the benchmarked sites). Only the overview was accessible: [Baymard B2B](https://baymard.com/research/business-to-business).
- I didn't fetch a current Alibaba.com product page, so the exact layout of its tiered price block (e.g., quantity-range price columns) is not verified from a primary page.
- No Office Depot / ODP Business Solutions primary page was retrieved.

## 2. Multi-user business accounts: requester vs approver, spending limits, approval workflow, notifications, "menunggu persetujuan"

### Takeaway
The common model has three roles: admin, requester/buyer, and approver. Approval is triggered by a rule (all orders, or orders above a limit, set per group or per user). The requester's final button changes to "Submit for approval". The order then waits in a locked, time-limited state. Approvers act from an email, a dashboard banner or an approvals queue. They can approve, reject with a reason, or (on some platforms) edit fields such as PO, budget or payment. Indonesian platforms use the same model, named Requestor / Approver / Power User (Bhinneka) or PP → PPK → Bendahara (Mbizmarket and LKPP for government).

### Cited Findings
- Amazon Business approval policies:
  - Admins create policies under Business Settings → Buying Policies and Approvals, per group.
  - Spending-limit policies cover all orders or orders above a threshold.
  - Each approval level can have up to 10 approvers, and one approval per level is enough. Multiple levels are allowed.
  - When an order needs approval, all designated approvers are notified.
  — [Amazon Business, Configure Spend Limits](https://business.amazon.com/en/learn/spending-limit); [Amazon help, Add approvers](https://www.amazon.com/gp/help/customer/display.html?nodeId=202209010)
- Amazon Business holds a cart awaiting approval at locked prices and quantities for 7 days, weekends included (14 days for Prime Business Medium and Enterprise). If nobody approves in time, the order is cancelled automatically and the buyer resubmits at possibly different prices. — [Amazon Business, Guided Buying](https://business.amazon.com/en/solutions/compliance-management/guided-buying); [Amazon Business, Approving Requests](https://business.amazon.com/en/learn/approving-requests)
- Amazon Business approver experience:
  - Approvers are notified by email, on their dashboard, and when they log in.
  - The order details page shows shipping address, payment method, items and (if configured) a PO field.
  - Approvers can reject single items and let the rest proceed. A wrong address or payment method means rejecting the whole order.
  - Approvers should explain rejections in comments. Buyers get an email with a link to rebuild the rejected cart, and a confirmation email when the order is approved.
  — [Amazon Business, Approving Requests](https://business.amazon.com/en/learn/approving-requests)
- Amazon Business requester checkout:
  - For orders that need approval, "Submit order for approval" replaces "Place your order".
  - Requesters can add a comment for the approver, or an additional approver (e.g., IT) who reviews before the designated approver.
  - Status is tracked on an "Approvals Detail Page" linked from Your Orders.
  — [Amazon help, Check out with Amazon Business](https://www.amazon.com/gp/help/customer/display.html?nodeId=201633090); [Amazon help, Add approvers](https://www.amazon.com/gp/help/customer/display.html?nodeId=202209010); [Amazon Business Approvals guide PDF (2015)](https://images-na.ssl-images-amazon.com/images/G/01/BISS/B2B/pdfs/AmazonBusinessAccounts_Approvals._V288121526_.pdf)
- Amazon Business custom approvals set an individual spending limit for a specific member, which overrides group spend-limit approvals for that member. — [Amazon.in help, Custom buying policy approvals](https://www.amazon.in/gp/help/customer/display.html?nodeId=GDPNDDKWPT4NTPS6)
- Amazon Business product policies:
  - Restricted categories only show a warning; an approver must be added to route those items for review.
  - Blocked items cannot be purchased.
  - If several policies apply, all workflows trigger, but approvers get only one notification per order.
  — [Amazon help, Types of approval policies](https://www.amazon.com/gp/help/customer/display.html?nodeId=TFbz7F1T8m2PDj1KtZ); [Amazon Business, Guided Buying Overview](https://business.amazon.com/en/learn/guided-buying-overview)
- Staples Business Advantage approvals:
  - Approvers see pending orders on their home page or under Account → Approve Orders, filterable by order number, approver, orderer or budget approvals.
  - Declining requires a reason.
  - "Global approvers" can approve for all users when the assigned approver is unavailable.
  - Approvers may modify PO, PO release, budget, payment method and delivery date, but not the ship-to address.
  — [Staples Advantage, Administrator Quick Reference Guide](https://marketingassets.staples.com/m/5d59618f9c39b46b/original/Administrator-s-Quick-Reference-Guide.pdf)
- An older Staples Preferred guide lists three approval-limit modes: no approval, a dollar limit, or all orders. Approvers get an email and an "Order Approval Message" banner on the home page, can bulk "Approve Selected", and the orderer gets a confirmation email. — [Staples Preferred Quick Reference Guide](http://media.staples.com/cadv/CADV_Quick_Ref_Guide_Eng.pdf)
- Bhinneka e-procurement roles:
  - Power User: buys without approval and sets other users' roles, transaction limits and product categories.
  - Requestor: creates orders and can be multi-department.
  - Approver: approves requestor orders and can be multi-department.
  - Budget limits are set per department by Administrator or Power User.
  — [Bhinneka blog, 9 Kelebihan e-Procurement](https://www.bhinneka.com/blog/kelebihan-bhinneka-e-procurement/)
- Bhinneka's b2b.id "Smart Procurement" platform lists these features: multi-user (requestor and approver), spending limit and control per business unit, business-unit grouping that mirrors company structure, an order approval workflow by authorisation level, and reporting dashboards. The admin invites colleagues as Requestor or Approver. — [Bhinneka blog, eProcurement di Bhinneka Bisnis](https://www.bhinneka.com/blog/belanja-keperluan-bisnis-lebih-mudah-dengan-eprocurement-di-bhinneka-bisnis/)
- Mbizmarket:
  - One business account can be used by several employees once "Manajemen Persetujuan" is turned on. Finance and procurement staff are invited as approvers and can approve online from anywhere.
  - For government buyers it claims an integrated PP → PPK → Bendahara approval chain and a "Sub-Staff" role (operator or shopper on behalf of the PP).
  - "Single User Multiple Account" lets one user belong to more than one company without a new login.
  — [Mbiz Insight](https://insight.mbiz.co.id/2020/04/21/bagaimana-karyawan-saya-bisa-menggunakan-e-procurement-mbiz/); [Mbizmarket Play Store listing](https://play.google.com/store/apps/details?id=com.mbizmarket.app&hl=id); [Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718)
- Mbizmarket's "ringkas" (short) government flow comes in two forms. Orders under Rp50 million can be placed "with PPK approval" (PPK approves the PR and creates the PO) or "without approval". The regular flow (up to Rp200 million) goes RFQ → quotation → PR → PPK approves and creates PO → PPK confirms receipt → Bendahara pays. Negotiating the quotation is mandatory above Rp50 million. — [Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718)
- UX research: in B2B purchases, research, approval, purchase, receipt and use are often done by different people. NN/g recommends tools that let customers share products and cart contents with teammates for review, discussion and approval. Its personas include "Ashley the Office Admin" and "Barbara the Boss". — [NN/g, B2B Usability](https://www.nngroup.com/articles/b2b-usability/); [NN/g, B2B vs B2C](https://www.nngroup.com/articles/b2b-vs-b2c/)

### Inferences
- For BNI branches, a minimal role set is enough: Admin (coop or BNI area office), Pemesan (branch requester, e.g., the branch's general-affairs officer) and Penyetuju (branch manager or area approver). Rules: approval on all orders or above Rp X per branch.
- Requester-side states supported by these sources:
  - The checkout button becomes "Kirim untuk persetujuan".
  - The order list shows a "Menunggu persetujuan" status with the approver's name and a deadline (Amazon locks prices for 7 days, then auto-cancels).
  - A rejected order shows the reason and a one-click "Perbaiki & kirim ulang" (rebuild cart).
- Approver side: an email or WhatsApp-style notification, a home-page banner, and an "Persetujuan" queue with approve/decline (reason required), per-line rejection, and a global backup approver. Do not let approvers change the delivery address (Staples rule). Allowing them to add or change the PO or budget field is common.

### Gaps
- No primary screenshots or text were found for Mbizmarket's or Bhinneka's private-sector approval screens (queue layout, status labels). The Indonesian sources are marketing or blog descriptions. The Bhinneka b2b.id post may describe a legacy product (it is republished with a 2026 date).
- No source documents how Indonesian private B2B platforms label pending-approval orders (e.g., the exact "Menunggu Persetujuan" wording) outside LKPP's status names.

## 3. Shopping lists, requisitions, quick reorder, order templates, bulk add by SKU, punchout

### Takeaway
Repeat buying is the main B2B job, and the tools for it are reorder from history, shared lists (reorder lists vs one-time shopping lists), quick-order pads that take SKU and quantity by typing, pasting or CSV upload, and punchout to procurement systems. Indonesian platforms offer "Repeat Order", "Seleksi di Keranjang", "Ajukan Pengadaan" with a list upload, and "Quick Buy".

### Cited Findings
- Amazon Business Lists:
  - Reorder Lists keep items visible after purchase. Shopping Lists hide purchased items (e.g., new-hire kits).
  - Items can be added from order history, keyword, ASIN, supplier SKU or manufacturer part number, with comments and priority levels.
  - Lists can be shared with people, groups or the whole company, as "Buy only" or "Edit + buy". Buyers can buy the whole list or selected items and can copy shared lists.
  — [Amazon Business, Business Lists](https://business.amazon.com/en/learn/business-lists); [Amazon help, Share your lists](https://www.amazon.com/gp/help/customer/display.html?nodeId=GH3SBC9WDJUH3TFU)
- Amazon Business:
  - Punchout connects to more than 200 e-procurement systems: the user starts in their own system, punches out to Amazon, and the cart returns for approval.
  - "Punch-in" reverses this: start on Amazon, then send the cart to the e-procurement system.
  - SSO can set a default buying role ("Punchout user" vs "Requisitioner").
  — [Amazon Business, Punchout](https://business.amazon.com/en/solutions/systems-integration/punchout); [Amazon Business SSO guide](https://static-assets.business.amazon.com/assets/global/documents/guides/amazon-business-sso-integration-guide.pdf)
- Staples punchout (university Workday job aids):
  - The requisition header defaults to the requester's cost centre and ship-to.
  - The user connects to the Staples site, adds to cart and submits. The cart returns as requisition lines where spend category, unit of measure and cost-centre splits are completed.
  - A PO is generated and approved, Staples sends the supplier invoice automatically, and the user must record a receipt before the invoice is paid.
  — [UAMS, Create a Purchase Requisition from a Punchout Session](https://supplychain.uams.edu/wp-content/uploads/sites/11/2023/07/UA_System_QRG_FIN19_Create-a-Purchase-Requisition-Staples.pdf); [Accounting support, How to use Staples Punchout](https://accountingfinancesupport.zendesk.com/hc/en-us/articles/20463089485581-How-to-use-Staples-Punchout)
- Quick order pad: B2B buyers often already know the SKU. Recommended design:
  - A "Quick order" link in the main nav for logged-in buyers.
  - A SKU and quantity grid with type-ahead.
  - Pasting "SKU, qty" lines and CSV upload.
  - Inline flags before the cart for discontinued items (with replacement), MOQ and case-pack rounding.
  — [Chykalophia](https://www.chykalophia.com/insights/ui-ux-best-practices-b2b-e-commerce)
- A distributor portal reports that one-click Reorder on any past order or invoice (copied at today's prices) is how returning customers place most orders. "My Items" starred favourites support fast reorder. — [Ask the Ledger](https://asktheledger.com/b2b-web-portal.html)
- B2B portal "must-haves" are login with account context, order history with reorder, quick order, and checkout with PO and terms. Saved lists or job kits are listed as standard portal features. — [Hunchbite, B2B self-service portals](https://hunchbite.com/guides/b2b-self-service-portal-industrial); [Ecommerce Partners, B2B design](https://ecommercepartners.com/2025/03/23/unlock-b2b-sales-design-for-higher-conversion-rates/)
- Mbizmarket features: "Repeat Order" (rebuy a completed order), "Seleksi di Keranjang" (choose which cart items become a transaction), and downloadable BAST per delivery. — [Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718)
- Bhinneka:
  - "Ajukan Pengadaan": from the dashboard, enter a procurement name, budget and notes, and optionally upload an item list. Sales replies, and the buyer clicks Accept or "Ask to Revise".
  - "Quick Buy" is the fast, supervisor-monitored checkout path.
  - In the Bhinneka cart, a "Tambahkan Produk" option lets buyers request items not found on the site.
  — [Bhinneka blog, 9 Kelebihan](https://www.bhinneka.com/blog/kelebihan-bhinneka-e-procurement/); [ITB, Corporate Purchase Bhinneka guide](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/03/Corporate-Purchase-Bhinneka.com_.pdf)
- Amazon Business also offers Subscribe & Save / recurring deliveries (up to 15% off selected items). — [Amazon Business, Savings](https://business.amazon.com/en/discover-more/blog/savings-and-credit)

### Inferences
- For branch pantry and ATK restocking, the strongest patterns are:
  - "Pesan lagi" on every past order.
  - A shared "Daftar belanja rutin cabang" (reorder list) the coop or area admin can curate per branch type.
  - A quick-order grid by coop SKU or name for staff who know the items.
  - Cart-line selection so one cart can be split into separate submissions.
- Punchout is out of scope unless BNI runs an e-procurement system that supports cXML/OCI. The requisition-to-approval pattern can be reproduced inside the coop's own workspace instead.
- A "Minta barang di luar katalog" request (Bhinneka's add-product-not-found) covers ad-hoc branch needs without breaking the catalog flow.

### Gaps
- No primary Indonesian source shows a SKU quick-order or CSV upload on Mbizmarket or Bhinneka; only list upload in "Ajukan Pengadaan" is documented.
- I found no information on whether BNI's internal procurement uses an e-procurement suite that could punch out.

## 4. Branch / delivery address management for multi-location organisations; cost centres / budgets

### Takeaway
Multi-location buyers manage addresses at the organisation or group level, not per user. Admins define shared ship-to addresses (Amazon Business shared addresses; Staples ship-to locations chosen at login). Users are authorised for specific locations, budget centres and POs, with defaults. Cost centre, GL code, location and department are optional or required checkout fields that flow onto invoices and reports.

### Cited Findings
- Amazon Business checkout:
  - Buyers select a buying group and can ship one cart to every address in that group simultaneously.
  - "Business order information" offers optional fields: PO number, GL code, cost centre, location, department and comments for the approver. The fields appear when approvals or reporting are enabled.
  — [Amazon help, Check out with Amazon Business](https://www.amazon.com/gp/help/customer/display.html?nodeId=201633090)
- With Amazon Business shared settings, admins designate shared payment methods and shared shipping addresses for buyers to choose at checkout. An admin must add a shared address before orders can be placed. Accounts default to individual pay. — [Amazon Pay by Invoice Admin Guide](https://m.media-amazon.com/images/G/01/BISS/B2B/pdfs/Pay_by_Invoice-AdminGuide-1.24.19._CB1548359964_.pdf)
- Amazon Business groups mirror organisational structure with group-level purchase settings. SSO can provision users into groups, e.g. "\Company\Finance\New York". — [Amazon Business, Team management](https://business.amazon.com/en/solutions/team-management); [Amazon SSO guide](https://static-assets.business.amazon.com/assets/global/documents/guides/amazon-business-sso-integration-guide.pdf)
- Staples Advantage:
  - When setting up a user, admins choose authorised ship-to locations, then authorised budget centres per ship-to (with a default budget centre and an "all current and future" option), then authorised POs per ship-to with a default PO.
  - Users authorised for more than one ship-to pick a "Ship-to Location" on login.
  — [Staples Admin Quick Reference Guide](https://marketingassets.staples.com/m/5d59618f9c39b46b/original/Administrator-s-Quick-Reference-Guide.pdf); [Staples Preferred Quick Reference Guide](http://media.staples.com/cadv/CADV_Quick_Ref_Guide_Eng.pdf)
- A university procurement office found that vague ship-to entries (building only) caused failed or returned Staples deliveries. It required detailed deliver-to entries (building, floor, room) and a "requesting entity" tied to each ship-to account, plus a free-text internal memo with contact and room details. — [UAMS memo on Deliver-to/Ship-to](https://supplychain.uams.edu/wp-content/uploads/sites/11/2022/11/Updating-Deliver-to-Ship-to-Locations-on-requisitions-using-a-Requesting-Entity.pdf)
- Staples markets user spend limits, approval flows and purchasing/user reporting for business accounts. — [Staples Business Account Options](https://www.staplesadvantage.com/learn/staples-business-advantage-account-options)
- Bhinneka: spending limit per business unit, business-unit grouping that mirrors company structure, and budget limits per department. The corporate checkout requires saved shipping and billing addresses in the dashboard. — [Bhinneka blog eProcurement](https://www.bhinneka.com/blog/belanja-keperluan-bisnis-lebih-mudah-dengan-eprocurement-di-bhinneka-bisnis/); [Bhinneka 9 Kelebihan](https://www.bhinneka.com/blog/kelebihan-bhinneka-e-procurement/); [ITB Bhinneka guide](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/03/Corporate-Purchase-Bhinneka.com_.pdf)
- Mbizmarket buyer registration for government units asks for both a shipping address and a billing address. Mbizmarket advertises budget control, spending reports and analytics. — [Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718); [Mbizmarket news, platform e-procurement](https://www.mbizmarket.co.id/news/platform-e-procurement/)
- LKPP Katalog v6 supports delivery "Langsung ke Satu Lokasi" or "Langsung ke Banyak Lokasi". With many locations, the buyer can choose staged payments (termin) grouped by delivery. — [INAPROC, Panduan Pembayaran LS Pemda](https://bantuan.inaproc.id/hc/id-id/articles/11087582760975-Panduan-Pembayaran-Mekanisme-Langsung-LS-Pemerintah-Daerah-Pemda)

### Inferences
- BNI branch addresses should be organisation-managed master data, not typed by each requester. Each user is linked to one or more branches (KC/KCP/outlet), and the branch defaults at checkout. Delivery details should include floor or unit and a recipient name and phone, as the UAMS memo suggests.
- Budget control, if needed, fits per branch (monthly limit), with the approval rule triggering above it. Cost centre or branch code should appear on the order and invoice for BNI finance.
- A branch/account selector in the header (like Staples' ship-to selection at login) suits users who serve several branches.

### Gaps
- No source on how BNI structures branch cost centres or budgets for office supplies. This needs confirming with the client.

## 5. Invoices, payment terms (tempo / TOP), PO numbers at checkout, faktur pajak in Indonesia

### Takeaway
B2B checkouts add a PO or reference field, invoice/terms payment (net 30/45/60 abroad; "TOP/tempo" versus "CBD" in Indonesia) and tax-document options. Indonesian platforms add specific tax controls: a "dengan faktur pajak" checkbox and buyer NPWP/NITKU fields (Bhinneka, Blibli), per-item or overall PPN/PPh settings, and a WAPU vs non-WAPU choice (Mbizmarket). This matters for BNI: as a BUMN it is a designated PPN collector (WAPU) for payments over Rp10 million, and it may withhold PPh 22.

### Cited Findings
- Amazon Business Credit Account (formerly Pay by Invoice):
  - Invite-only, with standard 30-day terms and no fees or interest. Prime Business plans unlock 45 or 60 days on approval.
  - Admins set it as the default payment for groups.
  - Invoices can be consolidated monthly or by PO, sent to several email addresses, and carry custom "Business Order Information" such as the PO number.
  — [Amazon Business, Business Credit Account](https://business.amazon.com/en/solutions/payment-options/business-credit-account); [Amazon Business blog, How to use Business Credit Account](https://business.amazon.com/en/blog/how-to-use-business-credit-account); [Amazon Business, Accounting & Pay by Invoice](https://business.amazon.com/en/small-business/success-studio/blogs/accounting-and-pay-by-invoice)
- Amazon Pay by Invoice admin guide:
  - One purchase-level invoice is issued when all items ship, and invoices appear on Your Orders and Your Invoices.
  - Invoices are searchable by PO, amount and date, and reports include PO number, invoice status, due amount and due date.
  — [Amazon Pay by Invoice Admin Guide](https://m.media-amazon.com/images/G/01/BISS/B2B/pdfs/Pay_by_Invoice-AdminGuide-1.24.19._CB1548359964_.pdf)
- Amazon.in places an optional PO field on the "Business Order Information" step before address selection. On amazon.com, buyers can verify or change the PO number on the review page. — [Amazon.in, Ordering for Business](https://www.amazon.in/gp/help/customer/display.html?nodeId=Tc5tliFYOwPD7TPHVH); [Amazon help, Check out](https://www.amazon.com/gp/help/customer/display.html?nodeId=201633090)
- Mbizmarket RFQ form (buyer):
  - Purpose of purchase ("keperluan untuk kegiatan apa") and a note to the seller.
  - Choice of CBD or TOP, entering the number of days for TOP.
  - Tax set overall or per item, covering PPN and PPh. PPN is hidden automatically if the seller is non-PKP.
  - A WAPU vs non-WAPU payment choice with a reason.
  - The seller can counter the TOP length, delivery estimate and tax settings in the quotation.
  — [ITB, Mbizmarket Alur Transaksi PDF (2024)](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/05/Mbizmarket-Alur-Transaksi.pdf)
- Mbizmarket says it complies with PMK 58/2022 as a tax collector and supports regional taxes. Invoices are generated automatically after the PO is confirmed, and buyers must use "Konfirmasi Penerimaan Barang" so funds are released to the seller. — [Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718); [Mbizmarket, Cara Bertransaksi](https://www.mbizmarket.co.id/cara-transaksi)
- Bhinneka corporate checkout:
  - "Corporate Purchase" (direct payment) or "Ask for Quote" from the cart, with saved shipping and billing addresses.
  - A "Number Ref Order" field for the buyer's internal document number.
  - A "dengan faktur pajak" checkbox.
  - Payment by credit card, virtual account or Term of Payment.
  — [ITB, Corporate Purchase Bhinneka guide](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/03/Corporate-Purchase-Bhinneka.com_.pdf)
- Bhinneka advertises flexible payment terms, faktur pajak requests on every e-procurement transaction, and report downloads in CSV or PDF. It implemented e-Bupot PPh 23 with DJP host-to-host integration (2018). — [Bhinneka blog, Bhinneka Bisnis](https://www.bhinneka.com/blog/penuhi-kebutuhan-korporasi-dengan-cerdas-efisien-melalui-bhinneka-bisnis/); [Bhinneka 9 Kelebihan](https://www.bhinneka.com/blog/kelebihan-bhinneka-e-procurement/); [TopBusiness 2018](https://www.topbusiness.id/17385/bhinneka-kampiun-bayar-pajak-berkat-aplikasi-ini.html)
- Blibli faktur pajak requests:
  - Buyers request a faktur pajak from Order Detail → Faktur pajak, entering NPWP, NITKU, address and email.
  - Requests are allowed only after delivery is complete and up to the 20th of the following month.
  - Not available for non-PPN products, sellers that are not PKP, or orders in return/refund.
  - Blibli-fulfilled items take 1–3 working days; seller items take 3–14 days. Status is tracked on a Faktur pajak page.
  — [Blibli FAQ, Pembayaran](https://www.blibli.com/faq/topic/pembayaran/)
- Ralali payments: bank transfer, credit card, instant payment, Kredivo (including 30-day pay later) and "Ralali Cicilan". Ralali has also offered RalaliPay and RalaliCredit and promotes "Ralali Billing". — [Ralali, Cara Belanja](https://www.ralali.com/carabelanja); [Ralali, Metode Pembayaran](https://www.ralali.com/metodepembayaran); [Ralali, Tentang Kami](https://www.ralali.com/tentangkami); [Ralali Play Store](https://play.google.com/store/apps/details?id=com.ralali&hl=en_SG)
- BUMN as PPN collector (PMK 8/PMK.03/2021):
  - BUMNs collect, deposit and report the PPN on purchases from PKP suppliers ("rekanan").
  - Exception: payments of at most Rp10,000,000 including tax that are not split from a larger transaction. Then the supplier collects PPN as usual.
  - The supplier must still issue a faktur pajak for every delivery, at delivery or when payment is received.
  — [JDIH Kemenkeu, PMK 8/2021 summary](https://jdih.kemenkeu.go.id/dok/8-pmk-03-2021/summary); [BPK, PMK 8/2021](https://peraturan.bpk.go.id/Details/160062/pmk-no-8pmk032021); [Ortax, PPN WAPU BUMN](https://ortax.org/ppn-wapu-bumn-dan-badan-usaha-tertentu)
- Faktur pajak code 03 is used for deliveries to non-government PPN collectors such as BUMNs. Deliveries to a collector keep code 02/03 even when they would otherwise fall under code 04 (DPP nilai lain). — [DJP, Kode Transaksi Faktur Pajak](https://www.pajak.go.id/id/artikel/kode-transaksi-faktur-pajak); [Ortax](https://ortax.org/ppn-wapu-bumn-dan-badan-usaha-tertentu)
- PPh 22: BUMNs (and listed BUMN subsidiaries) collect PPh Pasal 22 at 1.5% of the transaction value on purchases of goods related to their business. Payments of at most Rp10,000,000 that are not split are exempt. — [DJP, Pemungutan PPh Pasal 22](https://pajak.go.id/id/pemungutan-pajak-penghasilan-pasal-22)
- From 1 January 2025, non-luxury goods carry PPN at 12% × DPP nilai lain (11/12 of price), an effective 11%. Ordinary (non-WAPU) deliveries now use faktur code 04. PMK-11/2025 consolidates the DPP nilai lain rules. — [DJP press release on PMK-11/2025](https://www.pajak.go.id/en/node/114038); [Ortax, Kode Transaksi era Coretax](https://ortax.org/menentukan-kode-transaksi-faktur-pajak-era-coretax); [Bisa Pajak](https://bisapajak.id/ikuti-cara-membuat-faktur-pajak-dpp-nilai-lain-di-coretax/)
- Koperasi that are PKP issue e-Faktur through Coretax, with master data for repeat customers. — [Smartcoop, Faktur Pajak Koperasi melalui Coretax](https://smartcoop.id/post/faktur-pajak-koperasi-melalui-coretax-kewajiban-pkp-pembuatan-e-faktur-dan-spt-masa-ppn)
- Corporate buyers commonly require a PO, a delivery note (surat jalan) and a faktur pajak (if the seller is PKP) alongside the invoice. Without PKP status and legal documents, a small supplier's invoices may not be processable by corporates. — [Legalyn (2025)](https://legalyn.id/cara-umkm-kecil-bisa-kirim-invoice-ke-korporat-dengan-legalitas/)
- From 1 August 2026, Tokopedia, Shopee, Lazada and Blibli collect PPh 22 from merchants under PMK 37/2025 (0.5% final for turnover of Rp500 million–4.8 billion). This affects sellers on those marketplaces, not a coop's own web shop. — [Bisnis.com, 1 Jul 2026](https://ekonomi.bisnis.com/read/20260701/259/1984734/alasan-tokopedia-shopee-lazada-dan-blibli-diminta-pungut-pajak-e-commerce); [Liputan6, 1 Jul 2026](https://www.liputan6.com/bisnis/read/8130206/tokopedia-shopee-lazada-dan-blibli-resmi-pungut-pajak-pedagang-online)

### Inferences
- Checkout for BNI branches should include:
  - An optional or required "Nomor PO / referensi internal" field, shown on the invoice and searchable in order history.
  - A payment-method choice between transfer now and tempo/TOP for approved branches, with the due date shown.
  - A tax section with a "Butuh faktur pajak" toggle, using the BNI NPWP/NITKU from the organisation profile rather than typed by each branch user.
- If the coop is PKP, the order and invoice must handle WAPU. For orders over Rp10 million (incl. PPN, not split), BNI collects and deposits the PPN itself: the faktur pajak uses code 03 and BNI pays only the DPP. Smaller orders use code 04 at effective 11%. PPh 22 at 1.5% may also be withheld above Rp10 million.
- Invoice and summary screens should show the DPP, the PPN and the net payable amount separately, as Mbizmarket's RFQ form does (subtotal, PPN, PPh, total transaksi, total pembayaran).
- Splitting one need into several sub-Rp10 million orders to avoid WAPU is explicitly excluded ("bukan pembayaran yang dipecah"). The approval workflow should not encourage order splitting for this purpose.
- Document set per order for BNI finance: invoice, surat jalan / delivery proof (BAST-like goods-received confirmation), and faktur pajak (if PKP). These should be downloadable from the order page.

### Gaps
- Not verified whether BNI (and every branch-level purchase) is still designated a PPN collector and PPh 22 collector after the 2025 BUMN-law changes and Danantara. The DJP PPh 22 page appears outdated (it lists PT Bank BNI Syariah, which merged into BSI in 2021). BNI's tax department should confirm.
- The coop's own PKP status (PKP threshold for small businesses, Rp4.8 billion turnover) and tax treatment as a koperasi were not determined.
- I found no Indonesian source specifying how tempo/TOP credit limits are displayed to buyers (remaining limit, overdue status) on Mbizmarket or Bhinneka.

## 6. Combining a public marketing catalog with a logged-in procurement workspace (navigation, header, account switcher)

### Takeaway
The leading pattern is one storefront and catalog whose state changes when a business user signs in. The header gains an account/organisation menu (Amazon's "Account for [business]" dropdown with Switch Accounts, Business Settings, Approve Orders). Prices, badges and policy messages switch to business mode. Account management becomes the B2B user's home: orders, approvals, lists, invoices, users. Indonesian platforms follow the same model, with account tiers (Personal vs Korporasi) and multi-company membership.

### Cited Findings
- Amazon Business:
  - The "Account for [your business]" dropdown has a "Switch Accounts" option for moving between business and personal accounts without logging out. There is also a dedicated Amazon Business app to keep buying separate.
  - Account linking shows the linked account in an account picker at sign-in but keeps addresses, payments and orders separate.
  — [Amazon help, Switch accounts](https://www.amazon.com/gp/help/customer/display.html?nodeId=TCthycvaJ6zmE5NlON); [Amazon help, Account linking](https://www.amazon.com/gp/help/customer/display.html?nodeId=TZjPS5zPdxpKjBmra6); [Amazon Business, Team management](https://business.amazon.com/en/solutions/team-management)
- Older Amazon Business guidance places "Manage your Business" and "Approve Orders" in the business account dropdown, which appears whenever the user is logged in with a business account. — [Amazon Business Approvals guide (2015)](https://images-na.ssl-images-amazon.com/images/G/01/BISS/B2B/pdfs/AmazonBusinessAccounts_Approvals._V288121526_.pdf)
- Amazon Guided Buying shows policy messages in the shopping and checkout experience, answering "what can I buy?" (preferred, restricted and blocked products and sellers). — [Amazon Business, Guided Buying Overview](https://business.amazon.com/en/learn/guided-buying-overview)
- Staples Advantage puts pending approvals on the home page and an Approvals entry under Account in the header. — [Staples Admin Quick Reference Guide](https://marketingassets.staples.com/m/6aaa2d086f5e010/original/Administrator-s-Quick-Reference-Guide.pdf)
- B2B practitioners say the account portal is "where buyers live". It should hold order history with reorder, invoices and payment status, saved addresses, multiple users with roles, approval workflows, and credit/terms visibility. — [Ecommerce Partners, B2B design](https://ecommercepartners.com/2025/03/23/unlock-b2b-sales-design-for-higher-conversion-rates/); [Hunchbite](https://hunchbite.com/guides/b2b-self-service-portal-industrial)
- One catalog, different rules: the portal is a view on top of the same product data. Pricing, visibility and terms are loaded per account at login. — [Hunchbite](https://hunchbite.com/guides/b2b-self-service-portal-industrial)
- Bhinneka uses one site and login for all users. At login it asks the user to continue with the corporate account ("Lanjutkan dengan akun korporasi"). Registration compares Personal, Korporasi Basic and Korporasi Essential tiers side by side. — [ITB Bhinneka guide](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/03/Corporate-Purchase-Bhinneka.com_.pdf); [Bhinneka register](https://accounts.bhinneka.com/register)
- Mbizmarket:
  - "Single User Multiple Account": one user can belong to several companies.
  - Users switch account status between buyer and seller ("Pastikan Status Akun sudah diubah menjadi Penjual").
  - Government buyers enter via INAPROC SSO ("Toko Daring" → Mbizmarket).
  — [Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718); [ITB Mbizmarket PDF](https://eproc.itb.ac.id/wp-content/uploads/sites/78/2024/05/Mbizmarket-Alur-Transaksi.pdf)
- Erajaya keeps consumer retail (Eraspace/Erafone) separate from B2B (enterprise.eraspace.com, a service catalogue plus contact form). — [Enterprise Eraspace](https://enterprise.eraspace.com/); [Erajaya subsidiaries](https://www.erajaya.com/subsidiary-companies)
- NN/g notes B2B buyers compare B2B sites unfavourably with the consumer sites they use after work, and that segmentation splits (e.g., by company size) confuse users who don't know which segment they belong to. — [NN/g, B2B vs B2C](https://www.nngroup.com/articles/b2b-vs-b2c/); [NN/g, B2B Usability](https://www.nngroup.com/articles/b2b-usability/)

### Inferences
- For the coop, the supported design is a single site:
  - Public: catalog, categories, search, product pages without prices or with a sign-in prompt, plus "how ordering works for BNI branches".
  - Signed in: the same pages with contract prices, plus a workspace ("Ruang kerja cabang") reached from the header.
- Header items when signed in: branch/organisation badge or switcher (if the user serves several branches), cart, approvals badge with count (approvers only), and an account menu (Pesanan, Daftar belanja, Persetujuan, Tagihan & faktur, Pengguna & cabang for admins).
- Avoid a separate "B2B portal" subdomain with different navigation. Amazon, Bhinneka and Mbizmarket keep one storefront and switch mode by account. Erajaya's split works only because its B2B is sales-led.

### Gaps
- No primary screenshots of Mbizmarket's or Bhinneka's signed-in header and navigation were retrieved, so their exact menu labels are unverified.
- No Tokopedia or Shopee corporate-buyer programme (multi-user, approvals, tempo) was found in 2023–2026 sources. Their B2B tax-invoice material concerns seller fees ([Klikpajak on Tokopedia](https://klikpajak.id/blog/cara-mendapatkan-faktur-pajak-tokopedia/); [Shopee Seller Edu](https://seller.shopee.co.id/edu/article/24063)). Blibli documents buyer faktur pajak requests but no corporate workspace was found.

## 7. LKPP e-Katalog / INAPROC specifics: catalog structure, comparison, keranjang, negotiation, approval, documents

### Takeaway
Katalog Elektronik v6 (INAPROC, run by LKPP with Telkom) is the most formal Indonesian model:
- Browse etalase and categories.
- Compare up to three products.
- Add to a cart that checks out one supplier at a time.
- At checkout, enter HPS against the RUP budget and choose the approving PPK.
- Negotiate (mandatory) within a 3-working-day response SLA.
- PPK reviews in "Daftar Persetujuan PPK" and creates a digital Surat Pesanan, e-signed by both sides.
- Then delivery, inspection, digital BAST, and payment with tax documents uploaded.

Every step has an explicit status label.

### Cited Findings
- Roles and thresholds:
  - Buyers are PPK, PP or designated officials. The PP may negotiate and order up to Rp200 million, the PPK above that.
  - PP orders up to Rp200 million can be forwarded to the PPK for review and approval.
  - Bendahara Pengeluaran handles payment.
  — [INAPROC, Syarat dan Ketentuan Katalog v6](https://bantuan.inaproc.id/hc/id-id/articles/14461422000143-Syarat-dan-Ketentuan-Katalog-Elektronik-Versi-6-Versi-1-5); [INAPROC, Memulai Pesanan](https://bantuan.inaproc.id/hc/id-id/articles/8167455220367-Memulai-Pesanan)
- Catalog and comparison:
  - In the v5-era guide (updated Jan 2023), the PP picks "Etalase Produk", then an etalase category, then filters.
  - Up to 3 products can be compared via a "bandingkan" checkbox.
  - Products from blacklisted suppliers cannot be bought.
  — [LKPP/Kemlu, User Guide Purchasing Negosiasi PP](https://ukpbj.kemlu.go.id/uploads/laporan/20231101122627-panduan-pp.pdf)
- Keranjang in v6:
  - "Tambah Keranjang" shows "Barang telah dimasukkan ke keranjang. Lihat Keranjang". A full cart shows "Keranjang anda penuh…".
  - +/- steps by 1 or 0.01 depending on the supplier's decimal-quantity setting.
  - Per-item notes ("Tambah Catatan").
  - Checkout selects items from one supplier, then "Beli".
  - Unprocessable items are grouped under "Tidak bisa diproses" with reasons: out of stock, inactive or blacklisted store, or product delisted (wrong category, unreasonable price, trademark issue, TKDN mismatch, etc.).
  - Products still under curation can be bought after a confirmation pop-up.
  — [INAPROC, Memulai Pesanan](https://bantuan.inaproc.id/hc/id-id/articles/8167455220367-Memulai-Pesanan)
- Checkout in v6:
  - Above Rp100 million (goods plus shipping), the buyer must enter an HPS (owner's estimate) no greater than the selected RUP budget and upload an HPS breakdown (.xlsx template, max 10 MB). The HPS is locked after checkout.
  - The PP then selects the PPK (search by name, filter by K/L/PD and Satker) and clicks "Ajukan Negosiasi".
  - Prices at checkout include PPN and PPnBM, with "Tidak Kena Pajak" labels where applicable.
  — [INAPROC, Memulai Pesanan](https://bantuan.inaproc.id/hc/id-id/articles/8167455220367-Memulai-Pesanan)
- Negotiation:
  - Mandatory in v6 unless rules say otherwise. It covers DPP of products, add-on services and shipping, and the price cannot exceed the displayed price.
  - Each party must respond within 3 days (excluding weekends) until the supplier signs the Surat Pesanan, or the order auto-cancels.
  - Real-time notifications go out by email, SMS and WhatsApp.
  - In the older guide, the buyer edits unit price, shipping, delivery date and notes, then "Kirim Negosiasi". Status shows "Pemesan setuju & Penyedia belum setuju" until both agree and the buyer clicks "Selesaikan Negosiasi". The lowest negotiated unit price is stored as "Harga Terbaik".
  — [INAPROC S&K v6](https://bantuan.inaproc.id/hc/id-id/articles/14461422000143-Syarat-dan-Ketentuan-Katalog-Elektronik-Versi-6-Versi-1-5); [LKPP/Kemlu PP guide](https://ukpbj.kemlu.go.id/uploads/laporan/20231101122627-panduan-pp.pdf); [Malang Kota negosiasi v6 bimtek](https://blp.malangkota.go.id/wp-content/uploads/sites/6/2025/05/negosiasi_katalog_v6.pdf)
- PPK approval and order:
  - After negotiation, "Lanjut Proses Pesanan" sends the order to the PPK's "Daftar Persetujuan PPK".
  - The PPK opens "Lihat Detail" to check product, quantity, price and delivery, then "Buat Pesanan", uploads the SSUK/SSKK contract documents and previews the auto-generated digital Surat Pesanan.
  - The PPK signs via PrivyID. Above Rp200 million the supplier owes a performance bond and the signing SLA extends from 3 to 14 working days.
  — [INAPROC, Panduan PPK Membuat dan Mengelola Pesanan](https://bantuan.inaproc.id/hc/id-id/articles/12048176695823-Panduan-PPK-Membuat-dan-Mengelola-Pesanan)
- Order status sequence (PPK view):
  1. Menunggu Konfirmasi Penyedia
  2. Diproses Penyedia (both signed, e-meterai)
  3. Dalam Pengiriman (with tracking)
  4. Pesanan Tiba
  5. Persiapan Pembayaran (after BAST signed by both)
  6. Dalam Proses Pembayaran (invoice, faktur pajak uploaded)
  7. Selesai

  Suppliers must accept within 3 working days or the order auto-cancels. A Surat Pesanan over Rp5 million needs an e-meterai.
  — [INAPROC, Panduan PPK](https://bantuan.inaproc.id/hc/id-id/articles/12048176695823-Panduan-PPK-Membuat-dan-Mengelola-Pesanan); [INAPROC, Menerima Pesanan](https://bantuan.inaproc.id/hc/id-id/articles/8176727583247-Menerima-Pesanan)
- Receipt:
  - The PPK runs a "Pemeriksaan Tepat Guna" pop-up confirming function, quantity and timeliness, and is advised to video the unboxing.
  - The BAST is generated automatically; the PPK enters the work unit's BAST number and e-signs via BSrE, then the supplier signs.
  - Mismatches go to "Chat Penyedia", then "Pusat Resolusi".
  — [INAPROC, cara menerima pesanan v6](https://bantuan.inaproc.id/hc/id-id/articles/7629851252111-Bagaimana-cara-menerima-pesanan-pada-Katalog-Elektronik-versi-6); [INAPROC Panduan PPK](https://bantuan.inaproc.id/hc/id-id/articles/12048176695823-Panduan-PPK-Membuat-dan-Mengelola-Pesanan)
- Payment:
  - Either one payment or staged termin payments by delivery group.
  - In the regional-government LS mechanism, the supplier uploads the billing documents (faktur pajak or substitute) and the PPK uploads the tax withholding slip (bukti potong), or ticks "tidak memerlukan bukti potong" with a reason, before payment proceeds.
  - Statuses include "Menunggu Kelengkapan Dokumen", "Dibayarkan Sebagian" and "Pembayaran Berhasil". UP payments can use a government card (KKP).
  — [INAPROC, Panduan Pembayaran LS Pemda](https://bantuan.inaproc.id/hc/id-id/articles/11087582760975-Panduan-Pembayaran-Mekanisme-Langsung-LS-Pemerintah-Daerah-Pemda); [INAPROC, setelah pembayaran berhasil](https://bantuan.inaproc.id/hc/id-id/articles/10698009867279-Apa-yang-harus-dilakukan-setelah-pembayaran-pada-Katalog-Elektronik-versi-6-berhasil)
- Scope: Katalog v6 serves ministries, regional governments, BUMN, BUMD and other institutions using APBN/APBD or other budgets. "Toko daring" partners such as Mbizmarket connect via INAPROC SSO and API reporting. — [INAPROC S&K v6](https://bantuan.inaproc.id/hc/id-id/articles/14461422000143-Syarat-dan-Ketentuan-Katalog-Elektronik-Versi-6-Versi-1-5); [Mbizmarket deck](https://www.slideshare.net/slideshow/mbizmarket-indonesia-s-leading-digital-procurement-marketplace-for-government-and-business/287173718)

### Inferences
- BNI branches are not bound to Katalog v6 for small consumables bought from their coop. Still, LKPP's vocabulary and status discipline are familiar to Indonesian institutional buyers and worth copying in a lightweight form:
  - Clear role names (Pemesan / Penyetuju / Keuangan).
  - A cart that flags unbuyable items with reasons.
  - Explicit, time-boxed statuses (Menunggu persetujuan → Diproses → Dikirim → Tiba → Diterima/BAST → Menunggu pembayaran → Selesai).
  - A one-screen goods-received confirmation (quantity OK, condition OK, date) that produces a downloadable receipt document.
- Auto-cancel SLAs (3 working days in LKPP, 7 days at Amazon) stop orders sitting forever in "waiting" states. A coop could adopt a reminder-then-expire rule for unapproved orders.
- "One supplier per checkout" doesn't apply (the coop is the only seller), but per-branch checkout (one delivery address per order, or a deliberate multi-branch split) mirrors LKPP's "Satu Lokasi / Banyak Lokasi" choice.

### Gaps
- The v6-specific comparison feature (max items, UI) was only confirmed from the older v5-era guide (max 3) and from Mbizmarket (max 3). The v6 help text retrieved does not restate it.
- The INAPROC "Persetujuan dan Pembuatan Pesanan" article returned only a title, with no body. Details came from the "Panduan PPK" article instead.
- The current catalog taxonomy (Katalog Nasional / Sektoral / Lokal and etalase structure in v6) was not documented in the retrieved sources.
