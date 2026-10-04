import { z } from 'zod';

export const paymentPdfFields = ['amount', 'currency', 'transfer_date', 'transfer_time', 'recipient', 'account', 'bank', 'reference'] as const;
export const supplierPdfFields = ['document_reference', 'document_date', 'supplier_name', 'total_amount', 'currency', 'purchase_order_reference', 'delivery_reference', 'source_sku'] as const;
const fieldNames = [...paymentPdfFields, ...supplierPdfFields] as const;
const integerPattern = /^[1-9][0-9]{0,18}$(?![\s\S])/;
const integer = z.string().regex(integerPattern).refine(value => integerPattern.test(value) && BigInt(value) < BigInt('9223372036854775807'));
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/);
const fractionPattern = /^(?:0(?:\.[0-9]{1,28})?|1(?:\.0{1,28})?)$(?![\s\S])/;
const fraction = z.string().regex(fractionPattern);
const unit = BigInt(10) ** BigInt(28);
const scaled = (value: string) => { if (!fractionPattern.test(value)) return BigInt(-1); const [whole, part = ''] = value.split('.'); return BigInt(whole) * unit + BigInt(part.padEnd(28, '0')); };
const text = (maximum: number) => z.string().min(1).max(maximum).refine(value => value === value.trim() && value === value.normalize('NFC') &&
  !/[\p{Cc}\p{Cf}]/u.test(value) && !Array.from(value).some(character => character.length === 1 && /[\uD800-\uDFFF]/u.test(character)));
const codes = z.array(z.string().regex(/^[a-z_]{1,64}$(?![\s\S])/)).max(16).refine(values => new Set(values).size === values.length);
const fieldFlags = z.array(z.enum(['unknown', 'ambiguous', 'low_confidence', 'unparsed', 'masked', 'timezone_unverified'])).max(16)
  .refine(values => new Set(values).size === values.length);
const finite = z.number().finite().min(-14400).max(14400);
const point = z.object({ x: finite, y: finite }).strict();
const corners = z.tuple([point, point, point, point]);
const box = z.tuple([fraction, fraction, fraction, fraction]).refine(value => scaled(value[2]) > BigInt(0) && scaled(value[3]) > BigInt(0) &&
  scaled(value[0]) + scaled(value[2]) <= unit && scaled(value[1]) + scaled(value[3]) <= unit);
const evidence = z.object({ id: integer, version: integer, sha256: hash }).strict();
const source = z.object({ actorId: integer, sourceId: integer, sourceRevision: integer, submissionId: integer,
  sourceKind: z.enum(['Payment', 'SupplierInvoice']), evidence }).strict();
const provenance = z.object({ engineVersion: text(128), modelVersion: text(128), modelSha256: hash, runtimeSha256: hash, preprocessingVersion: text(128) }).strict();
const rasterProvenance = z.object({ pipeline: text(128), wrapper: text(128), pdfium: text(128), wheelSha256: hash, pillow: text(32), runtimeSha256: hash }).strict();
const near = (first: number, second: number) => Math.abs(first - second) <= .001;
const samePoint = (first: {x: number; y: number}, second: {x: number; y: number}) => near(first.x, second.x) && near(first.y, second.y);
const raster = z.object({ pageIndex: z.number().int().min(0).max(9), width: z.number().int().min(1).max(4096), height: z.number().int().min(1).max(4096),
  rotationDegrees: z.union([z.literal(0), z.literal(90), z.literal(180), z.literal(270)]), length: z.number().int().min(57).max(10 * 1024 * 1024), sha256: hash,
  canvasSize: z.tuple([finite.positive(), finite.positive()]), visibleBox: z.tuple([finite, finite, finite, finite]), pixelCornersInPdfCanvas: corners,
  reviewReasons: z.array(z.enum(['human_review_required', 'no_visible_marks'])).min(1).max(2) }).strict().refine(value => {
    const [left, bottom, right, top] = value.visibleBox;
    const expected = [{x: left, y: top}, {x: right, y: top}, {x: right, y: bottom}, {x: left, y: bottom}];
    return right > left && top > bottom && value.width * value.height <= 4_000_000 && value.reviewReasons[0] === 'human_review_required' &&
      (value.reviewReasons.length === 1 || value.reviewReasons[1] === 'no_visible_marks') &&
      Math.ceil(value.canvasSize[0] * (25 / 9)) === value.width && Math.ceil(value.canvasSize[1] * (25 / 9)) === value.height &&
      near(value.canvasSize[0], value.rotationDegrees % 180 === 0 ? right - left : top - bottom) &&
      near(value.canvasSize[1], value.rotationDegrees % 180 === 0 ? top - bottom : right - left) &&
      value.pixelCornersInPdfCanvas.every((item, index) => samePoint(item, expected[(index + 4 - value.rotationDegrees / 90) % 4]));
  });
