import { parseBuyerCodeMatch, type BuyerCodeMatch } from '../catalog/catalog-buyer-client';
import type { DraftInput } from './order-draft-client';
import { orderQuantity } from './order-quote-client';

export class BulkInputError extends Error {
  constructor(message: string, readonly line: number | null = null) {
    super(line === null ? message : 'Baris ' + line + ': ' + message);
    this.name = 'BulkInputError';
  }
}
export type BulkSkuRow = Readonly<{ line: number; code: string; packages: number }>;

export function parseBulkSkuInput(text: string): readonly BulkSkuRow[] {
  if (typeof text !== 'string' || text.length > 16000) throw new BulkInputError('Daftar terlalu panjang. Masukkan paling banyak 100 SKU.');
  const rows: BulkSkuRow[] = [], seen = new Set<string>();
  for (const [index, raw] of text.split(/\r?\n/).entries()) {
    if (!raw.trim()) continue;
    const line = index + 1, cells = raw.split(/[\t;]/);
    if (cells.length !== 2) throw new BulkInputError('Pisahkan SKU dan jumlah kemasan dengan tab atau titik koma.', line);
    const code = cells[0].trim(), quantity = cells[1].trim();
    // Validate ASCII before case conversion, just like CatalogPolicy.NormalizeSkuCode.
    if (/[\u0000-\u001f\u007f]/.test(cells[0]) || !/^[A-Za-z0-9._-]{1,64}$(?![\s\S])/.test(code)) throw new BulkInputError('Kode SKU internal tidak valid.', line);
    const normalized = code.toUpperCase();
    if (seen.has(normalized)) throw new BulkInputError('SKU yang sama hanya boleh dicantumkan sekali.', line);
    if (!/^[1-9][0-9]{0,6}$(?![\s\S])/.test(quantity) || Number(quantity) > 1000000)
      throw new BulkInputError('Jumlah kemasan harus bilangan bulat dari 1 sampai 1.000.000.', line);
    rows.push(Object.freeze({ line, code: normalized, packages: Number(quantity) })); seen.add(normalized);
    if (rows.length > 100) throw new BulkInputError('Masukkan paling banyak 100 SKU.', line);
  }
  if (rows.length === 0) throw new BulkInputError('Masukkan setidaknya satu SKU dan jumlah kemasan.');
  return Object.freeze(rows);
}

export function prepareBulkSkuSelection(rows: readonly BulkSkuRow[], matches: readonly BuyerCodeMatch[],
  current: DraftInput['selections']) {
  if (rows.length === 0 || rows.length > 100 || matches.length !== rows.length ||
      new Set(rows.map(row => row.code)).size !== rows.length || new Set(matches.map(item => item.variant.skuId)).size !== matches.length ||
      new Set(matches.map(item => item.matchedIdentifier.identifierId)).size !== matches.length)
    throw new BulkInputError('Hasil pemeriksaan tidak sesuai daftar SKU. Periksa kembali.');
  const choices = rows.map((row, index) => {
    let variant: BuyerCodeMatch['variant'];
    try { variant = parseBuyerCodeMatch(matches[index], row.code).variant; }
    catch { throw new BulkInputError('Kode dan kemasan tidak cocok. Periksa daftar kembali.', row.line); }
    if (variant.currentPrice === null) throw new BulkInputError('Kemasan atau harga SKU belum tersedia.', row.line);
    let packages: number;
    try { packages = orderQuantity(String(row.packages), variant.definition.minimumOrderPackages, variant.definition.orderIncrementPackages); }
    catch { throw new BulkInputError('Jumlah tidak memenuhi minimum atau kelipatan kemasan ini.', row.line); }
    return { skuId: variant.skuId, definitionId: variant.definition.id, definitionVersion: variant.definition.version,
      priceId: variant.currentPrice.id, priceVersion: variant.currentPrice.version, packages };
  });
  const replacements = new Map(choices.map(item => [item.skuId, item]));
  const merged = current.map(item => replacements.get(item.skuId) ?? { ...item });
  const existing = new Set(current.map(item => item.skuId));
  merged.push(...choices.filter(item => !existing.has(item.skuId)));
  if (merged.length > 100) throw new BulkInputError('Gabungan draf melebihi 100 kemasan berbeda.');
  return { selections: merged, replacements: choices.filter(item => existing.has(item.skuId)).map(item => item.skuId) };
}
