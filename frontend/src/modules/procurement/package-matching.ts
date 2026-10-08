import { z } from 'zod';
import { paymentAllocationSchemas } from '../billing/payment-allocation-client';

const match = z.object({ skuId: paymentAllocationSchemas.id, kind: z.enum(['browse', 'exact_code', 'exact_name', 'contains', 'token_prefix', 'similar_name']),
  field: z.enum(['sku', 'name', 'package']), editDistance: z.number().int().min(0).max(8).nullable() }).strict().refine(value => {
  if (value.kind === 'exact_code') return value.field === 'sku' && value.editDistance === 0;
  if (value.kind === 'exact_name') return value.field === 'name' && value.editDistance === 0;
  if (value.kind === 'similar_name') return value.field === 'name' && value.editDistance !== null;
  return value.editDistance === null && (value.kind !== 'browse' || value.field === 'name');
});
export const packageMatchingSchema = z.object({ ruleVersion: z.literal('catalog-code-name-v1'), limited: z.boolean(), items: z.array(match).max(25) }).strict();
export type PackageMatching = z.infer<typeof packageMatchingSchema>;
export function packageMatchesFit(items: readonly { skuId: string }[], matching: PackageMatching) {
  return items.length === matching.items.length && items.every((item, index) => item.skuId === matching.items[index].skuId);
}
export function packageMatchExplanation(value: PackageMatching['items'][number] | undefined) {
  if (!value || value.kind === 'browse') return '';
  const field = { sku: 'kode SKU', name: 'nama barang', package: 'label kemasan' }[value.field];
  if (value.kind === 'exact_code') return 'Kode SKU sama persis. Periksa isi kemasan sebelum memilih.';
  if (value.kind === 'exact_name') return 'Nama barang sama persis. Periksa varian dan isi kemasan.';
  if (value.kind === 'similar_name') return `Saran nama mirip (${value.editDistance} perubahan huruf). Belum menjadi pilihan Anda.`;
  return `${value.kind === 'contains' ? 'Teks ditemukan pada' : 'Awalan kata cocok dengan'} ${field}. Periksa barang dan kemasannya.`;
}
