/**
 * Human-facing numbers for records that have no printed document number of their own.
 * An order reads "PSN-005201": a typed prefix and the record number, so it still matches the
 * "Pesanan 5201" printed on surat jalan issued earlier. Invoices and shipments keep the number on
 * their issued document, shown exactly as printed. Drafts get no number until they are submitted.
 *
 * Each record type has its own prefix so a return never reads like an order. A supplier order is
 * "SP" (surat pesanan), because buyers already write their own "PO" in the purchase reference.
 * A branch request is "PB" (permintaan barang). The server keeps its own "PT-00000001" reference as an
 * internal key; people see PB-, and a search still accepts the PT- form.
 */
const recordId = /^[1-9][0-9]{0,18}$/;

const prefixes = { order: 'PSN', case: 'KLH', return: 'RTR', purchaseRequest: 'PP', purchaseOrder: 'SP', branchRequest: 'PB' } as const;
export type RecordKind = keyof typeof prefixes;
/** Other prefixes a record of this kind may be typed with (the server's internal reference). */
const searchAliases: Partial<Record<RecordKind, readonly string[]>> = { branchRequest: ['PT'] };

export function recordNumber(kind: RecordKind, id: string): string {
  if (!recordId.test(id)) throw new RangeError('record id must be a positive decimal string');
  return prefixes[kind] + '-' + id.padStart(6, '0');
}

/** What a person typed into a search box, read as this kind of record, or null when it cannot be one. A bare
 *  number matches; a number typed with another record's prefix does not. */
export function matchesRecordNumber(kind: RecordKind, typed: string): string | null {
  const compact = typed.trim().toUpperCase().replace(/[\s-]/g, '');
  const letters = /^[A-Z]*/.exec(compact)![0];
  if (letters !== '' && letters !== prefixes[kind] && !searchAliases[kind]?.includes(letters)) return null;
  const digits = compact.slice(letters.length);
  if (!/^[0-9]{1,19}$/.test(digits)) return null;
  const id = digits.replace(/^0+/, '');
  return recordId.test(id) ? id : null;
}

export function orderNumber(orderId: string): string {
  return recordNumber('order', orderId);
}

/** What a person typed into a search box, read as an order, or null when it cannot be one. */
export function matchesOrderNumber(typed: string): string | null {
  return matchesRecordNumber('order', typed);
}
