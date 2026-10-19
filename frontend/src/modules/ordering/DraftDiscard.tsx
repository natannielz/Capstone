import { ReviewPanel } from '@/components/workspace-kit/review-panel';
import { formatDate } from '@/lib/format';
import { DISCARD_WINDOW_MS, type DiscardedDraft } from './order-draft-client';
import common from '../administration/access.module.css';
import styles from './order-draft.module.css';

const PURGE_NOTE = 'Draf yang dibuang dihapus permanen 30 hari setelah dibuang.';
export const draftLabel = (draft: { id: string; input: { purchaseReference: string | null } }) => draft.input.purchaseReference ?? 'Draf ' + draft.id;
export const discardedNotice = (restorableUntil: string) => `Draf dibuang. Anda dapat memulihkannya sampai ${formatDate(restorableUntil, 'long')}.`;

/** The confirmation is the review: what is discarded, until when it can be restored, and that submitted orders are unaffected. */
export function DiscardReview({ label, now, busy, onConfirm, onCancel }: { label: string; now: number; busy: boolean; onConfirm: () => void; onCancel: () => void }) {
  return <ReviewPanel title="Buang draf ini?" tone="danger" actionLabel="Buang draf" busy={busy} onConfirm={onConfirm} onCancel={onCancel}
    items={[{ label: 'Draf', value: label }, { label: 'Dapat dipulihkan sampai', value: formatDate(new Date(now + DISCARD_WINDOW_MS), 'long') }]}
    note={`${PURGE_NOTE} Pesanan yang sudah diajukan tidak terpengaruh.`} />;
}

export function DiscardedDraftList({ items, locked, onRestore }: { items: DiscardedDraft[]; locked: boolean; onRestore: (draft: DiscardedDraft) => void }) {
  return <section className={common.queue} aria-label="Draf yang dibuang"><div className={common.queueHeader}><h2>Draf yang dibuang</h2></div>
    <p className={common.help}>{PURGE_NOTE}</p>
    {items.length === 0 ? <p className={styles.empty}>Tidak ada draf yang dibuang di cabang ini.</p> :
      <ul className={common.queueList}>{items.map(draft => <li key={draft.id} className={styles.discarded}>
        <div><strong>{draftLabel(draft)}</strong><span>{draft.input.selections.length} jenis kemasan · Dapat dipulihkan sampai {formatDate(draft.restorableUntil, 'long')}</span></div>
        <button type="button" className={common.secondary} disabled={locked} aria-label={'Pulihkan draf ' + draftLabel(draft)} onClick={() => onRestore(draft)}>Pulihkan draf</button>
      </li>)}</ul>}
  </section>;
}
