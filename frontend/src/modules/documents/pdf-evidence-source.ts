import {parsePaymentDraft, type PaymentDraft} from '@/modules/billing/payment-client';
import {parseSupplierDocumentRead, type SupplierDocumentRead} from '@/modules/procurement/supplier-document-client';
import {pdfDocumentOcrOwnerKey, type PdfDocumentOcrOwner} from './pdf-document-ocr-client';
import {RequestNotSentError} from '@/api/client';

export function paymentDocumentOcrOwner(actorId: string, original: PaymentDraft): PdfDocumentOcrOwner {
  const owner = parsePaymentDraft(original); if (!owner.submission || owner.state !== 'submitted') throw new RequestNotSentError(409, 'evidence_read_not_eligible');
  const selected: PdfDocumentOcrOwner = {actorId, sourceId: owner.id, sourceRevision: owner.revision, sourceKind: 'Payment', submissionId: owner.submission.id, evidence: {...owner.submission.evidence}};
  pdfDocumentOcrOwnerKey(selected); return selected;
}
export function supplierDocumentOcrOwner(actorId: string, original: SupplierDocumentRead): PdfDocumentOcrOwner {
  const owner = parseSupplierDocumentRead(original); if (!owner.document?.evidence) throw new RequestNotSentError(409, 'evidence_read_not_eligible');
  const selected: PdfDocumentOcrOwner = {actorId, sourceId: owner.draft.id, sourceRevision: owner.draft.version, sourceKind: 'SupplierInvoice', submissionId: owner.document.id, evidence: {...owner.document.evidence}};
  pdfDocumentOcrOwnerKey(selected); return selected;
}