const preprocessing = z.object({ orientationDegrees: z.union([z.literal(0), z.literal(90), z.literal(180), z.literal(270)]),
  deskewDegrees: z.number().finite().min(-10).max(10).refine(value => Number.isInteger(value * 4)), contrastApplied: z.boolean(),
  workingWidth: z.number().int().min(0).max(4800), workingHeight: z.number().int().min(0).max(4800), orientationScore: z.number().finite().min(0).max(1),
  orientationMargin: z.number().finite().min(0).max(1), reviewReasons: z.array(z.enum(['no_text', 'orientation_unresolved', 'skew_out_of_range', 'low_text_confidence'])).max(1) }).strict()
  .refine(value => value.workingWidth * value.workingHeight <= 5_500_000 && value.orientationMargin <= value.orientationScore);
const day = (value: string) => /^\d{4}-\d{2}-\d{2}$(?![\s\S])/.test(value) && !value.startsWith('0000') &&
  Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value;
const fieldValue = (field: typeof fieldNames[number], value: string | null) => value === null || (
  field === 'amount' || field === 'total_amount' ? /^(?:0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])/.test(value) :
  field === 'currency' ? value === 'IDR' : field === 'transfer_date' || field === 'document_date' ? day(value) :
  field === 'transfer_time' ? /^(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]$(?![\s\S])/.test(value) :
  field === 'account' ? /^[0-9*Xx]{4,34}$(?![\s\S])/.test(value) : true);
const candidate = z.object({ field: z.enum(fieldNames), value: text(256).nullable(), confidence: fraction.nullable(), boundingBox: box.nullable(),
  pdfCanvasCorners: corners.nullable(), flags: fieldFlags }).strict().refine(value => (value.boundingBox === null) === (value.pdfCanvasCorners === null) &&
    (value.confidence === null) === (value.boundingBox === null) && (value.value === null || value.confidence !== null) &&
    (value.value === null || !value.flags.some(flag => ['unknown', 'ambiguous', 'unparsed'].includes(flag))) &&
    fieldValue(value.field, value.value) &&
    (value.value !== null || value.flags.some(flag => ['unknown', 'ambiguous', 'unparsed'].includes(flag))) &&
    (value.confidence === null || (scaled(value.confidence) < BigInt(9) * unit / BigInt(10)) === value.flags.includes('low_confidence')));
const line = z.object({ text: text(512), confidence: fraction, boundingBox: box, pdfCanvasCorners: corners }).strict();
const page = z.object({ raster, ocrProvenance: provenance, preprocessing, lines: z.array(line).max(128), fields: z.array(candidate).length(8), reviewReasons: codes }).strict();
const fieldReview = z.object({ field: z.enum(fieldNames), observations: z.array(z.object({ pageIndex: z.number().int().min(0).max(9), candidate }).strict()).min(1).max(10),
  reviewReasons: z.array(z.enum(['human_review_required', 'unknown_on_page', 'ambiguous_on_page', 'contradictory_pages', 'repeated_across_pages'])).min(1).max(5) }).strict();
const result = z.object({ id: integer, resultHash: hash, extractedAt: z.string().datetime({offset: true}), purpose: z.enum(['Payment', 'SupplierInvoice']),
  rasterProvenance, ocrProvenance: provenance, pages: z.array(page).min(1).max(10), fields: z.array(fieldReview).length(8), requiresHumanReview: z.literal(true) }).strict();
const safeFailures = ['ocr_unavailable', 'ocr_timeout', 'ocr_invalid_output', 'ocr_integrity_mismatch', 'ocr_invalid_media', 'ocr_original_unavailable',
  'pdf_document_deadline', 'pdf_document_deadline_invalid', 'pdf_document_source_mismatch', 'pdf_document_invalid_output', 'pdf_document_page_limit', 'pdf_document_unavailable',
  'pdf_raster_source_mismatch', 'pdf_raster_source_limit', 'pdf_raster_malformed_pdf', 'pdf_raster_encrypted_pdf', 'pdf_raster_unsupported_forms', 'pdf_raster_unsupported_attachments',
  'pdf_raster_unsupported_javascript', 'pdf_raster_page_limit', 'pdf_raster_geometry_limit', 'pdf_raster_total_pixel_limit', 'pdf_raster_output_limit', 'pdf_raster_deadline',
  'pdf_raster_render_failed', 'pdf_raster_unavailable', 'pdf_raster_configuration', 'pdf_raster_runtime_pin', 'pdf_raster_process_failed', 'pdf_raster_invalid_output'] as const;
