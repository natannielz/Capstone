import type { ReactNode } from "react";

/**
 * The one description list (ui-standard §6.4): two columns from 640 px (one below), 13 px muted term, 15/600 value.
 * Replaces `common.facts`, `.review-panel dl`, request `.meta` and the health facts.
 */
export function Facts({ items, columns = 2 }: { items: Array<{ term: string; value: ReactNode; key?: string }>; columns?: 1 | 2 }) {
  return <dl className="facts-list" data-columns={columns}>
    {items.map(item => <div key={item.key ?? item.term}><dt>{item.term}</dt><dd>{item.value}</dd></div>)}
  </dl>;
}
