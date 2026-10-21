import { z } from 'zod';
import { AccessFailure } from '../administration/workflow';
import { RequestNotSentError } from '../../api/client';
import { IdentityFailure } from '../identity/identity-workflow';

export const requestIdSchema = z.string().refine(value => /^[1-9][0-9]{0,18}$(?![\s\S])/u.test(value) && BigInt(value) <= BigInt('9223372036854775807'));
export const requestText = (maximum: number) => z.string().min(1).max(maximum).refine(value => value.trim() === value && !/[\u0000-\u001f\u007f]/u.test(value));
export const requestTimeSchema = z.string().datetime({ offset: true });
export const requestContextSchema = z.object({ organizationId: requestIdSchema, branchId: requestIdSchema, divisionId: requestIdSchema.nullable() }).strict();
export const requestSelectionSchema = z.object({ skuId: requestIdSchema, definitionId: requestIdSchema, definitionVersion: requestIdSchema,
  packages: z.number().int().min(1).max(1_000_000) }).strict();
export const requestInputSchema = z.object({ context: requestContextSchema, lines: z.array(requestSelectionSchema).min(1).max(100), note: requestText(1000).nullable() }).strict()
  .refine(input => new Set(input.lines.map(line => line.skuId)).size === input.lines.length);
export type BranchRequestInput = z.infer<typeof requestInputSchema>;
export type BranchRequestContext = z.infer<typeof requestContextSchema>;
export type BranchRequestSelection = z.infer<typeof requestSelectionSchema>;
export type BranchRequestOperation = 'ordering.branchrequest.submit' | 'ordering.branchrequest.decide';
export type BranchRequestAction = 'accept' | 'reject';
export const requestContextKey = (context: BranchRequestContext) => [context.organizationId, context.branchId, context.divisionId ?? ''].join(':');
export function requestPackages(value: string, minimum = 1, increment = 1) {
  if (!/^[1-9][0-9]{0,6}$(?![\s\S])/u.test(value)) throw new AccessFailure(400, 'branch_request_quantity_invalid');
  const quantity = Number(value);
  if (quantity > 1_000_000 || quantity < minimum || quantity % increment !== 0) throw new AccessFailure(400, 'branch_request_quantity_invalid');
  return quantity;
}
export function branchRequestInput(value: unknown): BranchRequestInput {
  const checked = requestInputSchema.safeParse(value);
  if (!checked.success) throw new AccessFailure(400, 'invalid_branch_order_request');
  return checked.data;
}
export function requestResult(value: unknown) {
  const base = z.object({ requestId: requestIdSchema, number: requestText(40), version: z.enum(['1', '2']), state: z.enum(['pending', 'accepted', 'rejected']), recordedAt: requestTimeSchema,
    decisionId: requestIdSchema.nullable().optional() }).strict();
  const checked = base.safeParse(value);
  if (!checked.success || checked.data.number !== 'PT-' + checked.data.requestId.padStart(8, '0') ||
    (checked.data.state === 'pending' ? checked.data.version !== '1' || checked.data.decisionId != null : checked.data.version !== '2' || checked.data.decisionId == null)) throw new AccessFailure(502, 'branch_request_response_invalid');
  return checked.data;
}
export type BranchRequestResult = ReturnType<typeof requestResult>;
export function requestStateLabel(value: BranchRequestResult['state']) {
  return { pending: 'Menunggu peninjauan', accepted: 'Diterima untuk diproses', rejected: 'Ditolak toko' }[value];
}
export function requestMessage(error: unknown, writing = false) {
  if (error instanceof RequestNotSentError) return 'Permintaan belum dikirim. Periksa sesi dan koneksi sebelum melanjutkan.';
  if (error instanceof IdentityFailure && error.status === 401) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
  if (error instanceof AccessFailure) {
    if (error.code === 'actor_changed') return 'Akun di peramban berubah. Periksa sesi sebelum melanjutkan.';
    if (error.code === 'branch_request_quantity_invalid') return 'Isi jumlah sesuai minimum dan kelipatan kemasan.';
    if (error.code === 'branch_request_multiple_fulfillment_owners') return 'Pilih kemasan dari satu toko tujuan untuk setiap permintaan.';
    if (error.code === 'branch_request_package_unavailable') return 'Salah satu kemasan tidak lagi tersedia untuk cabang ini. Periksa kembali pilihan Anda.';
    if (error.code === 'invalid_branch_order_request') return 'Periksa cabang, pilihan kemasan, jumlah, dan catatan Anda.';
    if (error.status === 401) return 'Masuk dengan akun organisasi Anda untuk melanjutkan.';
    if (error.status === 403) return 'Permintaan atau tindakan ini tidak tersedia dalam penugasan akun Anda.';
    if (error.status === 409) return 'Data berubah. Muat kembali dan tinjau informasi terbaru.';
  }
  return writing ? 'Hasil belum dapat dipastikan. Gunakan Periksa hasil untuk permintaan ini sebelum mengirim tindakan lain.' : 'Data belum dapat dimuat. Periksa koneksi dan coba kembali.';
}