const viewSchema = z.object({ sourceId: integer, sourceRevision: integer, submissionId: integer, sourceKind: z.enum(['Payment', 'SupplierInvoice']), evidence,
  state: z.enum(['queued', 'running', 'retry', 'dead', 'succeeded']), result: result.nullable(), failureCode: z.enum(safeFailures).nullable() }).strict()
  .refine(value => (value.state === 'succeeded') === (value.result !== null) && (['retry', 'dead'].includes(value.state) || value.failureCode === null));

export type PdfDocumentOcrOwner = z.infer<typeof source>;
export type PdfDocumentOcrView = z.infer<typeof viewSchema>;
export type PdfDocumentOcrPage = z.infer<typeof page>;
export type PdfDocumentOcrCandidate = z.infer<typeof candidate>;
export type PdfDocumentCandidateReference = { resultId: string; resultHash: string; evidence: PdfDocumentOcrOwner['evidence']; pageIndex: number; field: PdfDocumentOcrCandidate['field'] };
export type PdfDocumentObservationSelection = { source: PdfDocumentOcrOwner; reference: PdfDocumentCandidateReference; candidate: PdfDocumentOcrCandidate };
export class PdfDocumentOcrError extends Error {
  constructor(public readonly code: 'pdf_document_ocr_response_invalid' | 'pdf_document_ocr_stale') { super(code); this.name = 'PdfDocumentOcrError'; }
}
const invalid = (): never => { throw new PdfDocumentOcrError('pdf_document_ocr_response_invalid'); };
const equal = (first: unknown, second: unknown) => JSON.stringify(first) === JSON.stringify(second);
const parsedOwner = (value: unknown) => { const parsed = source.safeParse(value); return parsed.success ? parsed.data : invalid(); };
export const pdfDocumentOcrOwnerKey = (owner: PdfDocumentOcrOwner) => JSON.stringify(parsedOwner(owner));
function mapped(rasterPage: PdfDocumentOcrPage['raster'], normalized: [string, string, string, string], actual: z.infer<typeof corners>): boolean {
  const [x, y, width, height] = normalized.map(Number);
  const [topLeft, topRight, , bottomLeft] = rasterPage.pixelCornersInPdfCanvas;
  const at = (across: number, down: number) => ({x: topLeft.x + across * (topRight.x - topLeft.x) + down * (bottomLeft.x - topLeft.x),
    y: topLeft.y + across * (topRight.y - topLeft.y) + down * (bottomLeft.y - topLeft.y)});
  return actual.every((value, index) => samePoint(value, [at(x, y), at(x + width, y), at(x + width, y + height), at(x, y + height)][index]));
}

