# Supplier invoices and SPH statements

This workflow records supplier originals and supports human matching. It does not pay a supplier, clear an advance, post a payable or release quarantined stock. SPH means the supplier debt statement used in the preserved BPP VI process. It is distinct from a purchase quotation.

## Access and source records

- Use an actual approved purchase order in the operator's fulfillment location. Invoice matches select actual submitted inspection lines from the same supplier and location, retaining original package definitions and observed conditions.
- `procurement.supplier.document.record` records documents; `procurement.supplier.document.match` records matching reviews. Both require a current verified person, appropriate scoped grants and MFA within five minutes.
- `procurement.supplier.document.evidence.read` separately permits original-image and OCR reading. Ordinary procurement list access does not grant private evidence access. Draft evidence remains restricted to its author.
- Roles do not automatically acquire these permissions. Existing independent access administration applies.

## Operator flow

1. Select the actual purchase order, document kind and original reference. Enter only facts that can be read from the document. Unknown date, quantity or money remains blank; record why facts or totals are unresolved. Invoice rows contain package quantities and explicit money components. SPH rows contain invoice references and statement amounts without invented package quantities.
2. Upload the original PNG/JPEG photograph. Existing image limits apply: at most 10 MiB, one image/page and the configured pixel/dimension limits. Admission, private publication and clean scan/full image decoding precede eligibility. PDF/multipage extraction is not supported by this image path.
3. Review and submit the eligible original. Submission pins its exact bytes and creates the durable OCR request. An OCR request or a high confidence result does not verify the invoice or move money/stock.
4. Compare the original, entered facts, OCR suggestions and actual source records. OCR retains its complete bounded recognized lines, positions, confidence, model/runtime provenance and uncertainty flags. Ambiguous or missing reference/code suggestions remain unresolved. Deliberately choose the real source; do not infer a package from its name.
5. Confirm exact invoice matches or record unresolved differences. Partial allocations have explicit amounts and quantities; no tax tolerance or proportional rounding policy is invented. Exact matches claim document and physical-inspection capacity atomically. Damage, expiry and opened-package observations may overlap; matching does not establish healthy or saleable stock.
6. Reconcile SPH rows to real invoice-match records. A statement never consumes those inspection quantities a second time. It cannot create another supplier payable merely by repeating an invoice reference.

Original facts stay immutable. Reviewed transcriptions and matching decisions retain the actor, reason and source bindings. Duplicate identities or missing claim/receipt/history prevent further claims until resolved through an applicable approved process. Differences requiring a corrected supplier document, price exception, fiscal policy or stock disposition remain open.

Original-image and OCR reads recheck current private-file eligibility. Nonfinancial match history relies on the exact original submission and pin recorded at submission; it does not reopen the file for each match. Revoked file access therefore blocks new viewing without erasing historical matches or their recovery. A recorded match is not proof of current file accessibility, supplier-payment approval or stock-release approval.

## Recovery and processing

After an uncertain response, recover by the original operation key and actor. A page reload must not silently resubmit. Refresh current source capacity and review facts before a genuinely new command.

The API uses the existing `Evidence:PrivateWindows` image store. The worker uses explicit `Evidence:SupplierDocumentOcr` configuration and the selected pinned **Tesseract 5.5.3/tesserocr 2.11.0 `ind+eng`** engine. The image pipeline is `bounded-gray-orient4-deskew10-contrast1-psm6-ind+eng-v2`, with the separate `supplier-document-labels-id-en-v1` parser. Bounded orientation/skew/contrast processing preserves the original hash and maps text locations back to the original image; ambiguous orientation or missing facts remain unknown for human review. Recognized output is bounded to 128 lines and 16,384 characters; overflow fails rather than truncating into a successful match. Only eligible PNG/JPEG originals are supported today, not PDFs or delivery-document OCR.

Configure the same approved private store, reviewed binary/model pins and finite current `supplierDocumentProcessingPolicies` approval for the **exact pipeline ID, consumer, store and host/model release references**. Recalculate the pipeline from the actual pinned helper/runtime/models/parser after an upgrade; an old pipeline approval does not authorize new preprocessing. Missing configuration/approval leaves processing unavailable. See the [OCR operator runbook](document-processing.md) for the selected release, restricted host requirements and synthetic benchmark limitations.

The admitted private-read deadline is captured before OCR and cannot be renewed by later source revalidation. Expired output is rejected before result persistence, with timestamp checks after asynchronous identifier allocation and final transaction fences; it must not leave a partial extraction or financial effect. Retry needs genuine new admission. The native supplier fixture's ten-second private lease stays within the existing cap but approves no production policy or default. Measure and independently approve the actual consumer/store/host budget; do not widen it just to reproduce a fixture pass.

Read OCR and original files only through the authorized private endpoints. Every derivative remains bound to the actual submitted document, immutable original pin and current evidence eligibility. Original bytes, raw OCR and full supplier document details must not appear in public URLs or operational logs.

## Acceptance boundary

Implementation and local checks are tracked in the [single progress ledger](../quality/production-progress.md). Synthetic people, policies, documents, scanner responses and local fixtures do not establish production acceptance. Real supplier examples, approved tax/rounding and discrepancy handling, private-host/scanner/model approvals, deployment connections and branch pilot evidence remain operational inputs. Financial clearance and stock posting additionally require their real source, accounting and disposition controls.
