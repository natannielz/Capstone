# Local document processing

## Current boundary

The internal `ILocalOcrEngine` now has a real Windows Tesseract candidate and bounded immutable extraction contracts. The helper accepts one assigned image stream and returns suggestions with text locations, confidence and model/runtime/preprocessing provenance. The backend remains .NET; this isolated Python/native helper has no application API, Mongo credentials or financial operations.

The worker can now register this candidate through the optional `Evidence:PaymentOcr` section. Without complete configuration and a matching, current processing policy it claims no payment jobs. Migration 35 adds private processing grants, results, failure records and required processing/derivative-read audits. The buyer's payment workspace can load queued/running/retry/dead/succeeded state and the exact submitted image's suggestions through an authenticated, revision-bound endpoint. Finance has a separately permissioned submitted-payment viewer described below. OCR never changes submitted fields, money or payment approval. Correction, assignment and bank-decision workflows remain separate work. Supplier invoice and delivery-note extraction explicitly return unsupported rather than pretending the payment parser applies to them.

The adapter checks exact helper/runtime/library/model pins, retains read locks, uses isolated Python startup, and binds output to the actual stream SHA-256 and requested immutable evidence reference. It bounds bytes, pixels, text, fields, coordinates, JSON/output size, child count, memory and elapsed time. Memory/process confinement is not an egress firewall or a restricted service identity. Production activation needs approved immutable directories, runtime/standard-library/dependency review, denied network egress and separately restricted worker credentials.

Unknown fields stay null. Amounts use strict IDR grammar and the existing money bounds; the parser never repairs `O` to `0`, treats balance/fees as transfer amount, resolves conflicting labels, fills a missing date, infers timezone or confirms that funds arrived. Confidence is engine output, not probability of a payment. Boundary-contact and low-confidence flags describe observed text; blur/rotation detection and image similarity have not been implemented.

## Reproduce the local candidate

Use [tesseract-candidate.json](../../backend/document-processing/tesseract-candidate.json) to obtain/check exact evaluation assets. Keep binaries/models and generated outputs outside Git. The command below uses the already provisioned local dependencies, generates labelled synthetic images, compiles, tests the real process plus existing image decoder, requires all selected tests to pass without skips, and writes measurements/pins. It performs no installation, cloud inference, worker registration, production migration or external message.

```powershell
& ./backend/tests/DocumentProcessing/run-local-candidate.ps1 `
  -Python 'C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' `
  -Packages 'C:\Users\User\Documents\Capstone\.tools\local-ocr\tesseract\packages' `
  -Pillow 'C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\Lib\site-packages' `
  -Models 'C:\Users\User\Documents\Capstone\.tools\local-ocr\tesseract\models' `
  -Font 'C:\Windows\Fonts\arial.ttf' `
  -Dotnet 'C:\Users\User\Documents\Capstone\.tools\dotnet10\dotnet.exe'
```

Paths are explicit local inputs, not a production deployment recipe. The command restores its process environment afterward. `tmp/production/ocr-fixtures/manifest.json` binds generated images to expected fields and the font hash. The aggregate report contains only synthetic identifiers/match outcomes and metrics. Test-created runtime pins are not approval pins: production composition must use a separately reviewed release manifest and current policy.

The [evaluation](../quality/local-ocr-evaluation.md) records current limitations, including rotation errors. Complete the accepted PP-OCRv6 tiny/small comparison and actual operator/hardware acceptance before selection.

## Worker and private result boundary

`PaymentOcrRuntimeOptions` requires the private root/store and trusted operator SIDs, a dedicated consumer ID, approved host and model-selection references, and explicit engine paths, limits and reviewed SHA-256 pins. The runtime does not derive approval pins from installed files, seed policies, run migrations or download models. Its pipeline ID must match `documentProcessingPolicies` together with consumer/store, both approval references, active revision and expiry. Configuration is not evidence of host/model acceptance. The production default remains unconfigured. Run schema upgrades explicitly with writers quiesced; migration 35 accepts the genuine v34 pending-source graph and rejects unexplained prior processing, unsafe indexes/options, inconsistent source records or populated new result collections before altering target definitions. Applied migrations 1–34 are unchanged.