export function parsePdfDocumentOcr(value: unknown, ownerInput: PdfDocumentOcrOwner): PdfDocumentOcrView {
  const owner = parsedOwner(ownerInput);
  try { if (new TextEncoder().encode(JSON.stringify(value)).length > 2 * 1024 * 1024) return invalid(); } catch { return invalid(); }
  const parsed = viewSchema.safeParse(value); if (!parsed.success) return invalid(); const view = parsed.data;
  if (view.sourceId !== owner.sourceId || view.sourceRevision !== owner.sourceRevision || view.submissionId !== owner.submissionId ||
    view.sourceKind !== owner.sourceKind || !equal(view.evidence, owner.evidence)) return invalid();
  if (!view.result) return view;
  const extraction = view.result, names = owner.sourceKind === 'Payment' ? paymentPdfFields : supplierPdfFields;
  if (extraction.purpose !== owner.sourceKind || extraction.pages.reduce((sum, item) => sum + item.raster.width * item.raster.height, 0) > 25_000_000 ||
    extraction.pages.reduce((sum, item) => sum + item.raster.length, 0) > 12 * 1024 * 1024 ||
    extraction.pages.reduce((sum, item) => sum + item.lines.reduce((count, item) => count + item.text.length, 0), 0) > 65536) return invalid();
  for (const [index, item] of extraction.pages.entries()) {
    if (item.raster.pageIndex !== index || !equal(item.ocrProvenance, extraction.ocrProvenance) || !item.fields.every((field, fieldIndex) => field.field === names[fieldIndex]) ||
      item.lines.reduce((sum, line) => sum + line.text.length, 0) > 16384 || !item.reviewReasons.includes('human_review_required')) return invalid();
    const prep = item.preprocessing, fallback = prep.reviewReasons.some(reason => ['orientation_unresolved', 'skew_out_of_range'].includes(reason)) || !prep.workingWidth || !prep.workingHeight;
    if (fallback ? item.lines.length !== 0 || prep.workingWidth !== 0 || prep.workingHeight !== 0 || prep.orientationDegrees !== 0 || prep.deskewDegrees !== 0 || prep.reviewReasons.length !== 1 :
      prep.orientationScore < .65 || prep.orientationMargin < .10 || (item.lines.length === 0) !== prep.reviewReasons.includes('no_text')) return invalid();
    if (item.lines.some(line => !mapped(item.raster, line.boundingBox, line.pdfCanvasCorners)) ||
      item.fields.some(field => field.boundingBox !== null && (!mapped(item.raster, field.boundingBox, field.pdfCanvasCorners!) ||
        !item.lines.some(line => line.confidence === field.confidence && equal(line.boundingBox, field.boundingBox))))) return invalid();
  }
  for (const [index, field] of extraction.fields.entries()) {
    if (field.field !== names[index] || field.observations.length !== extraction.pages.length ||
      field.observations.some((item, pageIndex) => item.pageIndex !== pageIndex || !equal(item.candidate, extraction.pages[pageIndex].fields[index]))) return invalid();
    const expected = ['human_review_required'];
    if (field.observations.some(item => item.candidate.value === null)) expected.push('unknown_on_page');
    if (field.observations.some(item => item.candidate.flags.includes('ambiguous'))) expected.push('ambiguous_on_page');
    const known = field.observations.flatMap(item => item.candidate.value === null ? [] : [item.candidate.value]);
    if (new Set(known).size > 1) expected.push('contradictory_pages'); else if (known.length > 1) expected.push('repeated_across_pages');
    if (!equal(field.reviewReasons, expected)) return invalid();
  }
  return view;
}

/** An observation reference is never a correction, bank review, payment or document approval. */
export function selectPdfDocumentObservation(viewInput: PdfDocumentOcrView, owner: PdfDocumentOcrOwner, pageIndex: number,
  field: PdfDocumentOcrCandidate['field']): PdfDocumentObservationSelection {
  const view = parsePdfDocumentOcr(viewInput, owner), extraction = view.result;
  if (!extraction || !Number.isInteger(pageIndex)) return invalid();
  const item = extraction.pages[pageIndex]?.fields.find(item => item.field === field); if (!item) return invalid();
  return structuredClone({source: parsedOwner(owner), reference: {resultId: extraction.id, resultHash: extraction.resultHash, evidence: view.evidence, pageIndex, field}, candidate: item});
}

/** The maintained gateway supplies actor/CSRF/origin transport; the owner calls cancel on source/session change and unmount. */
export function createPdfDocumentOcrReader(options: { read: (owner: PdfDocumentOcrOwner, signal: AbortSignal) => Promise<unknown>;
  currentActorId: () => string | null; currentOwner: () => PdfDocumentOcrOwner | null }) {
  let active: AbortController | null = null;
  const cancel = () => { active?.abort(); active = null; };
  return {cancel, async query(ownerInput: PdfDocumentOcrOwner): Promise<PdfDocumentOcrView> {
    cancel(); const owner = parsedOwner(ownerInput), key = pdfDocumentOcrOwnerKey(owner), controller = new AbortController(); active = controller;
    const current = () => { const selected = options.currentOwner(); return active === controller && !controller.signal.aborted &&
      options.currentActorId() === owner.actorId && selected !== null && pdfDocumentOcrOwnerKey(selected) === key; };
    const requireCurrent = () => { if (!current()) { controller.abort(); if (active === controller) active = null; throw new PdfDocumentOcrError('pdf_document_ocr_stale'); } };
    try { requireCurrent(); const raw = await options.read(owner, controller.signal); requireCurrent(); const shown = parsePdfDocumentOcr(raw, owner); requireCurrent(); return shown; }
    finally { if (active === controller) active = null; }
  }};
}
