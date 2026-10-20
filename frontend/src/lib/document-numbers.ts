/**
 * Human-facing numbers for records that have no printed document number of their own.
 * An order reads "PSN-005201": a typed prefix and the record number, so it still matches the
 * "Pesanan 5201" printed on surat jalan issued earlier. Invoices and shipments keep the number on
 * their issued document, shown exactly as printed. Drafts get no number until they are submitted.
 */
const recordId = /^[1-9][0-9]{0,18}$/;

export function orderNumber(orderId: string): string {
  if (!recordId.test(orderId)) throw new RangeError('order id must be a positive decimal string');
  return 'PSN-' + orderId.padStart(6, '0');
}

/** What a person typed into a search box, read as an order, or null when it cannot be one. */
export function matchesOrderNumber(typed: string): string | null {
  const compact = typed.trim().toUpperCase().replace(/[\s-]/g, '');
  const digits = compact.startsWith('PSN') ? compact.slice(3) : compact;
  if (!/^[0-9]{1,19}$/.test(digits)) return null;
  const id = digits.replace(/^0+/, '');
  return recordId.test(id) ? id : null;
}
