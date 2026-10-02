import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';
import { parsePaymentEvidenceStatus, parsePaymentReservation, type PaymentEvidenceStatus } from '../billing/payment-client';
import { parseCaseView, type AfterSalesCase } from './case-client';
import { parseReturnFinancialApprovalResult, type ReturnFinancialApprovalResult } from './return-financial-approval-client';

const { id, positive, hash, text } = paymentAllocationSchemas;
const day = z.string().regex(/^[2-9][0-9]{3}-[0-9]{2}-[0-9]{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const timestamp = z.string().datetime({ offset: true });
const reference = z.object({ id, version: z.literal('1'), sha256: hash }).strict();
const input = z.object({ beneficiaryId: id, expectedBeneficiaryRevision: id, payingAccountId: id, payingAccountVersionId: id,
  amount: positive, transferDate: day, transferReference: text(160) }).strict();
const draftResult = z.object({ draftId: id, revision: z.literal('1'), approvalId: id, state: z.literal('awaiting_private_evidence'), amount: positive, createdAt: timestamp }).strict();
const result = z.object({ payoutId: id, draftId: id, approvalId: id, state: z.literal('recorded_unverified'), amount: positive, recordedAt: timestamp }).strict();
const view = z.object({ approvalId: id, caseId: id, returnId: id, invoiceId: id, amount: positive, beneficiaryId: id, beneficiaryRevision: id,
  beneficiaryName: text(160), beneficiaryBankCode: text(80), beneficiaryAccountSuffix: z.string().regex(/^[0-9]{4}$(?![\s\S])/),
  payingAccountId: id, payingAccountVersionId: id, payingBankCode: text(80), payingAccountSuffix: z.string().regex(/^[0-9]{4}$(?![\s\S])/),
  payoutRecordingProcedureReference: text(256), beneficiaryVerificationProcedureReference: text(256), input, payout: result.nullable(), draft: draftResult.nullable(),
  evidence: reference.nullable(), reservationId: id.nullable() }).strict().refine(value => value.input.amount === value.amount && value.input.beneficiaryId === value.beneficiaryId &&
    value.input.expectedBeneficiaryRevision === value.beneficiaryRevision && value.input.payingAccountId === value.payingAccountId && value.input.payingAccountVersionId === value.payingAccountVersionId &&
    (value.draft === null || value.draft.approvalId === value.approvalId && value.draft.amount === value.amount) &&
    (value.payout === null || value.draft !== null && value.payout.draftId === value.draft.draftId && value.payout.approvalId === value.approvalId && value.payout.amount === value.amount && Date.parse(value.payout.recordedAt) >= Date.parse(value.draft.createdAt)) &&
    (value.reservationId === null) === (value.evidence === null) && (value.evidence === null || value.draft !== null) && (value.payout === null || value.evidence !== null));
const preview = z.object({ view, binding: hash }).strict();
const uploadOutcome = z.object({ state: z.enum(['quarantined', 'interrupted', 'unresolved', 'unknown', 'publish_pending', 'recovery_required']), reference: reference.nullable() }).strict()
  .refine(value => (value.state === 'quarantined') === (value.reference !== null));
export type RefundPayoutDraftResult = z.infer<typeof draftResult>;
export type RefundPayoutResult = z.infer<typeof result>;
export type RefundPayoutView = z.infer<typeof view>;
export type RefundPayoutPreview = z.infer<typeof preview>;
export type ApprovedRefundView = { approval: ReturnFinancialApprovalResult | null; payout: RefundPayoutResult | null; draft: RefundPayoutDraftResult | null };
function invalid(): never { throw new AccessFailure(502, 'refund_payout_response_invalid'); }
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const checked = schema.safeParse(value); return checked.success ? checked.data : invalid(); }
function data<T>(response: { data?: T; error?: unknown; response: Response }): T {
  if (!response.response.ok || response.data === undefined) { const failure = response.error && typeof response.error === 'object' ? response.error as Record<string, unknown> : {};
    throw new AccessFailure(response.response.status, typeof failure.code === 'string' ? failure.code : 'request_failed'); } return response.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': parse(id, actorId) });
const check = (current: () => boolean) => { if (!current()) throw new RequestNotSentError(409, 'refund_payout_view_stale'); };
export function canRecordRefundPayout(actions: readonly string[]) { return ['disbursement.record', 'case.read', 'invoice.read', 'payment.review.read'].every(action => actions.includes(action)); }
export function refundPayoutOwner(key: string, operation: 'draft' | 'record' | 'reserve') {
  const prefix = { draft: 'rpd', record: 'rpr', reserve: 'rpe' }[operation];
  const match = new RegExp('^' + prefix + '\\.([1-9][0-9]{0,18})\\.([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$(?![\\s\\S])').exec(key);
  if (!match || match[2] === '00000000-0000-0000-0000-000000000000') return invalid(); return parse(id, match[1]);
}
export function parseRefundPayoutDraftResult(value: unknown, key: string) { const saved = parse(draftResult, value); return saved.approvalId === refundPayoutOwner(key, 'draft') ? saved : invalid(); }
export function parseRefundPayoutResult(value: unknown, key: string) { const saved = parse(result, value); return saved.draftId === refundPayoutOwner(key, 'record') ? saved : invalid(); }
export function parseRefundPayoutView(value: unknown, owner: AfterSalesCase, approval: ReturnFinancialApprovalResult) {
  parseCaseView(owner); parseReturnFinancialApprovalResult(approval, 'fa.' + owner.id + '.' + approval.returnId + '.refund.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
  const shown = parse(view, value);
  if (owner.state !== 'return_authorized' || approval.action !== 'refund' || approval.state !== 'refund_reserved' || shown.approvalId !== approval.financialApprovalId || shown.caseId !== owner.id || shown.returnId !== approval.returnId ||
    shown.invoiceId !== owner.source.invoiceId || shown.caseId !== approval.caseId || shown.invoiceId !== approval.invoiceId || shown.amount !== approval.reservedRefund ||
    shown.draft !== null && Date.parse(shown.draft.createdAt) < Date.parse(approval.recordedAt)) return invalid(); return shown;
}
export function prepareRefundPayoutDraft(actorId: string, owner: AfterSalesCase, approval: ReturnFinancialApprovalResult, value: RefundPayoutPreview, reason: string) {
  const shown = parse(preview, value); parseRefundPayoutView(shown.view, owner, approval);
  if (shown.view.draft || shown.view.payout || shown.view.evidence || shown.view.reservationId) return invalid();
  return Object.freeze({ actorId: parse(id, actorId), approvalId: shown.view.approvalId, amount: shown.view.amount,
    body: Object.freeze({ binding: shown.binding, input: Object.freeze({ ...shown.view.input }), reason: parse(text(1000), reason) }) });
}
export function prepareRefundPayoutRecord(actorId: string, owner: AfterSalesCase, approval: ReturnFinancialApprovalResult, value: RefundPayoutPreview, image: PaymentEvidenceStatus, reason: string) {
  const shown = parse(preview, value), evidence = parsePaymentEvidenceStatus(image); parseRefundPayoutView(shown.view, owner, approval);
  if (!shown.view.draft || shown.view.payout || !evidence.reference || evidence.scanStatus !== 'clean' || evidence.decoderStatus !== 'decoded' || evidence.safeFailure !== null ||
    shown.view.reservationId !== evidence.reservationId || JSON.stringify(shown.view.evidence) !== JSON.stringify(evidence.reference)) throw new RequestNotSentError(409, 'evidence_read_not_eligible');
  return Object.freeze({ actorId: parse(id, actorId), draftId: shown.view.draft.draftId, revision: shown.view.draft.revision, approvalId: shown.view.approvalId, amount: shown.view.amount,
    body: Object.freeze({ binding: shown.binding, reservationId: evidence.reservationId, reference: Object.freeze({ ...evidence.reference }), reason: parse(text(1000), reason) }) });
}
export function createRefundPayoutGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async approved(actorId: string, owner: AfterSalesCase, returnId: string): Promise<ApprovedRefundView> {
      parseCaseView(owner); if (owner.state !== 'return_authorized') return invalid();
      const raw = parse(z.object({ approval: z.unknown().nullable(), payout: result.nullable(), draft: draftResult.nullable() }).strict(), data(await client.POST('/api/v1/billing/cases/{id}/returns/{returnId}/refund-payout/query', {
        body: { purpose: 'refund-payout' }, params: { path: { id: owner.id, returnId: parse(id, returnId) }, header: headers(actorId) }, cache: 'no-store' })));
      const approval = raw.approval === null ? null : parseReturnFinancialApprovalResult(raw.approval, 'fa.' + owner.id + '.' + returnId + '.refund.aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
      if (approval === null ? raw.payout !== null || raw.draft !== null : approval.invoiceId !== owner.source.invoiceId ||
        raw.payout !== null && (raw.payout.approvalId !== approval.financialApprovalId || raw.payout.amount !== approval.reservedRefund) ||
        raw.draft !== null && (raw.draft.approvalId !== approval.financialApprovalId || raw.draft.amount !== approval.reservedRefund)) return invalid();
      return { approval, payout: raw.payout, draft: raw.draft };
    },
    async previewDraft(actorId: string, owner: AfterSalesCase, approval: ReturnFinancialApprovalResult, transferDate: string, transferReference: string) {
      const transfer = parse(z.object({ transferDate: day, transferReference: text(160) }).strict(), { transferDate, transferReference });
      const shown = parse(preview, data(await client.POST('/api/v1/billing/refund-approvals/{id}/payout-drafts/preview', { body: transfer,
        params: { path: { id: parse(id, approval.financialApprovalId) }, header: headers(actorId) }, cache: 'no-store' })));
      parseRefundPayoutView(shown.view, owner, approval); if (shown.view.input.transferDate !== transferDate || shown.view.input.transferReference !== transferReference) return invalid(); return shown;
    },
    async createDraft(command: ReturnType<typeof prepareRefundPayoutDraft>, key: string, current: () => boolean) {
      check(current); if (refundPayoutOwner(key, 'draft') !== command.approvalId) return invalid();
      const saved = parseRefundPayoutDraftResult(data(await client.POST('/api/v1/billing/refund-approvals/{id}/payout-drafts', { body: command.body,
        params: { path: { id: command.approvalId }, header: { ...headers(command.actorId), 'Idempotency-Key': key } }, cache: 'no-store' })), key);
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved.amount === command.amount ? saved : invalid();
    },
    async read(actorId: string, owner: AfterSalesCase, approval: ReturnFinancialApprovalResult, draftId: string) {
      const shown = parseRefundPayoutView(data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/query', { body: { purpose: 'refund-payout' },
        params: { path: { id: parse(id, draftId) }, header: headers(actorId) }, cache: 'no-store' })), owner, approval);
      return shown.draft?.draftId === draftId ? shown : invalid();
    },
    async previewRecord(actorId: string, owner: AfterSalesCase, approval: ReturnFinancialApprovalResult, saved: RefundPayoutView, image: PaymentEvidenceStatus) {
      const before = parseRefundPayoutView(saved, owner, approval), evidence = parsePaymentEvidenceStatus(image); if (!before.draft || before.payout || !evidence.reference) return invalid();
      const shown = parse(preview, data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/preview', { body: { reservationId: evidence.reservationId, reference: evidence.reference },
        params: { path: { id: before.draft.draftId }, header: { ...headers(actorId), 'If-Match': '"' + before.draft.revision + '"' } }, cache: 'no-store' })));
      parseRefundPayoutView(shown.view, owner, approval);
      if (JSON.stringify(shown.view.input) !== JSON.stringify(before.input) || JSON.stringify(shown.view.draft) !== JSON.stringify(before.draft)) return invalid(); return shown;
    },
    async record(command: ReturnType<typeof prepareRefundPayoutRecord>, key: string, current: () => boolean) {
      check(current); if (refundPayoutOwner(key, 'record') !== command.draftId) return invalid();
      const saved = parseRefundPayoutResult(data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/record', { body: command.body,
        params: { path: { id: command.draftId }, header: { ...headers(command.actorId), 'If-Match': '"' + command.revision + '"', 'Idempotency-Key': key } }, cache: 'no-store' })), key);
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved.approvalId === command.approvalId && saved.amount === command.amount ? saved : invalid();
    },
    async recoverDraft(actorId: string, key: string) {
      const recovered = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: draftResult.nullable() }).strict(), data(await client.POST('/api/v1/billing/refund-approvals/{id}/payout-drafts/operations/query', {
        body: { purpose: 'operation-recovery', key }, params: { path: { id: refundPayoutOwner(key, 'draft') }, header: headers(actorId) }, cache: 'no-store' })));
      if (recovered.status === 'not_confirmed' && recovered.result === null) return null;
      return recovered.status === 'committed' && recovered.result ? { state: 'committed' as const, result: parseRefundPayoutDraftResult(recovered.result, key) } : invalid();
    },
    async recoverRecord(actorId: string, key: string) {
      const recovered = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), result: result.nullable() }).strict(), data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/operations/query', {
        body: { purpose: 'operation-recovery', key }, params: { path: { id: refundPayoutOwner(key, 'record') }, header: headers(actorId) }, cache: 'no-store' })));
      if (recovered.status === 'not_confirmed' && recovered.result === null) return null;
      return recovered.status === 'committed' && recovered.result ? { state: 'committed' as const, result: parseRefundPayoutResult(recovered.result, key) } : invalid();
    },
    async reserve(actorId: string, draftId: string, revision: string, key: string, current: () => boolean) {
      check(current); if (refundPayoutOwner(key, 'reserve') !== draftId) return invalid();
      const saved = parsePaymentReservation(data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/evidence', { body: { reason: 'Unggah bukti asli pencatatan refund' },
        params: { path: { id: parse(id, draftId) }, header: { ...headers(actorId), 'If-Match': '"' + parse(id, revision) + '"', 'Idempotency-Key': key } }, cache: 'no-store' })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async recoverReservation(actorId: string, key: string) {
      const raw = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), reservation: z.unknown().nullable() }).strict(), data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/evidence/operations/query', {
        body: { purpose: 'operation-recovery', key }, params: { path: { id: refundPayoutOwner(key, 'reserve') }, header: headers(actorId) }, cache: 'no-store' })));
      if (raw.status === 'not_confirmed' && raw.reservation === null) return null;
      return raw.status === 'committed' && raw.reservation !== null ? { state: 'committed' as const, result: parsePaymentReservation(raw.reservation) } : invalid();
    },
    async evidence(actorId: string, draftId: string, beforeId: string | null = null) {
      const raw = parse(z.object({ items: z.array(z.unknown()).max(25) }).strict(), data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/evidence/query', {
        body: { purpose: 'refund-payout-evidence', beforeId }, params: { path: { id: parse(id, draftId) }, header: headers(actorId) }, cache: 'no-store' })));
      const items = raw.items.map(parsePaymentEvidenceStatus); if (new Set(items.map(item => item.reservationId)).size !== items.length || beforeId !== null && items.some(item => BigInt(item.reservationId) >= BigInt(parse(id, beforeId)))) return invalid(); return items;
    },
    async upload(actorId: string, draftId: string, reservationId: string, revision: string, file: File, current: () => boolean) {
      if (file.size <= 0 || file.size > 10 * 1024 * 1024 || !['image/jpeg', 'image/png'].includes(file.type)) throw new RequestNotSentError(400, 'refund_payout_image_required'); check(current);
      const saved = parse(uploadOutcome, data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/evidence/{reservationId}/content', { body: '', bodySerializer: () => file,
        headers: { 'Content-Type': 'application/octet-stream' }, params: { path: { id: parse(id, draftId), reservationId: parse(id, reservationId) },
          header: { ...headers(actorId), 'If-Match': '"' + parse(id, revision) + '"', 'Idempotency-Key': 'upload.' + reservationId } }, cache: 'no-store' })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return saved;
    },
    async recoverUpload(actorId: string, draftId: string, reservationId: string) {
      return parse(uploadOutcome, data(await client.POST('/api/v1/billing/refund-payout-drafts/{id}/evidence/{reservationId}/recover', { body: { purpose: 'upload-recovery', reason: 'Periksa hasil unggah bukti refund asli' },
        params: { path: { id: parse(id, draftId), reservationId: parse(id, reservationId) }, header: { ...headers(actorId), 'Idempotency-Key': 'upload.' + reservationId } }, cache: 'no-store' })));
    },
    async original(actorId: string, draftId: string, revision: string, item: PaymentEvidenceStatus, current: () => boolean) {
      const image = parsePaymentEvidenceStatus(item); if (!image.reference || image.scanStatus !== 'clean' || image.decoderStatus !== 'decoded' || image.safeFailure !== null) throw new RequestNotSentError(409, 'evidence_read_not_eligible'); check(current);
      const response = await client.POST('/api/v1/billing/refund-payout-drafts/{id}/evidence/{reservationId}/original/query', { body: { purpose: 'refund-payout-evidence-view', reference: image.reference },
        params: { path: { id: parse(id, draftId), reservationId: image.reservationId }, header: { ...headers(actorId), 'If-Match': '"' + parse(id, revision) + '"' } }, parseAs: 'arrayBuffer', cache: 'no-store' });
      check(current); const bytes = data(response), mime = response.response.headers.get('content-type');
      if (!(bytes instanceof ArrayBuffer) || bytes.byteLength <= 0 || bytes.byteLength > 10 * 1024 * 1024 || response.response.headers.get('content-length') !== String(bytes.byteLength) || !['image/png', 'image/jpeg'].includes(mime ?? '')) return invalid();
      const digest = await crypto.subtle.digest('SHA-256', bytes); check(current); if (Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') !== image.reference.sha256) return invalid();
      return new Blob([bytes], { type: mime! });
    },
  };
}
