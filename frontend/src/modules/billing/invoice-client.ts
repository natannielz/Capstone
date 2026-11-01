import { z } from 'zod';
import { createUnitTokoClient, RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { priceId } from '../catalog/catalog-price-workflow';
import { orderAmount } from '../ordering/order-quote-client';

const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const money = z.string().regex(/^(?:0|[1-9][0-9]{0,14})(?:\.[0-9]{1,2})?$(?![\s\S])/);
const hash = z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/), text = z.string().min(1).max(1000);
const day = z.string().regex(/^\d{4}-\d{2}-\d{2}$(?![\s\S])/).refine(value => Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value);
const timestamp = z.string().datetime({ offset: true });
const selection = z.object({ shipmentId: id, receiptId: id }).strict();
const selected = z.array(selection).min(1).max(20).refine(values => new Set(values.map(value => value.shipmentId)).size === values.length && new Set(values.map(value => value.receiptId)).size === values.length);
const line = z.object({ orderId: id, shipmentId: id, receiptId: id, skuId: id, skuCode: text, productName: text, packageLabel: text,
  baseUnitsPerPackage: z.number().int().min(1).max(1000000), baseUnitCode: text, acceptedBaseUnits: id, net: money, tax: money, charges: money }).strict();
const cents = (value: string) => { const [whole, fraction = ''] = value.split('.'); return BigInt(whole) * BigInt(100) + BigInt(fraction.padEnd(2, '0')); };
const preview = z.object({ review: hash, buyerName: text, sellerName: text, issueDate: day, dueDate: day,
  grouping: z.enum(['buying_org_period_channel', 'buying_branch_period_channel', 'receipt']), periodFrom: day, periodTo: day,
  termOrigin: z.enum(['issue_date', 'earliest_receipt', 'latest_receipt']), termDays: z.number().int().min(0).max(365), dayRule: z.enum(['calendar_days', 'working_days']),
  taxDocumentProcess: text, net: money, tax: money, charges: money, total: money, lines: z.array(line).min(1).max(100),
}).strict().refine(value => {
  if (value.periodTo < value.periodFrom || new Set(value.lines.map(item => item.receiptId + ':' + item.skuId)).size !== value.lines.length) return false;
  for (const field of ['net', 'tax', 'charges'] as const) if (value.lines.reduce((total, item) => total + cents(item[field]), BigInt(0)) !== cents(value[field])) return false;
  return cents(value.total) > BigInt(0) && cents(value.net) + cents(value.tax) + cents(value.charges) === cents(value.total);
});
const invoice = z.object({ id, numberEntity: id, numberChannel: id, numberYear: z.number().int().min(1).max(9999), number: id, issuedAt: timestamp,
  detail: preview, documentHash: hash, documentLength: z.number().int().min(1).max(2097152),
  paid: money, credited: money, outstanding: money, paymentState: z.enum(['unpaid', 'partial', 'paid', 'credited']), aging: z.enum(['settled', 'overdue', 'due', 'not_due']) }).strict().refine(value =>
  cents(value.paid) + cents(value.credited) + cents(value.outstanding) === cents(value.detail.total) && (value.aging === 'settled') === (cents(value.outstanding) === BigInt(0)) &&
  value.paymentState === (cents(value.outstanding) === BigInt(0) ? cents(value.credited) === cents(value.detail.total) ? 'credited' : 'paid' : cents(value.paid) + cents(value.credited) > BigInt(0) ? 'partial' : 'unpaid'));