The processor claims only `payment.ocr.requested`, with a 30-second lease, 60-second processing deadline and five-attempt limit. The shorter lease permits recovery before the hard deadline. Original access is further bounded by the current read policy and cannot be extended merely by retrying the same claim. Billing supplies the immutable submission/version through a read-only source interface; Documents checks its exact pin, original generation/hash, scanner/decoder records, lifecycle and current policies. A durable service read grant precedes byte access. File I/O and OCR run outside retried Mongo callbacks; final lease/policy/source/grant checks precede the atomic result, audit and outbox receipt. A missing required audit rolls back publication.

Results deduplicate by evidence version, purpose, model checksum and preprocessing version. Committed results recover from the original receipt under a current claim without reading or processing the file again. Transient failures record bounded safe codes and retry; terminal failures do not create suggestions. Late output from a lost lease or expired original-read grant cannot publish. Submitted evidence is immutable in this slice. A future replacement workflow must preserve historical results and coordinate source/pin changes with Documents; it must not reassign old results to a new review.

The API rechecks the current buyer, branch membership, session, declared revision and exact submitted reference before returning a derivative. It repeats original eligibility checks and requires the complete result/audit/receipt graph plus a new derivative-read audit; it returns `private, no-store`. Raw recognized lines stay in private persistence, while the buyer receives bounded field candidates, locations, uncertainty and provenance. Read audits identify actor/resource/version/time/purpose/correlation without copying extracted bank values. The OCR service identity must have no Billing/Accounting write grants; Documents-owned fences and queue/ID operations remain required. Actual database privileges, denied network egress, immutable installations and host acceptance still require deployment verification.

Correction/exception provenance, duplicate-image investigation, independent bank review/allocation, replacement and after-sales remain pending. Required image/security/independence stay mandatory during an OCR outage. Never send originals or extracted bank data to a cloud OCR/general model, notifications or ordinary logs. Retention, restore and service credential/egress checks remain release gates. Local synthetic processor/runtime/browser checks are not production model selection or provider acceptance.

## Finance submitted-payment viewing

`/billing/payment-reviews` requires an explicit current `payment.review.read` grant in the relevant fulfillment scope and `invoice.read` for every referenced invoice. Verified provider/session assurance and active application membership still apply. The Finance v1 template/packs are unchanged; the optional permission suggestion must be deliberately reviewed and granted through access administration. System administration, a Finance label, assignment, or payment verification permission alone grants no private viewer access.

The queue excludes drafts and applies both scopes before pagination. Detail checks the complete immutable declaration/submission/invoice source graph. The Billing-owned read adapter exposes only the submitted reservation, reference and original uploader; it cannot admit uploads, change a draft or pin another submission. Original reads reuse current security eligibility, bounded read leases, actual-file hash verification, required audit and final/renewal authorization. Derivative reads reuse the exact-source result graph and current eligibility without opening the original. Revocation, changed revisions, unavailable policy or required-audit failure prevent the affected read.

The screen presents declared values, issued invoice totals, the submitted original and separate OCR candidates. Invoice totals are explicitly not remaining AR. Preview bytes are checked again against the expected SHA-256, retained only in an in-memory blob and revoked when closed, replaced, offline or unmounted. All four reviewer routes require authenticated POST, exact Origin/CSRF and expected actor; original/OCR reads additionally require the declaration revision and exact evidence reference. Responses are private/no-store. Read audits record the reviewer and resource without copying bank values into logs.

This viewer neither asserts person-level decision independence nor approves funds: those remain mandatory prerequisites of the later financial decision. Approved receiving-account versions, bank corroboration, human correction history, assignments, replacement/rejection, canonical receipt, allocation and after-sales are not enabled by this read permission. Local synthetic access is not a live grant, provider acceptance or launch authorization.
