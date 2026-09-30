# Local document processing

## Current boundary

The internal `ILocalOcrEngine` now has a real Windows Tesseract candidate and bounded immutable extraction contracts. The helper accepts one assigned image stream and returns suggestions with text locations, confidence and model/runtime/preprocessing provenance. The backend remains .NET; this isolated Python/native helper has no application API, Mongo credentials or financial operations.

This candidate is **not registered in the application worker**. `payment.ocr.requested` jobs remain durably queued. No extraction result collection, scoped result endpoint, correction workflow or browser result panel exists at this checkpoint. Keep those states pending; do not represent a submitted payment as OCR-completed or verified. Supplier invoice and delivery-note extraction explicitly return unsupported rather than pretending the payment parser applies to them.

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

## Next owning integration

Connect the existing submission outbox through a current, approved kind-specific worker lease. Billing must supply the exact owning submission/version and Documents must supply its pinned, currently eligible original; the worker receives no payment approval rights. Recheck lease/policy/lifecycle/source identity in the final transaction; persist immutable bounded result, audit and receipt together. Deduplicate by evidence version, purpose, model checksum and preprocessing version; stale/superseded output remains history, never a current review replacement. Recover crash/unknown commits from the original job receipt.

Expose queued/running/succeeded/failed/dead and scoped suggestions through the owning API and real browser journey; add correction/exception provenance and independent bank review afterward. Required image/security/independence remain mandatory during an OCR outage. Persist raw text only in private scoped storage; never send originals or extracted bank data to a cloud OCR/general model, notifications or ordinary logs. Retention, restore and service credential/egress checks remain release gates. This follows the existing payment plan, not a new helper-specific work sequence.
