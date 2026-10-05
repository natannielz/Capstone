import {z} from 'zod';
import {RequestNotSentError} from '@/api/client';
import {pdfDocumentOcrOwnerKey, supplierPdfFields, type PdfDocumentObservationSelection} from '../documents/pdf-document-ocr-client';
import type {SupplierDocumentRead} from './supplier-document-client';

export {supplierPdfFields};
const integer = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/.test(value) && BigInt(value) < BigInt('9223372036854775806'));
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const evidence = z.object({id: integer, version: z.literal('1'), sha256: hash}).strict();
const observation = z.object({field: z.enum(supplierPdfFields), candidate: z.object({resultId: integer, resultHash: hash, evidence,
  pageIndex: z.number().int().min(0).max(9), field: z.enum(supplierPdfFields)}).strict(), originalReviewed: z.literal(true)}).strict()
  .refine(value => value.field === value.candidate.field);
export const supplierDocumentPdfObservations = z.array(observation).min(1).max(8).refine(values => values.every((value, index) =>
  (index === 0 || supplierPdfFields.indexOf(value.field) > supplierPdfFields.indexOf(values[index - 1].field)) &&
  value.candidate.resultId === values[0].candidate.resultId && value.candidate.resultHash === values[0].candidate.resultHash &&
  value.candidate.evidence.id === values[0].candidate.evidence.id && value.candidate.evidence.sha256 === values[0].candidate.evidence.sha256));
export type SupplierDocumentPdfMatchObservation = z.infer<typeof observation>;
export type RememberedSupplierPdfObservation = {reference: SupplierDocumentPdfMatchObservation; value: string | null; flags: readonly string[]};
export const supplierPdfLabels: Record<typeof supplierPdfFields[number], string> = {document_reference: 'Referensi dokumen', document_date: 'Tanggal dokumen',
  supplier_name: 'Nama pemasok', total_amount: 'Total dokumen', currency: 'Mata uang', purchase_order_reference: 'Referensi pesanan',
  delivery_reference: 'Referensi pengiriman', source_sku: 'Kode barang'};
const invalid = (): never => {throw new RequestNotSentError(409, 'supplier_document_view_stale');};
/** Called after the private original has been reviewed and its exact page/field explicitly selected; never copies a value into manual facts. */
export function rememberSupplierDocumentPdfObservation(actorId: string, owner: SupplierDocumentRead,
  selection: PdfDocumentObservationSelection): RememberedSupplierPdfObservation {
  if (!owner.document?.evidence) return invalid();
  const expected = {actorId, sourceId: owner.draft.id, sourceRevision: owner.draft.version, submissionId: owner.document.id,
    sourceKind: 'SupplierInvoice' as const, evidence: owner.document.evidence};
  if (pdfDocumentOcrOwnerKey(expected) !== pdfDocumentOcrOwnerKey(selection.source) || selection.candidate.field !== selection.reference.field ||
    selection.reference.evidence.id !== expected.evidence.id || selection.reference.evidence.version !== expected.evidence.version ||
    selection.reference.evidence.sha256 !== expected.evidence.sha256) return invalid();
  const parsed = observation.safeParse({field: selection.reference.field, candidate: selection.reference, originalReviewed: true});
  if (!parsed.success) return invalid();
  return structuredClone({reference: parsed.data, value: selection.candidate.value, flags: selection.candidate.flags});
}
