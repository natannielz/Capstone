# Visual and interaction design language of Indonesian e-commerce storefront and account pages (mobile-first, trust, speed)

Scope note: storefront, catalog, product, cart, checkout and account pages only. Landing/marketing hero design is excluded. Researched October 2026. Several brand design systems (Tokopedia NEST, Bukalapak Bazaar, Traveloka Momentum) are internal and not publicly documented; where only second-hand or portfolio sources exist this is flagged. Contrast ratios marked "computed" were calculated by the researcher using the WCAG 2.x relative-luminance formula on the hex values cited; they are not taken from a source.

## 1. Published design systems from Indonesian tech companies: principles, components, spacing and type scales

### Takeaway
Indonesian super-apps and marketplaces all run token-based design systems (Tokopedia Unify→NEST, Gojek Asphalt, Bukalapak Bazaar, Traveloka Momentum), but only Gojek Asphalt has a public site, and even it publishes foundations at headline level rather than numeric spacing/type scales. The transferable lessons are their principles (progressive disclosure, cohesion over strict consistency, "purposefully aesthetic so it feels trusted") and the concrete evidence from Tokopedia's product-card redesign: fewer, higher-value data points per card, a shorter card, a prominent discount, and an explicit final price raised clicks and conversion.

### Cited Findings
**Tokopedia (Unify → NEST, 2016–2025)**
- Tokopedia's first design system, Unify ("Merah Putih: One for All"), launched in 2016 as a single design language across 3 platforms and 2 experiences (customer and business); it was rebranded into NEST "by 2020", introducing new typography, icons, colours and motion — [Adhe Kurniawan, Tokopedia NEST case study](https://adhekurniawan.com/case-study/tokopedia-nest/)
- The same case study lists NEST's governance problems after the rebrand: primitive non-scalable tokens, uncontrolled component adoption, poor-quality UI kits, lack of process, and slow bug resolution; designers "often bypassed the system" — [Adhe Kurniawan, Tokopedia NEST case study](https://adhekurniawan.com/case-study/tokopedia-nest/)
- A Tokopedia designer states NEST was announced internally at the first Tokopedia Design Keynote on 18 May 2022, covering "design principles, writing guidelines, and visual direction", with a new typography system, colour palette and supergraphic — [Rachel Ariella K., LinkedIn](https://www.linkedin.com/posts/rchlk_insidetokopedia-tokopedia-productdesign-activity-6937287784017588225-g1XD). Note: this date conflicts with the "by 2020" claim above; the likely reading is the visual refresh began c. 2020 and the system was formally launched in 2022, but neither source confirms that.
- Unify's public-facing goal was "a central location where we house a live inventory of UI components, brand guidelines, brand assets, code snippets, developer guidelines" — [unify-website repo (GitHub)](https://github.com/stevanus-christopel/unify-website)
- Tokopedia's primary brand green is #03AC0E (logo-extracted, closest Pantone 2423 C) — [LogoLabs](https://www.logolabs.org/logos/tokopedia)
- Tokopedia product-card redesign (designer portfolio; undated, post-2023 by context): research found many card details "not very important to users", e.g. halal status, weight, the Add to Cart button and promotional labels; pain points were too much unnecessary information, lack of guidance, hard to maintain, and "not engaging" — [Galuh Akhdandika, New Tokopedia Product Card](https://galuh-akhdandika.framer.website/tokopedia-product-card)
- Outputs of that redesign: discount made more prominent and moved to a more visible spot; an automatically calculated final price ("users can exactly know the exact price they'll pay"); a "selling point tag" for good-quality products; card height reduced 17% so more cards show per screen; one guideline governing 50 product-card use cases — [Galuh Akhdandika](https://galuh-akhdandika.framer.website/tokopedia-product-card)
- Reported A/B results: clicks +3.05% (350.98 vs 361.67), paid-order CVR 9.95% → 10.06% (+1.17%), TIV +3.76%; carousel module CVR to add-to-cart +3.46% and paid order +3.80%; tech reported 71–80% app-performance improvement from reduced lag — [Galuh Akhdandika](https://galuh-akhdandika.framer.website/tokopedia-product-card) (self-reported portfolio figures, not independently verified)
- Tokopedia 2024–2026 context: TikTok Shop's transition into Tokopedia completed 27 March 2024; the Shop front-end then showed "dominan warna hijaunya Tokopedia" — [Kompas](https://money.kompas.com/read/2024/04/03/144338126/transisi-tiktok-shop-rampung-kini-jadi-shop-tokopedia); the logo changed to "TikTok Shop by Tokopedia" in March 2025, with app appearance to change gradually — [detikInet](https://inet.detik.com/business/d-7804298/wajah-baru-tiktok-shop-ini-sederet-keistimewaannya); full integration took effect 10 June 2025 and users complained of confusing navigation — [Akurat](https://akurat.co/umkm/795509/tokopedia-x-tiktok-shop-terintegrasi-umkm-dan-pengguna-terdampak)

**Gojek Asphalt (Asphalt Aloha)**
- Asphalt went from "17 products, 300 sketch files, and 1,200 artboards" to 200 unique components used across 700+ screens in 14 months; GO-RIDE was the "development mule"; takeaways: start small, involve engineers early, document from day one — [Gojek Product + Tech, Medium (2019)](https://medium.com/gojekengineering/of-dreams-deadlines-and-a-design-system-6ad7c3d1b17f)
- Asphalt Aloha foundations are tokens for colour (accessible, light/dark switchable, themable per user type), typography (Maison Neue, "bold and playful"), motion, spacing ("definite and scalable spacing values, instead of having to guess"), and shadows (elevation via shadow tokens; light source perpendicular to the screen) — [Asphalt Aloha Foundations](https://asphalt.gojek.io/pages/foundations.html)
- Principle #1 is Consistency ("We design with the system in mind and never as individual flows") — [Asphalt Aloha Principles](https://asphalt.gojek.io/pages/principles.html); the public site exposes Components, Tokens, Typography and Accessibility sections — [Asphalt Aloha](https://asphalt.gojek.io/)

**Bukalapak Bazaar**
- Bazaar launched as an internal documentation site in June 2019 after 6 months, serving 50+ teams; lessons included one source of truth, a centralised team, and "Cohesive not consistent": teams may build one-off "snowflake" components that are periodically reviewed and either replaced or promoted — [Budi Tanrim, Bukalapak Design, Medium](https://medium.com/bukalapakproductdesign/what-i-learned-from-6-months-leading-a-design-system-for-a-leading-e-commerce-company-cdd3ce9c7156)
- Bukalapak product-experience principles: (1) progressive disclosure ("it's okay to hide the secondary information"), (2) users' needs first, (3) "Be cohesive, so users only have to learn once", (4) "Purposefully aesthetic, so it feels polished and trusted ... Default to standard and innovate only when it's worth it" — [Bukalapak Design, Medium](https://medium.com/bukalapakproductdesign/evolving-product-design-principles-at-bukalapak-366ab45ae5a6)

**Traveloka Momentum**
- Momentum splits UI into Core UI (button, typography, card), Future UI (common but not yet standardised; in 2020 this included modal and loading skeleton view) and Product UI (domain-owned compositions); a design spec rests on seven subjects: guidelines & principles, UI elements, tokens, colours, icons, typography, accessibility — [Traveloka Engineering, Medium (2020)](https://medium.com/traveloka-engineering/the-engineering-of-a-design-system-cdede29c58b6)
- Traveloka put design tokens in JavaScript as a code-based design system shared by designers and engineers — [Traveloka Design, Medium (2018)](https://medium.com/traveloka-design/when-design-meets-engineering-at-traveloka-dbcc11f46f03)
- A community list records Momentum (traveloka.design) as private, Asphalt as public, and Unify/Bazaar as public-but-draft with no URL — [design-systems-in-indonesia (GitHub)](https://github.com/daengdoang/design-systems-in-indonesia)

**Shopee brand colours (for comparison)**
- Shopee palette: Orange #EE4D2D, Red #D0011B, Blue #113366 — [ColorCodeHub](https://colorcodehub.com/brand/shopee) (third-party; not an official Shopee guideline)

### Inferences
- Transferable principle set for a B2B cooperative shop: progressive disclosure on cards and PDP, cohesion across catalog/cart/checkout/account, and "default to standard, innovate only when worth it" (Bukalapak), plus a token-first foundation (colour, type, spacing, elevation, motion) as in Asphalt.
- Product card rule (from the Tokopedia redesign evidence): image, 2-line name, final price (bold, the single most prominent text), optional strikethrough original price plus discount badge, one benefit/trust line (for example delivery promise or stock), and nothing else by default. Drop low-value metadata (weight, certifications) and the inline add-to-cart button from list cards unless the buyer is doing rapid repeat ordering. Keep the card as short as possible to increase products per viewport.
- For a B2B shop, the Tokopedia "final price" lesson maps to showing the member/contract price after any tier discount, not a list price that changes at checkout.
- Treat skeletons, bottom sheets and toasts as first-class system components (Momentum promoted loading skeletons into its system), not page-specific code.

### Gaps
- No public numeric spacing scale, type scale, grid or component specs were found for Tokopedia NEST, Bukalapak Bazaar or Traveloka Momentum; Asphalt's spacing/typography/motion detail pages returned 404. Exact Indonesian design-system values (for example 4/8 px base, body size) could not be verified.
- Tokopedia's NEST typeface could not be confirmed from a primary source. Creative Sauce says its Open Sauce superfamily was built "for a prominent online portal", compact and optimised for small on-screen text with ink traps ([Creative Sauce](https://www.creativesauce.com/font-design)), but it does not name Tokopedia.
- No published 2024–2026 redesign documentation from Shopee, Lazada or Blibli was found; only Tokopedia/TikTok integration news.
- Shopify Polaris pages could not be fetched (TLS error); Polaris is in any case admin-focused, not storefront.

## 2. Mobile-first patterns and performance expectations on Indonesian networks and devices

### Takeaway
Indonesian shoppers are overwhelmingly on Android, increasingly on entry-level phones, and on mobile networks whose median download is roughly 29–51 Mbps depending on the dataset (with a long slow tail). Tokopedia's own case studies show that cutting JavaScript, prioritising the LCP image, lazy-loading everything else, and serving smaller adaptive images produced measurable CTR, conversion and session gains. Interaction-wise: square 1:1 product imagery, sticky bottom action bars, bottom sheets for variants/filters with an explicit close button, 44–48 px tap targets, and visible applied-filter chips.

### Cited Findings
**Network and device context**
- Ookla (via DataReportal): median mobile download in Indonesia was 29.06 Mbps at the start of 2025, up 18.5% year on year; fixed was 32.05 Mbps — [DataReportal Digital 2025: Indonesia](https://datareportal.com/reports/digital-2025-indonesia)
- Speedtest Global Index: 42.85 Mbps mobile median in July 2025 (rank 86) — [detikInet](https://inet.detik.com/telecommunication/d-8076356/internet-indonesia-masih-paling-lambat-di-asean-ini-ranking-terbarunya); 50.77 Mbps in November 2025, 7th in Southeast Asia and 73rd of 103 countries — [Bisnis.com](https://teknologi.bisnis.com/read/20251222/101/1938772/kecepatan-internet-seluler-ri-november-2025-lebih-cepat-dari-kamboja-dan-laos). Ookla Speedtest Intelligence shows the national median rising from 17.54 Mbps (Q2 2022) to 30.5 Mbps (Q2 2025) — [Rethink Research](https://rethinkresearch.biz/?p=60522). These datasets differ in method and are not directly comparable.
- 212 million internet users (74.6% penetration) in January 2025; 356 million cellular connections (125% of population); 96.4% of connections are 3G/4G/5G "broadband" — [DataReportal](https://datareportal.com/reports/digital-2025-indonesia)
- IDC: Indonesia's smartphone market grew 15.5% to nearly 40 million units in 2024, driven mainly by the ultra-low-end segment — [IDC](https://www.idc.com/?p=103955); the <US$200 tier grew 17.8% in 1Q24, led by Transsion — [IDC via I-Connect007](https://mail.iconnect007.com/article/140783/idc-indonesias-smartphone-market-starts-the-year-with-a-strong-274-growth-in-1q24/140780/ein)
- Counterpoint Q2 2025: shipments fell ~7% YoY; demand concentrated in entry level (<$150, +3% YoY) while mid and premium tiers declined sharply — [Counterpoint](https://counterpointresearch.com/cn/insights/indonesia-smartphone-market-q2-2025)
- Android accounted for over 90% of mobile OS share in Indonesia (Q2 2025) — [Statista](https://www.statista.com/statistics/262205/market-share-held-by-mobile-operating-systems-in-indonesia)

**Performance evidence from Indonesian e-commerce**
- Core Web Vitals "good" thresholds: LCP ≤ 2.5 s, INP ≤ 200 ms, CLS ≤ 0.1, measured at the 75th percentile, segmented by mobile/desktop — [web.dev Web Vitals](https://web.dev/articles/vitals)
- Tokopedia's performance programme gave +35% CTR, +8% CVR and a 4 s TTI improvement; techniques: a script controller to load third-party scripts selectively, lighter libraries, code splitting, adaptive loading ("only loading high-quality images for devices on fast networks and using lower-quality images for devices on slow networks"), lazy-loading below-the-fold images, deferring non-critical JS, a Svelte "lite" homepage for first-time visitors, and a CI bundle-size budget that fails the build — [web.dev Tokopedia case study](https://web.dev/case-studies/tokopedia)
- Tokopedia improved LCP by 55% and saw 23% better average session duration by server-rendering and preloading the LCP element and compressing images (WebP, lazy-loading non-critical images) — [web.dev business impact of Core Web Vitals](https://web.dev/case-studies/vitals-business-impact)
- Tokopedia's mobile homepage LCP was 4.1 s ("poor"); serving a 500 px WebP instead of an 800 px JPG saved 50 kB; lazy-loading competing images plus preload improved LCP ~0.7 s; rendering less on the server improved ~0.4 s; full client-side rendering made LCP much worse — [Tokopedia Engineering, Medium (2020)](https://medium.com/tokopedia-engineering/getting-content-painted-under-2-seconds-on-the-mobile-web-7b3bbaca32cb)
- Tokopedia's lite landing page shipped ~37 kB gzip of above-the-fold JS versus ~320 kB for the main React app, interactive in about 4 s — [Tokopedia Engineering, Medium (2019)](https://medium.com/tokopedia-engineering/achieving-90-mobile-web-performance-at-tokopedia-23f557d98d5)
- Tokopedia stated "a lot of users visiting the mobile site have low-end phones with 3G network quality"; on sale pages it painted only ~8 components initially (intersection-based), requested images sized to the slot (≈250 px per banner in 2-up rows, 430 px for full width; ~30% less image weight), and lazy-loaded share button, toaster, back-to-top etc. (chunk 21 kB → 14 kB) — [Tokopedia Engineering, Medium (2020)](https://medium.com/tokopedia-engineering/a-tokopedia-mobile-web-performance-case-study-48ee5c6e4d5c)
- Tokopedia's AMP+PWA rollout stressed caching, offline fallback pages and auto-refresh on reconnect for "predominantly low-end smartphones", with reported 5x conversion, 65% faster loads and 60% lower bounce — [AMP case study PDF](https://amp.dev/static/files/case-studies/tokopedia.pdf)

**Imagery**
- Tokopedia (TikTok Shop by Tokopedia) seller guidance: up to 9 square images; 1:1 ratio; minimum 600 × 600 px; max 10 MB — [Tokopedia Seller Center](https://seller-id.tokopedia.com/university/essay?knowledge_id=10000818&lang=en); primary image on a solid (white recommended) background, product filling ≥60% of the frame, whole product visible, no stickers/watermarks obscuring it — [Tokopedia Seller Center](https://seller-id.tokopedia.com/university/essay?knowledge_id=7065663894374161)
- Shopee: cover photo ideally on white, product ≥70% of the photo, no text or graphics on the cover, minimum 500 × 500 px — [Shopee Seller Education](https://seller.shopee.co.id/edu/article/350); ads guidance asks for 1:1 square photos ≥1024 × 1024 px with a white cover background — [Shopee Iklanku FAQ](https://iklan.shopee.co.id/learn/faq/57/5); Shopee added a 3:4 upload option in 2024 — [Shopee Seller Education](https://seller.shopee.co.id/edu/article/17330)

**Interaction patterns and touch**
- Baymard mobile research (2026 summary): 42% of mobile sites don't combine product variations into one list item, which clutters lists and made a participant suspicious; 27% lack a visible search submit button next to the field; 42% lack a "View All" option at each catalog level; 66% don't show an "Applied Filters" overview above the product list — [Baymard Mobile UX Trends 2026](https://baymard.com/blog/mobile-ux-ecommerce)
- Bottom sheets: include a visible Close (X) button rather than relying only on the grab handle, because the swipe gesture is ambiguous (can trigger the OS notification panel) and inaccessible to users without precise dexterity — [NN/g Bottom Sheets](https://www.nngroup.com/articles/bottom-sheet/)
- Shopee's documented buy flow opens a variant chooser after tapping the CTA ("pilih opsi variasi ... (jika ada)") before confirming "Masukkan Keranjang" or "Beli Sekarang" — [Shopee Help Center](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee)
- Tap targets: Apple ≥44 × 44 pt — [Apple UI Design Dos and Don'ts](https://developer.apple.com/design/tips/); Android ≥48 × 48 dp, "larger is even better" — [Android Developers](https://developer.android.com/guide/topics/ui/accessibility/apps); WCAG 2.2 SC 2.5.8 (AA) ≥24 × 24 CSS px or equivalent spacing — [W3C](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html)
- Sticky add-to-cart evidence is mixed: vendor A/B tests report both conversion lifts and drops; no Baymard primary source could be retrieved (paywalled) — [cleancommit.io sticky mobile ATC test](https://cleancommit.io/ab-tests/sticky-mobile-add-to-cart-button/); [The Good](https://thegood.com/insights/sticky-add-to-cart/)

### Inferences
- Performance budget for Unit Toko catalog/PDP on mobile: LCP ≤ 2.5 s at p75 on a mid/low Android over ~4G; above-the-fold JS in the tens of kB, not hundreds; first product images server-rendered with `fetchpriority`/preload; everything below the fold lazy-loaded; product thumbnails served at slot width (≈ 180–250 px wide for a 2-column grid on a 360–412 px screen, WebP/AVIF), never full-size originals. Reserve image boxes with fixed `aspect-ratio: 1/1` to hold CLS ≤ 0.1.
- Grid and density: 2-column product grid on phones (cards ~ (viewport − 2×gutter − gap)/2), 16 px side gutter, 8 px grid gaps; 1:1 square images on white so B2B catalog photos look uniform; 3–4 columns from tablet up. This matches both marketplaces' 1:1 image norms.
- Thumb zone: put the primary purchase action in a sticky bottom bar on PDP and cart (price/total on the left, primary CTA right, secondary CTA as outline). Keep it ≥48 px tall with ≥8 px between buttons. Because sticky-ATC evidence is mixed, measure it rather than assume uplift.
- Use a modal bottom sheet for variant/unit/quantity selection and for filters, each with a visible "Tutup"/X, a sticky "Terapkan"/"Simpan" footer, and Android back-button dismissal. Show applied filters as removable chips above results, and collapse variants into one list item.
- Offline/poor-network resilience (cached shell, offline fallback, auto-retry on reconnect) is a documented Tokopedia pattern and fits cooperative members on patchy rural connections.

### Gaps
- No Indonesia-specific CrUX (Chrome UX Report) pass-rate or median page-weight statistics for e-commerce were found.
- No Indonesia-specific research on minimum comfortable font size for mid-range Android users was found; only platform guidance (Apple ≥11 pt) applies.
- No primary source documented exact Shopee/Tokopedia sticky-bar heights or grid gutters; the values above are inferences.
- Opensignal's 2025 Indonesia report returned HTTP 403.

## 3. Trust signals appropriate for a B2B shop

### Takeaway
Indonesian buyers lean heavily on reviews, discounts, free shipping, an easy checkout and clear payment options. Marketplaces turn this into visible badges (Official Store / Shopee Mall, Star Seller) and guarantees. For a cooperative B2B shop, the equivalent signals are a verified-entity badge, a transparent final price, a delivery promise, a clear return/complaint policy, and a one-tap WhatsApp contact. WhatsApp is the country's most-used social platform.

### Cited Findings
- DataReportal (January 2024, via GoodStats): purchase drivers among Indonesian internet users are coupons/discounts 52.3%, customer reviews 48.2%, free shipping 47.4%, easy checkout 45.5%, likes/comments 32.7%, cash on delivery 31.2% — [GoodStats](https://data.goodstats.id/statistic/6-faktor-ini-mendorong-pembelian-dalam-proses-belanja-online-di-indonesia-nxDZ9)
- Litbang Kompas poll (May 2025, n=514): 98% read reviews before buying online (42.3% always); 86.5% say reviews influence their decision; a large majority report having been misled by reviews (the article gives 84.3% but also says "three in five", an internal inconsistency) — [Kompas.id](https://www.kompas.id/artikel/mayoritas-konsumen-toko-daring-merasa-pernah-tertipu-ulasan-2)
- GMO Research (April 2025, n=881): 96.1% shopped online in the past 12 months; drivers were convenience 67.4%, promotions 67.4%, better prices 63.9%; among non-users, 41.2% would be persuaded by safer payment such as COD, and 17.6% distrust online payment — [GMO Research & AI](https://gmo-research.ai/en/resources/studies/2025-report-online-shopping-trends-id)
- Populix (Nov 2023 survey, published 2024): online-shopping drivers were practicality 67%, ease of comparing prices 66%, variety of payment methods 60%, easy returns 25% — [Populix](https://populix.co/articles/ritel-offline-dan-online-akomodasi-kebiasaan-belanja-konsumen-indonesia-yang-beragam/)
- Academic study of Indonesian Shopee users (n=290): information quality, reputation (reviews, ratings, seller image), return-policy leniency and social-commerce constructs (reviews, ratings, live chat) significantly affect trust and purchase intention; COD did not — [IJEBMR 2025](https://ijebmr.com/uploads/pdf/archivepdf/2025/IJEBMR_1519.pdf)
- Shopee Mall denotes official stores under Shopee supervision, with brand verification and stricter service standards — [BFI](https://www.bfi.co.id/en/blog/shopee-mall-adalah); [Shopappy](https://shopappy.com/ecommerce/shopee/shopee-mall); Star Seller marks sellers considered top-performing and trustworthy (sources conflict on whether companies are eligible) — [UKM Indonesia](https://ukmindonesia.id/baca-deskripsi-posts/bagaimana-cara-menjadi-star-seller-dan-shopee-mall-di-shopee)
- Shopee's own safety tips tell buyers to transact only via the official app/site and to check product name, description, images, store rating and review history — [Shopee Help Center](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee)
- WhatsApp was the most-used social media app in Indonesia in February 2025 and Q2 2025 (We Are Social data) — [Databoks early 2025](https://databoks.katadata.co.id/en/technology-telecommunications/statistics/67ca734b43927/list-of-most-used-social-media-in-indonesia-early-2025); [Databoks Q2 2025](https://databoks.katadata.co.id/en/technology-telecommunications/statistics/69959bb37aa8b/10-most-used-social-media-platforms-in-indonesia-in-q2-2025)
- Tokopedia's product-card redesign added a "selling point tag" so users can easily identify good-quality products, and an auto-calculated final price — [Galuh Akhdandika](https://galuh-akhdandika.framer.website/tokopedia-product-card)
- Bukalapak principle: "Purposefully aesthetic, so it feels polished and trusted" — [Bukalapak Design](https://medium.com/bukalapakproductdesign/evolving-product-design-principles-at-bukalapak-366ab45ae5a6)

### Inferences
- B2B trust kit: (a) a verified/official badge next to the cooperative name on PDP, cart seller row and invoice (equivalent of "Official Store"/"Shopee Mall"), with a tap-to-explain sheet saying what is verified; (b) the final member price shown on the card and PDP, with the same number in the cart; (c) a delivery promise line ("Estimasi tiba 2–3 hari kerja" or the cooperative's real SLA) plus delivery area; (d) a return/complaint policy link on PDP and checkout; (e) a persistent "Tanya via WhatsApp" entry (click-to-chat with a prefilled product/order reference) on PDP, order detail and error states; (f) transaction proof: order number, status timeline and downloadable invoice in the account area.
- Reviews matter but are distrusted, so in a closed B2B cooperative, verified-buyer labels and repeat-order counts ("Dipesan 120× oleh anggota") are likely more credible than star averages alone.
- Payment-option breadth (virtual account, e-wallet, transfer, and invoice/term payment for members) is itself a trust signal, so show accepted methods early (PDP or cart), not only at the payment step.
- Trust comes from restraint as well: avoid marketplace-style promo clutter, which Tokopedia's own research found reduced card clarity.

### Gaps
- No official Tokopedia help article defining Official Store / Power Merchant (Pro) badges could be retrieved (404).
- No research found on trust signals specifically for B2B or cooperative e-commerce in Indonesia; all evidence is B2C.
- The exact share of Indonesian internet users on WhatsApp (percentage) was not in retrieved text; only its #1 ranking.

## 4. Loading, empty and error state conventions

### Takeaway
Use content-shaped skeletons for full-page or list loads under ~10 s, spinners only for small modules, and progress indicators past 10 s. Never show a blank frame. Empty states should explain the status, teach, and offer a direct next action. Errors must sit next to their source, use redundant (not colour-only) cues, and give constructive, polite advice. Indonesian convention is friendly, informal "kamu" copy (Tokopedia) or polite "Anda" (Shopee help). Motion stays short: 100–300 ms for most UI transitions.

### Cited Findings
- Skeletons and spinners both suit waits under 10 s: spinners best for a single module, skeletons (except frame-display ones) better when the full screen loads; progress bars for anything over 10 s — [NN/g Skeleton Screens 101](https://www.nngroup.com/articles/skeleton-screens/)
- Do not use frame-display skeletons (header/footer only, empty body); users may assume the page is broken and abandon — [NN/g](https://www.nngroup.com/articles/skeleton-screens/)
- Skeleton benefits: prevent "site isn't working" assumptions, create the illusion of a shorter wait, and reduce cognitive load by previewing layout — [NN/g](https://www.nngroup.com/articles/skeleton-screens/)
- Traveloka listed "loading skeleton view" as a Future UI component on track for its core system — [Traveloka Engineering](https://medium.com/traveloka-engineering/the-engineering-of-a-design-system-cdede29c58b6)
- Empty states: (1) communicate system status, (2) provide learning cues, (3) provide direct pathways (link straight to the action that fills the state) — [NN/g Empty States](https://www.nngroup.com/articles/empty-state-interface-design/)
- Error messages: show close to the error's source; use noticeable, redundant and accessible indicators (bold, high-contrast, red is conventional) but never colour or animation alone, since ~350 million people have a colour-vision deficiency — [NN/g Error-Message Guidelines](https://www.nngroup.com/articles/error-message-guidelines/)
- Tokopedia's PWA approach used offline fallback pages and auto-refresh when connectivity returns — [AMP case study](https://amp.dev/static/files/case-studies/tokopedia.pdf)
- Tokopedia lazy-loads non-essential UI such as the toaster and an "internet-connection" component on demand — [Tokopedia Engineering](https://medium.com/tokopedia-engineering/a-tokopedia-mobile-web-performance-case-study-48ee5c6e4d5c)
- Motion: most UI animations 100–500 ms; ~100 ms for simple feedback (checkbox, toggle); 200–300 ms for larger changes such as a modal entering; at 500 ms they "start to feel like a real drag"; 100–400 ms suits most cases — [NN/g Animation Duration](https://www.nngroup.com/articles/animation-duration/)
- Asphalt treats motion as a foundation token alongside colour, type, spacing and shadow — [Asphalt Foundations](https://asphalt.gojek.io/pages/foundations.html)
- Indonesian UX-writing advice (practitioner blog): replace stern red errors with friendly language, e.g. "Wah, emailnya belum diisi nih. Coba ketik alamat email kamu dulu, ya.", combined with colour/icon on the field — [buataplikasi.id (2026)](https://buataplikasi.id/ide-mendesain-alur-pengguna/) (low-authority source)

### Inferences
- Catalog/search: render a 2-column grid of skeleton cards (square image block + 2 text bars + price bar) matching the real card dimensions, so there is no layout shift when data arrives. PDP: skeleton the image, title, price and sticky bar. Use a subtle shimmer at most, and disable it under `prefers-reduced-motion`.
- Empty search, Bahasa Indonesia pattern: illustration or icon + title "Barang tidak ditemukan" + body "Coba kata kunci lain atau cek ejaan." + actions "Hapus filter" / "Lihat semua produk" + optional "Tanya via WhatsApp". Empty cart: "Keranjangmu masih kosong" + "Mulai belanja". Empty orders: "Belum ada pesanan" + link to the catalog.
- Network/server error: inline state in the content area (not a blank page) with a plain cause ("Koneksi terputus" / "Halaman gagal dimuat"), a primary "Coba lagi" button, and auto-retry on the `online` event. Keep the cart and form inputs intact across retries.
- Form errors: inline under the field, red text plus an icon and an `aria-describedby` link, polite and specific ("Nomor HP minimal 10 digit"), with no blame.
- Toasts for non-blocking confirmations ("Berhasil ditambahkan ke keranjang" + "Lihat" action), roughly 3–4 s, placed above the sticky bottom bar so they don't cover the CTA.
- Motion tokens: 100 ms (feedback), 200–250 ms (bottom sheet/modal in), 150–200 ms (out), standard ease-out on enter and ease-in on exit; no decorative motion on low-end devices.

### Gaps
- No primary-source screenshots or copy decks of current Tokopedia/Shopee/Lazada/Blibli empty or error states (for example exact "Barang tidak ditemukan" wording) were retrievable; the Bahasa strings above are proposed, not verified platform copy.
- No published Indonesian design-system motion tokens (durations/easings) were found.

## 5. Accessibility (WCAG 2.2 contrast for price/status colours) and Bahasa Indonesia microcopy

### Takeaway
Both dominant marketplace brand colours fail WCAG AA 4.5:1 for normal-size text on white (Tokopedia green #03AC0E ≈ 3.0:1, Shopee orange #EE4D2D ≈ 3.7:1, computed). So a cooperative shop should keep its bright brand hue for large CTAs/fills and use a darker shade for price, links and status text. Microcopy conventions are well established: Tokopedia uses "+ Keranjang" / "Beli Langsung" with informal "kamu"; Shopee uses "Masukkan Keranjang" / "Beli Sekarang" / "Buat Pesanan" with formal "Anda" in help content.

### Cited Findings
**Contrast and target rules**
- Shopify theme guidance (mirrors WCAG AA): text under 24 px regular / 18.5 px bold needs 4.5:1; larger text 3:1; icons 3:1; input borders 3:1; colour must not be the only indicator — [Shopify theme accessibility](https://shopify.dev/docs/storefronts/themes/best-practices/accessibility)
- WCAG 2.2 SC 2.5.8 Target Size (Minimum), Level AA: ≥24 × 24 CSS px, with spacing, equivalent, inline, user-agent and essential exceptions — [W3C Understanding 2.5.8](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html)
- Platform minimums: text ≥11 pt; tap targets ≥44 × 44 pt (Apple) — [Apple](https://developer.apple.com/design/tips/); ≥48 × 48 dp (Android) — [Android Developers](https://developer.android.com/guide/topics/ui/accessibility/apps)
- Brand hex sources: Tokopedia #03AC0E — [LogoLabs](https://www.logolabs.org/logos/tokopedia); Shopee #EE4D2D / #D0011B / #113366 — [ColorCodeHub](https://colorcodehub.com/brand/shopee)
- Computed contrast on white (#FFFFFF) / on light grey (#F5F5F5): Tokopedia green #03AC0E 3.04 / 2.79; Shopee orange #EE4D2D 3.66 / 3.36; Shopee red #D0011B 5.67 / 5.20; Tailwind-style green-700 #15803D 5.02 / 4.60; red-600 #DC2626 4.83 / 4.43; red-700 #B91C1C 6.47 / 5.93; orange-700 #C2410C 5.18 / 4.75; amber-500 #F59E0B 2.15 / 1.97; amber-700 #B45309 5.02 / 4.61; slate-500 #64748B 4.76 / 4.36; gray-400 #9CA3AF 2.54 / 2.33 (researcher calculation, WCAG relative-luminance formula)
- Computed: darkening Tokopedia green to about #028A0B, or Shopee orange to about #D14428, reaches ≥4.5:1 on white (researcher calculation)
- A 2025 bootcamp UX critique of the Tokopedia app flagged delivery-filter chips whose colour contrast was "too light", including the selected state — [Medium, Alexa Aman](https://medium.com/@alexa.aman10/ux-writing-case-study-tokopedia-mobile-copy-improvement-5b0dbd041228) (low-authority source)
- A Tokopedia designer portfolio lists a "Tokopedia Color Blindness" project ("color-blind friendly features") headlined "49.95% CVR"; the metric is not explained — [Adhe Kurniawan portfolio](https://adhekurniawan.com/tokopedia-nest)

**Bahasa Indonesia microcopy**
- Tokopedia buy flow: "klik +Keranjang atau Beli Langsung" → "Cek Keranjang" → "Beli" → "Pilih Pembayaran" → "Bayar"; addresses users as "Toppers" and "kamu"; max 400 quantity per checkout — [Tokopedia Care](https://www.tokopedia.com/help/article/bagaimana-cara-belanja-di-tokopedia)
- Tokopedia "Beli Langsung" skips the cart using saved preferences (address, courier duration, payment), up to 5 preferences, single product/store only — [Tokopedia Care](https://www.tokopedia.com/help/article/beli-langsung)
- Shopee buy flow: "Beli Sekarang" → choose variation (if any) → "Beli Sekarang" → shipping → voucher/coins → payment → "Buat Pesanan"; or "Tambahkan Keranjang" → variation → "Masukkan Keranjang" → "Buka Keranjang" → "Checkout"; help copy uses "Anda"; max 40 products per checkout — [Shopee Help Center](https://help.shopee.co.id/portal/4/article/71961-[Baru-di-Shopee]-Bagaimana-cara-melakukan-checkout-di-Shopee)
- Shopee's own blog uses informal "kamu" and "Sobat Shopee" with the same labels ("Masukkan Keranjang", "Beli Sekarang", "Buat Pesanan") — [Inspirasi Shopee](https://shopee.co.id/inspirasi-shopee/ingin-tahu-cara-belanja-di-shopee-yuk-ikuti-langkahnya/)
- Baymard: provide an explicit "View All" at each catalog level (42% of mobile sites don't) — [Baymard](https://baymard.com/blog/mobile-ux-ecommerce) (supports a "Lihat Semua" link on every category/section)

### Inferences
- Colour roles for Unit Toko (rule set):
  - Brand/primary CTA: a filled button with white text needs ≥4.5:1 between white and the fill for the 14–16 px label, so use a darkened brand shade (for a green brand, ~#15803D / #028A0B or darker), not a neon brand green.
  - Price: neutral near-black (#111–#1F2937), bold, 16–18 px on cards and 20–24 px on PDP. Reserve red only for discount badges and strikethrough context, and use a shade ≥4.5:1 (for example #B91C1C) if red price text is used at all. Shopee-style orange price text at #EE4D2D fails AA at body sizes.
  - Status chips (order/stock): text ≥4.5:1 on its tinted background, plus an icon or word, never colour alone. Success = dark green, warning = amber-700 text on amber-50 (never amber-500 text), danger = red-700, info = blue-700, neutral = slate-600+.
  - Secondary/meta text: no lighter than ~#64748B on white (4.76:1); avoid gray-400 for anything users must read (sold count, unit, stock).
- Type scale for mid-range Android (inference; no Indonesia-specific study found): body 14–16 px, product name 13–14 px (2-line clamp), meta 12 px minimum (bold or high-contrast), price 16 px+ bold, section headers 16–18 px semibold. Respect system font scaling: no fixed-height text containers, and test at 130% font scale.
- Microcopy conventions to adopt (choose one register and keep it consistent):
  - Primary PDP actions: "+ Keranjang" (or "Masukkan Keranjang") as secondary, and "Beli Sekarang" (or "Beli Langsung" if it skips the cart) as primary.
  - Cart: "Checkout" or "Beli (n)" with item count; checkout final: "Buat Pesanan"; payment: "Bayar".
  - Navigation: "Lihat Semua", "Kembali", "Simpan", "Terapkan", "Atur Ulang"/"Reset", "Tutup".
  - States: "Barang tidak ditemukan", "Coba lagi", "Stok habis", "Sisa 3", "Diproses", "Dikirim", "Selesai", "Dibatalkan".
  - Register: B2B cooperative members are business buyers, so the Shopee-help-style polite "Anda" is defensible; Tokopedia/Gojek-style "kamu" reads friendlier. Pick one and apply it everywhere.
- Use title case for short button labels (as both marketplaces do) and sentence case for body/helper text.

### Gaps
- Lazada and Blibli CTA labels (for example "Masukkan Troli", "+ Keranjang") could not be verified from help-centre sources.
- No published Indonesian-language UX-writing guideline (for example Tokopedia NEST writing guidelines) is publicly available; NEST's writing guidelines are referenced but internal.
- No official accessible-colour tokens from Tokopedia/Shopee were found; contrast figures rely on third-party brand hex values and the researcher's calculation.
- No Indonesia-specific accessibility audits of e-commerce sites (for example WebAIM-style data for .co.id) were found.