const paymentStates = { unpaid: 'Belum dibayar', partial: 'Dibayar sebagian', paid: 'Lunas', credited: 'Dikreditkan' } as const;
/** The buyer-visible payment line of an issued invoice, in the allocation desk's words. */
export function invoicePayment(item: Invoice) {
  return ['Status pembayaran: ' + paymentStates[item.paymentState], 'Dibayar ' + orderAmount('IDR', item.paid),
    ...(cents(item.credited) > BigInt(0) ? ['Kredit ' + orderAmount('IDR', item.credited)] : []), 'Sisa tagihan ' + orderAmount('IDR', item.outstanding)].join(' · ');
}
const candidate = z.object({ shipmentId: id, receiptId: id, receivedAt: timestamp, buyerName: text, acceptedLines: z.number().int().min(1).max(100), invoiced: z.boolean() }).strict();
export type Invoice = z.infer<typeof invoice>;
export type InvoicePreview = z.infer<typeof preview>;
export type InvoiceCandidate = z.infer<typeof candidate>;
function invalid(): never { throw new AccessFailure(502, 'invoice_response_invalid'); }
function parse<T>(schema: z.ZodType<T>, value: unknown): T { const result = schema.safeParse(value); return result.success ? result.data : invalid(); }
export const parseInvoice = (value: unknown) => parse(invoice, value);
function data<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) { const error = result.error && typeof result.error === 'object' ? result.error as Record<string, unknown> : {}; throw new AccessFailure(result.response.status, typeof error.code === 'string' ? error.code : 'request_failed'); } return result.data;
}
const headers = (actorId: string) => ({ 'X-Expected-Actor-Id': priceId(actorId) });
const key = (value: string) => /^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/.test(value) ? value : invalid();
function selections(values: readonly { shipmentId: string; receiptId: string }[]) { return parse(selected, values.map(value => ({ shipmentId: value.shipmentId, receiptId: value.receiptId }))); }
function matches(source: InvoicePreview, receipts: readonly { shipmentId: string; receiptId: string }[]) {
  return receipts.every(receipt => source.lines.some(line => line.shipmentId === receipt.shipmentId && line.receiptId === receipt.receiptId)) &&
    source.lines.every(line => receipts.some(receipt => line.shipmentId === receipt.shipmentId && line.receiptId === receipt.receiptId));
}
export function prepareInvoice(actorId: string, values: readonly { shipmentId: string; receiptId: string }[], value: InvoicePreview, reason: string) {
  const detail = parse(preview, value), receipts = selections(values);
  if (!matches(detail, receipts) || reason !== reason.trim() || !reason || reason.length > 1000 || /[\u0000-\u001f\u007f]/.test(reason)) throw new AccessFailure(400, 'invalid_invoice');
  return Object.freeze({ actorId: priceId(actorId), body: Object.freeze({ receipts: Object.freeze(receipts.map(receipt => Object.freeze(receipt))), review: detail.review, reason }),
    detail: Object.freeze({ ...detail, lines: Object.freeze(detail.lines.map(line => Object.freeze(line))) }) });
}
export function createInvoiceGateway(client: ReturnType<typeof createUnitTokoClient> = createUnitTokoClient()) {
  return {
    async candidates(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const result = parse(z.object({ items: z.array(candidate).max(25), nextShipmentId: id.nullable() }).strict(), data(await client.POST('/api/v1/billing/invoices/candidates/query', { body: { purpose: 'invoices', beforeId }, params: { header: headers(actorId) }, signal })));
      if (beforeId !== null && (result.items.some(item => BigInt(item.shipmentId) >= BigInt(priceId(beforeId))) || result.nextShipmentId !== null && BigInt(result.nextShipmentId) >= BigInt(beforeId)) || new Set(result.items.map(item => item.shipmentId)).size !== result.items.length) return invalid(); return result;
    },
    async preview(actorId: string, values: readonly { shipmentId: string; receiptId: string }[]) {
      const receipts = selections(values), result = parse(preview, data(await client.POST('/api/v1/billing/invoices/preview/query', { body: { purpose: 'invoices', receipts }, params: { header: headers(actorId) } })));
      if (!matches(result, receipts)) return invalid(); return result;
    },
    async issue(command: ReturnType<typeof prepareInvoice>, requestKey: string, current: () => boolean) {
      if (!current()) throw new RequestNotSentError(409, 'invoice_view_stale');
      const result = parseInvoice(data(await client.POST('/api/v1/billing/invoices', { body: { ...command.body, receipts: [...command.body.receipts] }, params: { header: { ...headers(command.actorId), 'Idempotency-Key': key(requestKey), 'If-Match': '"0"' } } })));
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown');
      if (result.detail.review !== command.body.review || !matches(result.detail, command.body.receipts)) return invalid(); return result;
    },
    async list(actorId: string, beforeId: string | null = null, signal?: AbortSignal) {
      const result = parse(z.object({ items: z.array(invoice).max(25), hasMore: z.boolean() }).strict(), data(await client.POST('/api/v1/billing/invoices/query', { body: { purpose: 'invoices', beforeId }, params: { header: headers(actorId) }, signal })));
      if (new Set(result.items.map(item => item.id)).size !== result.items.length || beforeId !== null && result.items.some(item => BigInt(item.id) >= BigInt(priceId(beforeId))) || result.hasMore && result.items.length !== 25) return invalid(); return result;
    },
    async read(actorId: string, invoiceId: string) {
      const result = parseInvoice(data(await client.POST('/api/v1/billing/invoices/{id}/query', { body: { purpose: 'invoices' }, params: { path: { id: priceId(invoiceId) }, header: headers(actorId) } })));
      if (result.id !== invoiceId) return invalid(); return result;
    },
    async recover(actorId: string, requestKey: string) {
      const result = parse(z.object({ status: z.enum(['not_confirmed', 'committed']), invoice: invoice.nullable() }).strict(), data(await client.POST('/api/v1/billing/invoices/operations/query', { body: { purpose: 'operation-recovery', key: key(requestKey) }, params: { header: headers(actorId) } })));
      if (result.status === 'not_confirmed' && result.invoice === null) return null;
      if (result.status !== 'committed' || result.invoice === null) return invalid(); return { state: 'committed' as const, result: result.invoice };
    },
    async download(actorId: string, value: Invoice) {
      const expected = parseInvoice(value), response = await client.POST('/api/v1/billing/invoices/{id}/download', { body: { purpose: 'invoices' }, params: { path: { id: expected.id }, header: headers(actorId) }, parseAs: 'arrayBuffer' });
      const bytes = data(response);
      if (!(bytes instanceof ArrayBuffer) || bytes.byteLength !== expected.documentLength || response.response.headers.get('content-type') !== 'text/html; charset=utf-8') return invalid();
      const digest = await crypto.subtle.digest('SHA-256', bytes);
      if (Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') !== expected.documentHash) return invalid(); return new Blob([bytes], { type: 'text/html;charset=utf-8' });
    },
  };
}
