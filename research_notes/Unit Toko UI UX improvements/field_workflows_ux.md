# Field and Document-Heavy Workflow UX for Unit Toko (mobile web, as of Oct 2026)

Scope: payment-proof upload + Tesseract OCR review, mobile-web camera capture, barcode scanning (camera + HID wedge), warehouse receiving/picking/dispatch (Surat Jalan), e-POD, stock opname. Context: Next.js web app, desktop + mobile browsers, no native app. Research date: 2026-10-19. Browser-support facts are from caniuse/MDN snapshots fetched on that date. Vendor pages are labelled as vendor claims.

---

## 1. Document upload UX (picker vs drag-drop vs camera, formats, compression, progress/retry, previews, replace, privacy)

### Takeaway
Start with a plain `<input type="file">` and add a large drop zone that is always visible on desktop. On mobile, offer "Take photo" and "Choose file / screenshot" as separate actions. Name accepted types and size limits in plain words, both before upload and in errors. Compress and orient images on the client, and make uploads survive flaky networks: retry, resumable upload for large files, IndexedDB recovery. Watch two iOS Safari quirks: the `accept` list controls transcoding (HEIC to JPEG), and Background Sync does not exist.

### Cited Findings
- web.dev says to start with a simple file input and progressively enhance it, for example by adding a drag-and-drop target that reads `e.dataTransfer.files`. Drag-and-drop is "well-supported by the major browsers." — [web.dev, Capturing an image from the user](https://web.dev/articles/media-capturing-images)
- The GOV.UK Design System's improved File upload component made the drop zone bigger, always visible, and more responsive to interaction. Teams can change the button text and the "No file chosen" text, but should keep them short for screen-reader users. — [GOV.UK Design System, File upload](https://design-system.service.gov.uk/components/file-upload/)
- GOV.UK error-message patterns: wrong type gives "The selected file must be a [list of file types]" (e.g. "must be a JPG, BMP, PNG, TIF or PDF"). Too big gives "The selected file must be smaller than [largest file size]" (e.g. "smaller than 2MB"). — [GOV.UK Design System, File upload](https://design-system.service.gov.uk/components/file-upload/)
- iOS Safari transcodes picked images to the first non-wildcard type in `accept`. With `accept="image/jpeg, image/png, image/gif"`, a user's HEIC photo is transcoded to JPEG. WebKit bug 292350 (status RESOLVED FIXED) aligned the drag-and-drop path with the file chooser. — [WebKit Bugzilla 292350](https://bugs.webkit.org/show_bug.cgi?id=292350)
- Developers report that since Safari 17, `accept="image/*,image/heic"` caused PNG/JPG files to be converted to HEIC before upload. — [Apple Developer Forums thread 743049](https://developer.apple.com/forums/thread/743049)
- CSS `image-orientation: from-image` is the default initial value: browsers apply EXIF rotation when displaying images. It has been Baseline (widely available) since April 2020. — [MDN, image-orientation](https://developer.mozilla.org/en-US/docs/Web/CSS/image-orientation)
- `@uppy/compressor` optimizes JPEG/PNG and other browser-supported images before upload, "saving up to 60% in size (roughly 18 MB for 10 images)", using Compressor.js. Default `quality` is 0.6 (applies to JPEG/WebP only). It shows a message with the bytes saved and needs no user action. — [Uppy docs, Compressor](https://uppy.io/docs/compressor/)
- `@uppy/golden-retriever` restores selected files after a crash or accidental tab close. It stores metadata and state in IndexedDB, plus files up to 10 MiB each by default, with LocalStorage as a fallback for metadata. An optional Service Worker can hold larger files but does not survive browser restarts. Files it cannot restore show as "ghosts" with a re-add prompt. — [Uppy docs, Golden Retriever](https://uppy.io/docs/golden-retriever/)
- Uppy/Transloadit say resumable uploads matter most for large files and weak connections, which is why they created the open tus protocol. The Tus plugin lets uploads survive network outages, browser crashes, and navigating away. — [Transloadit, handling uploads](https://transloadit.com/services/handling-uploads/); [@uppy/tus package](https://classic.yarnpkg.com/en/package/@uppy/tus)
- The Background Sync API is not supported in Safari (3.1 through 27.1/TP) or Firefox (through 158). It is supported in Chrome and in Samsung Internet 5+. — [caniuse, Background Sync](https://caniuse.com/background-sync)
- NN/g's mobile-input checklist asks whether phone features (camera, GPS, voice, contacts) can fill a field, and whether copy-paste is supported. Both apply to bank-transfer screenshots. — [NN/g, A Checklist for Designing Mobile Input Fields](https://www.nngroup.com/articles/mobile-input-checklist/)
- Tesseract's best input is at least 300 DPI. It also benefits from deskewing, from removing alpha channels (Tesseract 4 blends alpha onto white), and from borders. Client-side compression therefore must not downscale text-bearing images too far. — [Tesseract docs, Improving the quality of the output](https://tesseract-ocr.github.io/tessdoc/ImproveQuality.html)
- Veryfi treats an image width or height of 500 px or less as a factor that lowers extraction reliability. — [Veryfi, Blur detection and image quality](https://faq.veryfi.com/en/articles/5429636-blur-detection)

### Inferences
- **Recommended mobile layout for payment proof (Unit Toko flow 1):**
  - Primary button "Unggah screenshot / file" uses `<input type="file" accept="image/jpeg,image/png,application/pdf">`. Most proofs are m-banking screenshots, so the gallery or Files picker is the main path, not the camera.
  - Secondary button "Foto bukti (kamera)" uses `capture="environment"` (see section 3).
  - On desktop, show a large always-visible drop zone plus a "Pilih file" button.
  - Put `image/jpeg` first in `accept` so iOS sends JPEG instead of HEIC, which Tesseract may not read.
- **Microcopy before upload:** "JPG, PNG atau PDF, maks. X MB. Pastikan nominal, tanggal, nomor referensi dan rekening tujuan terlihat jelas." Reuse the GOV.UK error templates in Bahasa Indonesia ("File harus berupa JPG, PNG atau PDF", "File harus lebih kecil dari 5 MB").
- **Compression policy:** Downscale photos to a long edge of about 2000–2500 px at quality around 0.8 or higher. Do not compress screenshots heavily. Uppy's default 0.6 is tuned for size, not OCR. Use an OCR-safe minimum, e.g. at least 1000 px on the short edge, and reject or warn below about 500 px, per Veryfi's threshold. Convert to JPEG/PNG without alpha. EXIF rotation is applied by the browser for display, but server-side Tesseract still needs the pixels rotated. Bake the rotation in when re-encoding through canvas, or normalise on the server.
- **Resilience:** Payment proofs are usually under 2 MB, so a full tus setup is optional. A single POST with automatic retry (exponential backoff), an idempotency key per selected file (to avoid duplicate proof records), and visible per-file states (Mengunggah 45% → Berhasil / Gagal – Coba lagi) is enough. Safari has no Background Sync, so keep the pending file in IndexedDB (Golden Retriever pattern) and retry on the `online` event or the next app open.
- **Preview and replace:** After upload, show a thumbnail with filename, size, and "Ganti" (replace) and "Hapus" actions. Replace should create a new version and keep the old one for audit (Unit Toko already seals records), not overwrite it. PDF: render a first-page thumbnail.
- **Privacy note (no sourced pattern found; suggested copy):** "Bukti transfer hanya dilihat oleh tim keuangan Unit Toko untuk verifikasi pembayaran." Also tell users to crop out unrelated balances if they wish.

### Gaps
- I found no Baymard or NN/g study specific to file-upload UX on mobile (drag-drop vs picker conversion data).
- I found no authoritative source on privacy-messaging patterns for financial-document uploads.
- WebKit 292350 does not state which Safari release shipped the fix. Test HEIC behaviour on current iOS 26/27 devices.

---

## 2. OCR review UX ("human in the loop"): image beside fields, bounding boxes, confidence, field-by-field confirm, keyboard flow, low-quality handling, fintech patterns

### Takeaway
Show the original document beside the extracted fields. Clicking or hovering a field highlights its source box on the image. Show confidence per field, not per document. A raw OCR score is not trustworthy on its own: combine it with format checks and business rules (amount equals order total, beneficiary equals the coop account, date within window), and send anything weak to manual review. Tesseract already emits per-word boxes and confidences (hOCR/TSV), so Unit Toko can build this overlay without a new OCR engine.

### Cited Findings
- Nutrient's argument: a confidence score says how sure the model was. A grounded score also says what it was sure about: page number plus bounding box. In their real example, confidence was 0.97 but the value came from the wrong column ("Total Assets" instead of income). Highlighting the source region is what makes the error visible. — [Nutrient, What should a document extraction confidence score actually tell you?](https://www.nutrient.io/blog/document-extraction-confidence-scores/)
- Nutrient FAQ: thresholds can route lower-confidence values to human review. High-impact fields may still need business-rule validation or review. Confidence scores help prioritise review but do not replace it. — [Nutrient](https://www.nutrient.io/blog/document-extraction-confidence-scores/)
- Taggun says its `confidenceLevel` "is not a calibrated probability or accuracy guarantee." Located fields include `regions` (pixel X/Y on the image), which are "useful for highlighting fields over the original image." Helper arrays such as `amounts[]` expose candidate values that are "useful for review tools". — [Taggun docs, Understanding Your Results](https://developers.taggun.io/docs/confidence-levels)
- Taggun recommends flagging fields at a confidence threshold of about 0.7–0.8. It names blur, smudges, poor lighting, small fonts, and folds or shadows as causes of low confidence. — [Taggun docs](https://developers.taggun.io/docs/confidence-levels) (via search summary)
- Veryfi returns an `is_blurry` boolean, which is not enabled by default. A per-field `ocr_score` below 0.8 "is a signal that the extraction results may not be as stable" and should be reviewed carefully. — [Veryfi, Blur detection and image quality](https://faq.veryfi.com/en/articles/5429636-blur-detection)
- Tesseract hOCR output gives every word a `bbox x0 y0 x1 y1` and an `x_wconf` word confidence (e.g. `bbox 65 41 111 61; x_wconf 96`). TSV output gives `left top width height conf text` per word, line, paragraph, and block. — [Tesseract docs, Command Line Usage](https://tesseract-ocr.github.io/tessdoc/Command-Line-Usage.html)
- Tesseract accuracy drops significantly when the page is skewed, because line segmentation degrades. Rotate so text lines are horizontal. — [Tesseract docs, Improving quality](https://tesseract-ocr.github.io/tessdoc/ImproveQuality.html)
- A system-design write-up suggests per-character confidence, per-word confidence (the minimum of its characters), and per-field confidence (the mean of its words). Fields below a threshold (example 0.80) go to a review queue. Value, confidence, and bbox are stored together. — [techinterview.org, LLD: Document OCR Service](https://www.techinterview.org/post/3233472317/lld-document-ocr/?format=md) (secondary/design write-up)
- Dext extracts and shows the original document image, date, supplier, currency, total, tax, invoice/reference number, and payment-method last 4 digits. "Each of these fields are editable via the Item details page." — [Dext Help, What information does Dext extract](https://help.dext.com/en/s/article/What-data-is-extracted-by-Dext-Prepare)
- Ramp describes its receipt pipeline as OCR extraction of merchant, date, amount, and category, followed by "real-time transaction matching: receipts link to the correct expense as soon as it clears." — [Ramp blog, How receipt scanning works](https://ramp.com/blog/receipt-scanning-expense-management)
- Duplicate detection should be layered. A SHA-256 file hash catches byte-identical resubmission, but re-saving the image defeats it. Perceptual hashing catches re-photographed copies within a small Hamming distance. Fuzzy field matching (merchant/date/amount) depends on extraction quality. Recommendation: hard-block only deterministic matches (identical hash, exact field match) and flag probabilistic ones for a reviewer, because false-positive blocks teach users the control is unreliable. — [Peakflo blog (vendor)](https://peakflo.co/blog/duplicate-expense-claim-detection-employee-reimbursements)
- A USPTO filing on partial perceptual hashing for invoices compares hashes by Hamming distance. A watermarked copy differed by only a few bits, and aHash is described as the easiest and fastest implementation. — [USPTO 11501344](https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/11501344)
- Indonesian context: Kompas TV (Apr 2025, citing BCA) reports that AI-edited fake m-banking transfer proofs are increasingly convincing. Common scams are "already transferred" and "overpaid, please refund the difference." Signs include inconsistent fonts, untidy layout, and faint or differently coloured text. Advice is not to trust a proof until the receiving account mutation is checked. — [Kompas TV](https://www.kompas.tv/ekonomi/589498/modus-penipuan-bukti-transfer-palsu-makin-canggih-berkat-ai-cek-langkah-pencegahannya)
- An Indonesian academic study combined image forensics, OCR, and a KNN classifier (k=7) to validate transfer proofs, reporting 97.1% accuracy on its own dataset (Android app). — [Sistemasi journal](https://sistemasi.ftik.unisi.ac.id/index.php/stmsi/article/download/5728/1125)
- Expensify SmartScan is reported (secondary sources) to auto-fill merchant, amount, and date, with processing that "can take a few minutes." — [checkthat.ai Expensify reviews](https://checkthat.ai/brands/expensify/reviews); [aisotools Expensify review 2026](https://aisotools.com/blog/expensify-review-2026) (aggregator, low reliability)

### Inferences
- **Reviewer screen (desktop, finance) layout:**
  - Left (about 60%): document viewer with zoom, pan, rotate, and a toggle for OCR boxes.
  - Right: a fixed set of four field cards: Nominal, Tanggal/Jam, No. Referensi, Rekening/Nama Penerima. Each card shows the suggested value, a confidence chip (Tinggi / Periksa / Rendah), why it was flagged (e.g. "format tanggal tidak dikenali", "nominal ≠ total pesanan Rp 1.250.000"), and the expected value from the order and coop account.
  - Focusing a card draws the Tesseract bbox(es) for that value on the image and scrolls them into view.
  - Below the cards: "Bank statement match" panel. When a reviewer pastes or selects a mutasi line, show a side-by-side diff.
- **Confidence should be a composite**, because Tesseract `conf` is per word, uncalibrated, and does not say which field the word belongs to (the Nutrient argument). Combine:
  - minimum word confidence of the tokens used;
  - parse validity (amount parses to IDR integer, date parses, reference matches a bank-specific regex);
  - cross-check against order data (amount equals order total or unique code; beneficiary account equals the coop's account; date between order creation and now).
  Route to manual review if any high-impact field fails a cross-check, regardless of OCR score.
- **Field-by-field confirm, keyboard flow:**
  - Tab or J/K to move between fields.
  - Enter accepts the suggestion.
  - Typing overwrites (the input is pre-selected).
  - Esc reverts.
  - A single "Approve" (Ctrl+Enter) is enabled only when all four fields are confirmed.
  - "Reject / request new proof" opens reason chips ("buram", "terpotong", "bukan bukti transfer", "nominal tidak sesuai").
  - Show the queue position ("3 dari 12") and auto-advance to the next item.
  - Log every edited field as suggested → corrected for audit and tuning.
- **Buyer-side quality gate (before submit):** Run cheap checks on the client (resolution, file type, optional Laplacian-variance blur check), then server checks (Tesseract mean confidence, whether an amount-like token was found). If they fail, show "Gambar kurang jelas – nominal tidak terbaca. Unggah ulang screenshot asli dari aplikasi m-banking (bukan foto layar)." Prefer original m-banking screenshots over photos of screens, which suffer moiré and glare.
- **Duplicates:**
  - Hard-block: identical SHA-256 already attached to another order; identical bank reference number with the same amount.
  - Flag for review with a side-by-side compare: perceptual-hash near-match, or same amount, date, and account within ±1 day.
  - Never tell the buyer "fraud". Say "Bukti ini mirip dengan bukti yang sudah pernah diunggah – tim keuangan akan memeriksa."
- Fake-proof risk in Indonesia means the bank-statement (mutasi) check must stay mandatory. OCR only speeds up the matching.

### Gaps
- I found no first-party design write-ups from Expensify, Ramp, Brex, or Xero/QuickBooks about their review-screen layouts, bbox highlighting, or keyboard flows. Only feature-level descriptions and aggregator reviews were available.
- I found no usability studies comparing confidence-indicator styles (colour chips vs percentages) or measuring review speed.
- I found no data on perceptual-hash false-positive rates for m-banking screenshots, which share near-identical layouts per bank. This risk is likely higher than for receipts.

---

## 3. Mobile web camera capture: getUserMedia vs `<input capture>`, overlays/guides, auto-capture, permissions UX (Android Chrome, iOS Safari), fallbacks

### Takeaway
For one-off document photos (payment proof, a photo of a signed Surat Jalan), `<input type="file" accept="image/*" capture="environment">` is the most robust choice. It opens the native camera, needs no permission prompt handling, and is supported on iOS Safari and Chrome/Firefox Android. Use `getUserMedia` only for live experiences: barcode scanning, document guides, auto-capture. On iOS Home-Screen web apps, expect repeated camera permission prompts. Keep the camera component mounted rather than routing away and back.

### Cited Findings
- `capture` takes `user` or `environment` on `<input type="file">` (formerly a boolean). MDN marks it "Limited availability" (not Baseline). — [MDN, capture attribute](https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Attributes/capture)
- caniuse HTML Media Capture: supported on Safari on iOS 6 through 27.2, Chrome for Android 155, and Firefox for Android 158. Global usage is about 61%, reflecting no desktop support. — [caniuse, HTML Media Capture](https://caniuse.com/html-media-capture)
- `getUserMedia()` requires a secure origin (HTTPS) and user consent. The camera should be stopped (`track.stop()`) when not needed, to save battery and to "give users confidence in your application". — [web.dev, Capturing an image from the user](https://web.dev/articles/media-capturing-images)
- `facingMode: { exact: "environment" }` makes the request fail if there is no rear camera or the user declines. Non-exact values are treated as preferences. facingMode has been Baseline since Sept 2017. — [MDN, facingMode](https://developer.mozilla.org/en-US/docs/Web/API/MediaTrackConstraints/facingMode)
- WebKit bug 215884: in standalone (Home Screen) mode, iOS re-prompted for camera permission on every hash change, which Safari tabs did not do. Status is "RESOLVED CONFIGURATION CHANGED". A WebKit engineer commented (Feb 2026) that persistent permission for PWAs is a separate issue. A new bug, 327237, was filed on 12 Oct 2026: "[iOS] Home Screen web apps ask for camera/microphone permission again on every launch," with reports that pushState navigation and cold boots also re-prompt. — [WebKit Bugzilla 215884](https://bugs.webkit.org/show_bug.cgi?id=215884)
- WebKit engineer: in iOS Safari, users can persistently grant camera access to a site from the Safari UI. Scandit support advises setting the camera to "Allow" via the "AA" menu > Website Settings. — [WebKit Bugzilla 215884](https://bugs.webkit.org/show_bug.cgi?id=215884); [Scandit support](https://support.scandit.com/hc/en-us/articles/360008443011)
- An Apple Developer Forums thread reports a Next.js app on iOS Safari re-prompting for the camera across routes even after permission was granted. It was closed without resolution. — [Apple Developer Forums 788518](https://developer.apple.com/forums/thread/788518)
- Torch on iOS: the `torch` constraint can be toggled via `applyConstraints` on an existing video track. `getSettings()` reported a stale torch value after iOS 18 (fixed in WebKit Dec 2024). `getSupportedConstraints()` misreported torch on iOS 16.4. — [WebKit Bugzilla 280970](https://bugs.webkit.org/show_bug.cgi?id=280970); [WebKit Bugzilla 270779](https://bugs.webkit.org/show_bug.cgi?id=270779)
- Document edge detection in the browser: an OpenCV.js pipeline (grayscale, Gaussian blur, threshold, contours, then `cv.warpPerspective`) works for high-contrast scenes (white paper on a dark surface) but struggles on cluttered backgrounds. Corners come back `undefined` when the document is partly out of frame. The OpenCV.js WASM is about 8 MB and should load async. — [Dynamsoft, Browser document scanner with OpenCV.js](https://dynamsoft.com/codepool/web-document-scanner-with-opencvjs.html)
- jscanify is an open-source OpenCV.js-based scanner offering `highlightPaper` (live outline) and `extractPaper` (rectified output). It advises a flat surface with a solid background. Auto-capture is not built in. — [jscanify docs index](https://docsearch.algolia.com/mcp/docs/repo/puffinsoft/jscanify)
- Commercial option: Dynamsoft Document Viewer provides camera auto-capture and interactive polygon (crop) editing on top of detection. — [Dynamsoft](https://dynamsoft.com/codepool/web-document-scanner-with-opencvjs.html) (vendor)

### Inferences
- **Decision rule for Unit Toko:**
  - (a) Payment-proof photo and POD photo use `<input capture="environment">`. Zero permission UX, native camera quality and autofocus, works in Safari tabs and installed PWAs alike.
  - (b) Barcode scanning (receiving, picking, opname) uses `getUserMedia` with `facingMode: { ideal: "environment" }`, a full-screen viewfinder, and a torch toggle where `getCapabilities().torch` exists.
  - (c) Document auto-capture with guides is optional and low priority. OpenCV.js adds about 8 MB, and most proofs are screenshots anyway.
- **Permission UX:**
  - Before calling `getUserMedia`, show a priming card ("Izinkan kamera untuk memindai barcode").
  - On `NotAllowedError`, show device-specific recovery steps. iOS: AA > Pengaturan Situs Web > Kamera > Izinkan. Android Chrome: lock icon > Izin > Kamera.
  - Always offer a manual-entry or keyboard-scanner fallback.
- **iOS PWA mitigation:**
  - Mount the scanner as a persistent overlay or modal in the layout, outside the route segment that changes, so the stream is not torn down by Next.js navigation.
  - Reuse one `MediaStream` across scans and check `track.readyState === "live"` before re-requesting.
  - Consider recommending that iOS warehouse staff use the Safari tab with "Allow" set, rather than a Home-Screen install, until WebKit 327237 is resolved.
- **Guides (if building live capture):** Use a rectangular overlay with document aspect ratio, a hint line ("Letakkan Surat Jalan di permukaan gelap, isi seluruh bingkai"), and a stability/blur check before enabling the shutter. Always allow manual shutter and manual corner adjustment.

### Gaps
- I did not find a current authoritative table of iOS Safari `getUserMedia` constraint support (focusMode, zoom, torch) for iOS 26/27. The WebKit bugs give partial evidence only.
- I did not find official Apple documentation on PWA camera-permission persistence. Evidence is from bug trackers and forums.
- I found no independent accuracy benchmark for in-browser document auto-capture libraries.

---

## 4. Barcode scanning in the browser: BarcodeDetector status, libraries, HID wedge scanners, feedback, continuous scanning

### Takeaway
As of Oct 2026, BarcodeDetector is usable natively only on Chrome/Samsung Internet for Android (and Chrome on macOS/ChromeOS). It remains disabled by default in all Safari/iOS versions, and Firefox lacks it. The practical approach is to feature-detect and fall back to a WASM polyfill (`barcode-detector`, ZXing-C++), or to buy a commercial SDK (STRICH, Scandit, Dynamsoft). Avoid basing new work on zxing-js or html5-qrcode, which are both in maintenance mode or seeking owners. Treat USB/Bluetooth scanners as keyboard wedges with a CR suffix. Vibration does not work on iOS, so use sound plus a visual flash for feedback.

### Cited Findings
- caniuse BarcodeDetector (fetched Oct 2026):
  - Chrome for Android 155: Supported.
  - Samsung Internet 13.0–30: Supported.
  - Chrome desktop 83–158: Partial.
  - Edge 83–155: Partial.
  - Firefox 2–157 and Firefox for Android 158: Not supported.
  - Safari 17.0–26.6, 27, 27.1–TP: Disabled by default.
  - Safari on iOS 17.0–26.6, 27.0, 27.1–27.2: Disabled by default.
  - Global: 46.39% full + 30.22% partial.
  — [caniuse, BarcodeDetector](https://caniuse.com/mdn-api_barcodedetector)
- Chrome docs: barcode detection is available on macOS, ChromeOS, and Android. Android requires Google Play Services. This explains the "partial" rating for Chrome desktop (Windows/Linux lack it). — [Chrome for Developers, Shape Detection API](https://developer.chrome.com/docs/capabilities/shape-detection)
- MDN: the Barcode Detection API is experimental, "Limited availability" (not Baseline), secure-context only, and available in Web Workers. Use `BarcodeDetector.getSupportedFormats()` to check formats (e.g. `ean_13`, `code_39`). — [MDN, Barcode Detection API](https://developer.mozilla.org/en-US/docs/Web/API/Barcode_Detection_API)
- `barcode-detector` (Sec-ant) is a BarcodeDetector ponyfill/polyfill using ZXing-C++ WebAssembly. It covers linear formats (codabar, code_39, DataBar variants, EAN-8, …) and matrix formats (QR, Micro QR, Aztec, …). The `.wasm` loads at runtime from jsDelivr by default, and `prepareZXingModule` can self-host it for CSP or offline use. — [GitHub, Sec-ant/barcode-detector](https://github.com/Sec-ant/barcode-detector)
- zxing-js README: "The project is in maintenance mode… Only bug fixes and minor enhancements will be considered. There is otherwise no active development or roadmap". The maintainers are open to new maintainers, and the README points to STRICH for commercial support. — [GitHub, zxing-js/library](https://github.com/zxing-js/library)
- The html5-qrcode README says "Important – Looking for new owners for this project." The library is built on zxing-js and supports EAN_13, EAN_8, UPC_A/E, CODE_128, CODE_39, ITF, QR, etc. — [GitHub, mebjas/html5-qrcode](https://github.com/mebjas/html5-qrcode); [STRICH comparison](https://strich.io/comparison-with-oss.html)
- STRICH's own benchmark (vendor, not independent): on good-quality DHL labels, ZXing-JS failed to read a finer Code 128, possibly because of the low camera resolution it requests, which it cannot configure. Quagga produced a misread value. QuaggaJS's original project stopped in 2017; the Quagga2 fork is volunteer-maintained and 1D-only. STRICH is commercial with a yearly fixed fee that does not depend on scans or devices. — [STRICH, comparison with OSS](https://strich.io/comparison-with-oss.html); [strich.io](https://strich.io)
- Scandit SparkScan (commercial, needs a license key) is a pre-built overlay for "scan-intensive workflows such as retail inventory or logistics goods receiving":
  - a large semi-transparent trigger button users can drag, with its position remembered across sessions;
  - a movable mini preview;
  - audio/haptic feedback on each scan, with the scanned code shown briefly;
  - tap-and-hold to scan multiple codes in a row;
  - zoom and flash controls.
  — [Scandit docs, About SparkScan (Web, SDK 8.x)](https://docs.scandit.com/sdks/web/sparkscan/intro/)
- onScan.js (keyboard-wedge detection) separates scans from typing by input speed and prefix/suffix:
  - `avgTimeByChar` default 30 ms;
  - `timeBeforeScanTest` 100 ms;
  - `minLength` 6;
  - `suffixKeyCodes` default [9, 13] (Tab/Enter);
  - suffix key events are silenced with `preventDefault`/`stopImmediatePropagation`;
  - `reactToPaste` supports built-in scanners in paste mode;
  - `singleScanQty` passes a quantity per scan.
  — [onScan.js README](https://unpkg.com/onscan.js@1.5.2/README.md)
- Odoo Barcode hardware guidance:
  - configure scanners to send a carriage return (CR) suffix, which Odoo treats as end-of-barcode;
  - Odoo applies a default 100 ms delay between scans to prevent accidental double scans;
  - the scanner keyboard layout must match the OS keyboard layout;
  - Android mobile computers (e.g. Cipherlab) should use KeyEvent keyboard emulation.
  — [Odoo 18 docs, Barcode scanner setup](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/barcode/setup/hardware.html)
- Vibration API is not supported on Safari/iOS (3.2 through 27.2) and is supported in Chrome 30+. — [caniuse, Vibration](https://caniuse.com/vibration). The W3C implementation report (June 2024) says Safari on iOS fails all automated tests ("no implementation"), and Firefox for Android fails manual tests. — [W3C public-device-apis log](https://lists.w3.org/Archives/Public/public-device-apis-log/2024Jun/0061.html)

### Inferences
- **Scanner abstraction for Unit Toko:** one `useScanner()` hook with three input sources feeding the same `onScan(code, source)` handler.
  - (1) HID wedge: onScan.js-style listener on `document`, CR suffix, ignored when focus is in a free-text field.
  - (2) Camera: native `BarcodeDetector` if `'BarcodeDetector' in window` and `getSupportedFormats()` includes `ean_13`. Otherwise the `barcode-detector` WASM polyfill, self-hosted for CSP/offline. This covers iOS Safari.
  - (3) Manual: GTIN text field with `inputmode="numeric"` and check-digit validation.
  - Validate the GTIN check digit and look it up before accepting, which mitigates misreads.
- **Feedback:**
  - Success: short Web Audio beep, green flash or banner with product name and new count, `navigator.vibrate(50)` where present (no-op on iOS).
  - Error (unknown GTIN, wrong item for this pick, over-quantity): distinct low double beep, red full-width banner that stays until acknowledged. In continuous mode, apply a debounce such as 100 ms or more, plus "same code within N ms" suppression, as Odoo does.
- **Continuous scanning for opname:** Keep the camera open in a compact preview (SparkScan-like) at the top or bottom of the screen with a large trigger, and show the running count list below. Each scan increments the line; long-press a line to set an exact quantity. For 1D EAN-13 on small packs, request at least 1280×720 and offer torch and zoom. The STRICH benchmark suggests low resolution is a common cause of failure.
- **Budget option:** native API plus polyfill is free and adequate for EAN-13/QR at short range. If staff struggle (damaged labels, low light, high volume), STRICH (flat yearly fee) or Scandit is the upgrade path, and a cheap Bluetooth HID ring or handheld scanner is often the best UX improvement for heavy receiving or opname.

### Gaps
- I found no independent (non-vendor) benchmark comparing BarcodeDetector, the ZXing-C++ WASM polyfill, STRICH, Scandit, and Dynamsoft on mobile web.
- I did not retrieve current STRICH, Scandit, or Dynamsoft pricing.
- WebKit has not published a signal on whether BarcodeDetector will be enabled by default on iOS (still a flag in Safari TP 27.x).

---

## 5. Warehouse receiving / picking / dispatch UX patterns (task screens, touch targets, one-hand, quantity entry, exceptions, offline)

### Takeaway
WMS barcode apps share one model. The worker opens a task (receipt, pick, delivery) by scanning or tapping the document. They scan product (or package) barcodes to increment lines. They edit a line with large +1/−1, "+N (fill expected)", and a numeric keypad. They validate once at the end. If quantities are short, the system asks whether to create a backorder or record an exception. Unit Toko can copy this, adding koli (carton) counting and damaged/short/over reasons.

### Cited Findings
- Odoo 18 Barcode receipts:
  - Scanning a product barcode receives 1 unit; scanning a packaging barcode (e.g. 6-pack) receives the pack quantity.
  - The pencil icon opens a line editor with a Quantity field, a "+#" button that auto-fills the quantity ordered on the PO (e.g. "+12"), +1/−1 buttons, packaging buttons, number keys, and a Location dropdown (default WH/Stock).
  - Confirm saves the line; Validate finishes the transfer.
  — [Odoo 18 docs, Process receipts and deliveries with barcodes](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/barcode/operations/receipts_deliveries.html)
- Odoo deliveries: if not everything has been scanned, the worker scans the remaining items or taps "+#" on the line, then Validate. — [Odoo 18 docs](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/barcode/operations/receipts_deliveries.html)
- Odoo community docs describe two receipt modes: working from printed documents (scan the document barcode to open it, scan lines, then scan a "Validate" command barcode) or working on screen and scanning products directly. — [Odoo users docs, receipts](https://odoo-users.readthedocs.io/en/stable/inventory/barcode/operations/receipts.html)
- Odoo 18 shows a pop-up warning when the scanned quantity is less than required before validation. — [Cybrosys Odoo 18 book, Barcode](https://www.cybrosys.com/odoo/odoo-books/v18/barcode/exploring-the-barcode-app-features/) (third-party)
- Scandit notes hardware-button triggering helps users wearing gloves, and its error state can block unwanted barcodes and then resume immediately. — [Scandit docs, SparkScan advanced](https://docs.scandit.com/next/sdks/net/ios/sparkscan/advanced/)
- WCAG 2.2 SC 2.5.8 (AA) requires pointer targets of at least 24×24 CSS px, or spacing such that 24-px circles do not overlap, with exceptions for inline links and equivalent controls. — [W3C, Understanding SC 2.5.8](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html)
- For whole-number entry, GOV.UK recommends `type="text"` plus `inputmode="numeric"` to get the numeric keypad. `inputmode="decimal"` adds a decimal separator. Some keypads cannot enter negative values. — [GOV.UK Design System, Text input](https://design-system.service.gov.uk/components/text-input/)
- Background Sync is unavailable in Safari and Firefox (see section 1). Offline queues must therefore flush on app foreground or `online`, not rely on service-worker sync. — [caniuse, Background Sync](https://caniuse.com/background-sync)

### Inferences
- **Receiving (penerimaan supplier) screen:**
  - Header: supplier, PO/receipt number, expected koli count.
  - Step 1 "Hitung koli": big counter (−/+ at 64 px or larger, numeric keypad on tap), with "Sesuai (N koli)" as a one-tap confirm.
  - Step 2 "Periksa isi": line list sorted by bin/shelf. Each line shows product, expected qty, received qty, and a progress bar. Scan increments; tap opens an Odoo-style line editor (+1/−1, "+sisa", keypad, packaging/pack-conversion buttons).
  - Exceptions are a sticky bottom sheet: Kurang (short), Lebih (over), Rusak (damaged, requires photo plus qty), Salah barang (wrong item). Each records a reason code and optional note.
  - Validate asks "Terima sebagian dan buat backorder?" when lines are short.
- **Picking for division orders:**
  - Show one task at a time, in shelf order.
  - Scan the location (optional), then the product.
  - Scanning the wrong product gives a red blocking banner ("Bukan barang ini – ambil X dari Rak B2").
  - A short pick must choose a reason (stok kosong / rusak / tidak ditemukan), which triggers a substitution or backorder (Unit Toko already has substitution flows).
- **Dispatch / Surat Jalan:** At pack-out, scan each koli label (QR on carton) to bind koli to the Surat Jalan number. Print or show the Surat Jalan with a QR encoding its ID, which the recipient side uses for confirmation (section 6).
- **Ergonomics:**
  - Primary actions in the bottom thumb zone.
  - Targets at least 48 px tall for gloves (beyond WCAG's 24 px minimum; WCAG 2.5.5 AAA is 44 px, not re-verified here).
  - High-contrast status colours plus icons, never colour alone.
  - Large type for quantities.
  - Avoid gestures that need two hands.
- **Offline:**
  - Queue scan events (not final state) in IndexedDB with client UUIDs and timestamps, and replay them idempotently when back online.
  - Show a persistent "3 aksi belum tersinkron" chip.
  - Block "Validate" offline, or allow it as "pending validation", depending on audit rules. Unit Toko's sealed records argue for server-side validation only.

### Gaps
- I could not retrieve official Zoho Inventory picklist docs (404) or NetSuite WMS receiving/picking screen docs. Shopify Stocky is shutting down (see section 7), so its receiving UX is not a forward-looking reference.
- I found no published research on glove-friendly touch-target sizes for warehouse web apps. The 48 px figure above is an inference.

---

## 6. Delivery proof / e-POD UX (signature, photo of signed note, recipient name, QR handoff, offline, timestamps)

### Takeaway
Logistics apps make POD requirements configurable per task. Signature, photo, and notes can each be required, optional, or off. Completion is blocked until the required items are captured, and each capture is stamped with time and GPS. For Unit Toko's internal B2B deliveries to bank divisions, a good pattern is: scan the Surat Jalan QR, enter the recipient name and position, capture a signature on screen or a photo of the wet-signed Surat Jalan, add optional notes or exceptions, then sync with timestamps. Indonesian law recognises uncertified electronic signatures, but they carry weaker evidentiary weight. Keep rich metadata as forensic evidence.

### Cited Findings
- Onfleet API "Completion Requirements" per task: `signature`, `photo`, `notes` (booleans: must be collected to complete), `minimumAge` (recipient ID scanned and age verified; plan-dependent), and `pin` (Enterprise). — [Onfleet API docs, Completion Requirements](https://docs.onfleet.com/reference/completion-requirements)
- Onfleet driver-app settings let admins set signature, photo, and notes to Required, Enabled, or Disabled, separately for pickup and dropoff. Drivers cannot complete until required items are collected. Dispatchers can force-complete. Photos, signatures, contactless signatures, notes, and the timeline sync to the dashboard. — [Onfleet Help Center](https://intercom.help/onfleet-4f83d83093ac/en/articles/15254706); [Onfleet Help, Completed tasks](https://intercom.help/onfleet-4f83d83093ac/en/articles/15254796-completed-tasks) (via search summary)
- Spoke Dispatch (Circuit) sets the POD type in Settings > Operations > Proof of delivery. Drivers are prompted for that type, and an API field toggles POD per stop. — [Circuit/Spoke product update, Oct 2024](https://getcircuit.com/teams/product-updates/customize-pod-collection-and-delivery-methods-to-meet-client-needs)
- Circuit's comparison (vendor-authored; ranks its own product first):
  - Spoke: signature, photo, barcode, geotags, timestamps, notes, offline capture, no expiry on stored POD.
  - Track-POD: up to 10 photos, configurable ePOD templates with conditional fields, PIN verification, offline mode with automatic sync.
  - DeTrack: contactless SMS POD.
  - Offline POD capture with automatic sync "should be a selection criterion" for warehouses with spotty Wi-Fi.
  — [Circuit blog, Best Proof of Delivery Apps 2026](https://getcircuit.com/teams/blog/proof-of-delivery-app)
- Upper (vendor) suggests at least two photos for standard drops (context plus label close-up) and says 1–2 MP is enough for evidence. — [Upper blog, multi-photo POD](https://www.upperinc.com/blog/multi-photo-proof-of-delivery.txt)
- signature_pad (canvas):
  - scale the canvas by `devicePixelRatio`;
  - resizing or rotating the device clears the canvas, so call `clear()`/`redraw()` or lock orientation;
  - export with `toDataURL()` (PNG/JPEG/SVG) or `toSVG()`, and `toData()` for raw point groups;
  - set an opaque `backgroundColor` for JPEG.
  — [GitHub, szimek/signature_pad](https://github.com/szimek/signature_pad)
- Indonesia: per the explanation of Article 11(1) of the UU ITE (Electronic Information and Transactions Law), an electronic signature, "even if only a code," has the same standing as a manual signature, and the article sets minimum requirements while leaving methods open. Certified e-signatures (via a PSrE, a licensed certification provider) have very strong evidentiary weight, close to an authentic deed. Uncertified ones are proven via digital forensic examination and are weaker because they are open to manipulation. — [Hukumonline, Pembuktian Tanda Tangan Elektronik](https://www.hukumonline.com/klinik/a/begini-pembuktian-tanda-tangan-elektronik-di-pengadilan-lt62444b29a4d00)
- An uncertified e-signature is one made without a PSrE (PP 71/2019, Art. 60). Legal commentators disagree on how much evidentiary weight it carries. — [UIKA Yustisi journal](https://ejournal.uika-bogor.ac.id/index.php/YUSTISI/article/download/18888/6126/88144); [UMA law faculty](https://mh.uma.ac.id/?p=4330)

### Inferences
- **Recommended Unit Toko e-POD flow (on the driver's or staff phone, or a tablet at the division):**
  1. Scan the Surat Jalan QR, or open it from the task list, to show koli count and items.
  2. Recipient checks koli, using one tap "Jumlah koli sesuai (N)" or entering the received count.
  3. Capture recipient name (with autocomplete from the division's known PICs), position/unit, and optional NIP.
  4. Capture a signature on a full-width canvas (landscape hint, "Hapus" and "Ulangi" buttons, minimum stroke-length check). Fallback: photograph the wet-signed Surat Jalan with `capture="environment"`.
  5. Record exceptions: koli kurang/rusak, each with photo and note.
  6. Submit, and stamp server time, device time, GPS (if permitted), user, and a hash of the signature image and Surat Jalan version. These artefacts support a forensic-style proof for uncertified signatures.
- **Requirements per delivery type**, Onfleet-style: e.g. a signature is required for all bank-division deliveries, and a photo is required only when there is an exception. This keeps the common path fast.
- **QR handoff alternative:** The recipient scans the Surat Jalan QR with their own phone, logs in with their Unit Toko account, and taps "Konfirmasi terima". This is stronger identity evidence than a scribble, because it is tied to an authenticated account, and it needs no shared device. Keep the scribble or photo as fallback.
- **Offline:** Store POD drafts (signature PNG/SVG plus photos) in IndexedDB and sync when online. Show "Tersimpan di perangkat – akan dikirim otomatis" and keep device time as well as server receipt time.

### Gaps
- I found no official documentation from Indonesian couriers (J&T, SiCepat, Lalamove, Paxel) on their e-POD UX. All POD patterns above are from US/global vendors.
- I found no direct legal source on whether a photographed wet signature on a Surat Jalan counts as an electronic signature. Hukumonline raises the question, but the answer text was not retrievable. Seek legal advice if disputes matter.
- I could not retrieve Uber Direct POD docs (404) or Onfleet's "Complete a Task" help article (403).

---

## 7. Stock count (stock opname) UX: blind vs expected qty, shelf-order count sheets, recount on variance, variance approval

### Takeaway
Mature systems separate counting from approving. Counters record quantities, ideally blind (no expected qty shown). The system computes variance against a snapshot. A supervisor approves, or rejects to trigger a recount, and only then are adjustments posted with an audit trail. Odoo's "Leave Empty" vs "Set Current Value" options show the tradeoff: blind forces an actual count, while prefilled only asks the counter to verify.

### Cited Findings
- Odoo 18 "Request a Count": choose products/lines, then set Inventory Date, responsible User, Accounting Date, and Count = "Leave Empty" or "Set Current Value". "The Leave Empty option forces the employee conducting the audit to manually type in the number they counted, while the Set Current Value option only requires the employee to verify the counted quantity and click Apply." — [Odoo 18 docs, Inventory adjustments](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/inventory_management/count_products.html)
- Odoo Physical Inventory shows On Hand Quantity, Counted Quantity, and Difference columns. A "Set" icon copies on-hand to counted (difference 0). Applying records an adjustment move, including 0-quantity moves for history. Count sheets can be printed as PDF ("Print ‣ Count Sheet"). — [Odoo 18 docs](https://www.odoo.com/documentation/18.0/applications/inventory_and_mrp/inventory/warehouses_storage/inventory_management/count_products.html)
- NetSuite WMS: completed counts go to "Complete/Pending Approval". A supervisor approves (final; inventory adjustments posted) or rejects ("status returns to Started and the warehouse operator must perform another count"). The record shows Count Quantity, Adjusted Quantity, and Variance Detail per item, plus who counted and when. — [NetSuite Help, Approving or Rejecting Inventory Counts](https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_155993310131.html)
- NetSuite recount: on the mobile app, workers can recount completed count tasks before submitting for approval, and recount all or only some tasks of a rejected count. Once in Completed/Pending Approval, recount is not possible. — [NetSuite Help, Recounting Cycle Counts](https://docs.oracle.com/en/cloud/saas/netsuite/ns-online-help/section_0807110426.html)
- ShelfCount (Shopify app): staff count without seeing expected quantities, the app shows per-item variance valued at cost, and the manager commits in one tap or discards before any adjustment is written. Uncounted items are left untouched by default. — [Shopify App Store, ShelfCount](https://apps.shopify.com/shelfcount-stock-take) (via search summary)
- A WooCommerce stocktake plugin offers a blind-count mode that hides expected quantities. It takes a stock snapshot at session start so sales during the count do not distort variance. — [WordPress.org, Stocktake – Physical Inventory Count for WooCommerce](https://ka.wordpress.org/plugins/?p=336205) (via search summary)
- Tiered variance policy (consulting/secondary): small variances auto-approve, moderate ones require a recount, and large ones require supervisor sign-off plus root-cause analysis. NetSuite sets no universal recount threshold; companies define their own. — [Houseblend, NetSuite cycle counting controls](https://www.houseblend.io/articles/netsuite-cycle-counting-controls) (consultancy content)
- Shopify Stocky (a common SMB stocktake reference) was delisted on 2 Feb 2026, with full shutdown on 31 Aug 2026 per a vendor's sunset page. Community posts say Shopify's native barcode stocktakes require POS Pro (unconfirmed). — [SKUSavvy](https://www.skusavvy.com/best-stocky-replacement-app); [Shopify Community](https://community.shopify.com/t/removing-stocky-is-a-material-retail-operations-risk/589102)

### Inferences
- **Recommended Unit Toko opname design:**
  - **Plan:** The supervisor creates an opname session per area or shelf. The system snapshots on-hand quantities (the "book" quantity) at start and freezes or flags movements in that area. It generates count sheets in physical shelf/bin order (printable PDF, plus a mobile list in the same order).
  - **Count (blind by default):** The mobile list shows bin → product name, photo, and GTIN, with no expected quantity. Counting modes: scan-to-increment (each GTIN scan +1; pack GTIN adds pack qty), or "Ketik jumlah" with a numeric keypad. Show the counter's own running total, never the book qty.
  - **Paper-sheet path:** Staff later enter counts per line in the same order. OCR of handwritten sheets is not recommended.
  - **Variance review:** The supervisor sees book vs counted vs variance (units and Rp at cost), sorted by absolute value. Rows above the threshold are auto-marked "Hitung ulang". The recount goes to a different counter if possible, still blind. Only after recount, approve. Approval posts adjustments as sealed, audited records with reason codes (rusak, hilang, salah input penerimaan, belum tercatat penjualan).
  - **Uncounted items:** Never zero them silently. List "Belum dihitung (12)" and require an explicit action. This follows ShelfCount's default.
- **Non-blind "verify mode"** (Odoo "Set Current Value") is acceptable for quick cycle counts of low-value items. Mark these counts as "verifikasi" in audit so variance analysis can discount them.

### Gaps
- I found no empirical study comparing accuracy or speed of blind vs non-blind counts in retail.
- I found no official Shopify Help page confirming Stocky's shutdown date or POS Pro stocktake requirements.
- I found no source on Indonesian cooperative or retail stock opname regulatory requirements (e.g. accounting standards for koperasi inventory adjustments).
