import type { ReactNode } from "react";
import { Button } from "./button";
import { Pagination } from "./pagination";
import { StateView, regionState } from "./state-view";
import type { TablePagination } from "./list-table";

/**
 * Queues, orders and requests (ui-standard §6.3): one <ul> of rows divided by 1 px lines inside a Section — never one bordered
 * card per row. Each row: title + badge, a 13 px meta line, optional content, actions right-aligned (stacked on phones).
 * With `onSelect` the title block is one button (the whole row opens the record; no separate "Lihat …" button) and the
 * selected row carries the sidebar's inset marker. Error, first load and empty each replace the list (exactly one state).
 */
export function DataList<Item>({ label, items, getKey, renderTitle, renderMeta, renderBadge, renderContent, renderActions, onSelect, openLabel, selectedKey, disabled = false, loading = false, error, empty, pagination }: {
  label: string; items: readonly Item[]; getKey: (item: Item) => string; renderTitle: (item: Item) => ReactNode;
  renderMeta?: (item: Item) => ReactNode; renderBadge?: (item: Item) => ReactNode; renderContent?: (item: Item) => ReactNode; renderActions?: (item: Item) => ReactNode;
  onSelect?: (item: Item) => void;
  /** Names the whole-row open button when the record already has a known name ("Tinjau pesanan PSN-…"), so rehearsals and screen readers keep it. */
  openLabel?: (item: Item) => string;
  selectedKey?: string | null; disabled?: boolean;
  loading?: boolean; error?: { title?: string; message: ReactNode; detail?: ReactNode; onRetry?: () => void };
  empty?: { title: string; message?: ReactNode; action?: ReactNode }; pagination?: TablePagination;
}) {
  const state = regionState({ loading, error: !!error, empty: items.length === 0 });
  if (state === "error") return <StateView kind="error" title={error!.title ?? label + " belum dapat dimuat"} message={error!.message} detail={error!.detail}
    action={error!.onRetry ? <Button onClick={error!.onRetry}>Coba lagi</Button> : undefined} />;
  if (state === "loading") return <StateView kind="loading" title={"Memuat " + label.toLowerCase()} rows={3} />;
  if (state === "empty") return empty ? <StateView kind="empty" title={empty.title} message={empty.message} action={empty.action} /> : null;
  return <>
    <ul className="data-list" aria-label={label}>
      {items.map(item => {
        const key = getKey(item), meta = renderMeta?.(item), badge = renderBadge?.(item), content = renderContent?.(item), actions = renderActions?.(item);
        // Spans only: the head may sit inside a <button>, which allows phrasing content alone.
        const head = <><span className="data-list-head"><span className="data-list-title">{renderTitle(item)}</span>{badge}</span>{meta && <span className="data-list-meta">{meta}</span>}</>;
        return <li key={key} className="data-list-row" aria-current={selectedKey === key ? "true" : undefined}>
          {onSelect ? <button type="button" className="data-list-open" aria-label={openLabel?.(item)} disabled={disabled} onClick={() => onSelect(item)}>{head}</button> : head}
          {content && <div className="data-list-content">{content}</div>}
          {actions && <div className="data-list-actions ui-actions">{actions}</div>}
        </li>;
      })}
    </ul>
    {pagination && <Pagination label={pagination.label ?? "Halaman " + label.toLowerCase()} {...pagination} />}
  </>;
}
