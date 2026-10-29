import Link from 'next/link';
import { workAgeText, workCountText, type WorkSummaryItem } from './work-summary-client';
import styles from './workspace-home.module.css';

/** Waiting queues, oldest first. Each is a single link whose name reads "7 Permintaan cabang menunggu peninjauan …". */
export function WorkQueue({ items, observedAt }: { items: readonly WorkSummaryItem[]; observedAt: string }) {
  const now = Date.parse(observedAt);
  return <ol className={styles.queue} aria-label="Pekerjaan yang menunggu">
    {items.map(item => {
      const age = item.unavailable ? '' : workAgeText(item.oldestAt, now, item.hasMore);
      return <li key={item.key}>
        <Link href={item.href} className={styles.tile} data-unavailable={item.unavailable || undefined}>
          <span className={styles.count}>{item.unavailable ? '—' : workCountText(item.count, item.hasMore)}</span>
          <span className={styles.text}>
            <span className={styles.area}>{item.area}</span>
            <span className={styles.label}>{item.label}</span>
            <span className={styles.age}>{item.unavailable ? 'Belum dapat dimuat. Buka ruang kerjanya untuk memeriksa.' : age}</span>
          </span>
        </Link>
      </li>;
    })}
  </ol>;
}
