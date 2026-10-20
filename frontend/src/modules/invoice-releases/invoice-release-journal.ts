import {z} from 'zod';

type StoragePort = Pick<Storage, 'length' | 'key' | 'getItem' | 'setItem' | 'removeItem'>;
const id = z.string().regex(/^[1-9][0-9]{0,18}$(?![\s\S])/).refine(value => BigInt(value) <= BigInt('9223372036854775807'));
const revision = z.union([z.literal('0'), id]);
const reference = z.object({actorId: id, action: z.enum(['propose', 'approve', 'reject']),
  key: z.string().regex(/^[A-Za-z0-9_.:-]{1,128}$(?![\s\S])/), revision, proposalId: id.nullable(),
  selectionKey: z.string().regex(/^[0-9a-f]{64}$(?![\s\S])/)}).strict()
  .refine(value => value.action === 'propose' ? value.proposalId === null : value.proposalId !== null && value.revision !== '0');
export type InvoiceReleaseReference = z.infer<typeof reference>;
export const invoiceReleaseJournalPrefix = (actorId: string) => 'unit-toko.invoice-release.pending.' + id.parse(actorId) + '.';
export function parseInvoiceReleaseReference(value: unknown, actorId: string): InvoiceReleaseReference {
  const checked = reference.parse(value); if (checked.actorId !== id.parse(actorId)) throw new Error('Original account changed'); return checked;
}
/** Original operation references only. Financial facts and reasons remain on the server. */
export class InvoiceReleaseJournal {
  constructor(private readonly storage: () => StoragePort) {}
  list(actorId: string): InvoiceReleaseReference[] {
    const store = this.storage(), prefix = invoiceReleaseJournalPrefix(actorId);
    return Array.from({length: store.length}, (_, index) => store.key(index)).filter((key): key is string => key !== null && key.startsWith(prefix)).map(key => {
      const value = parseInvoiceReleaseReference(JSON.parse(store.getItem(key) ?? 'null'), actorId);
      if (key !== prefix + value.action + '.' + value.key) throw new Error('Original reference changed'); return value;
    }).sort((left, right) => left.key.localeCompare(right.key));
  }
  remember(value: InvoiceReleaseReference) {
    const checked = parseInvoiceReleaseReference(value, value.actorId), store = this.storage();
    const key = invoiceReleaseJournalPrefix(checked.actorId) + checked.action + '.' + checked.key, text = JSON.stringify(checked), old = store.getItem(key);
    if (old !== null && JSON.stringify(parseInvoiceReleaseReference(JSON.parse(old), checked.actorId)) !== text) throw new Error('Original reference changed');
    if (old === null && this.list(checked.actorId).length >= 32) throw new Error('Resolve original operations first');
    store.setItem(key, text); if (store.getItem(key) !== text) throw new Error('Original reference was not saved');
  }
  confirmed(value: InvoiceReleaseReference) {
    const checked = parseInvoiceReleaseReference(value, value.actorId), store = this.storage();
    const key = invoiceReleaseJournalPrefix(checked.actorId) + checked.action + '.' + checked.key, old = store.getItem(key);
    if (old !== null && JSON.stringify(parseInvoiceReleaseReference(JSON.parse(old), checked.actorId)) !== JSON.stringify(checked)) throw new Error('Original reference changed');
    store.removeItem(key); if (store.getItem(key) !== null) throw new Error('Original reference was not cleared');
  }
}
