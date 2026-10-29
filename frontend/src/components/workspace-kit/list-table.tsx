import type { ReactNode } from "react";
import { Button } from "./button";
import { Pagination } from "./pagination";
import { StateView, regionState } from "./state-view";

export type ListColumn<Row> = { key: string; header: string; align?: "start" | "end"; width?: string; rowHeader?: boolean; render: (row: Row) => ReactNode };
export type TablePagination = Omit<Parameters<typeof Pagination>[0], "label" | "compact"> & { label?: string };

/**
 * Operational list (DataTable, ui-standard §6.2): sticky muted header, 48 px rows (36 compact), right-aligned tabular numbers,
 * one open action per row (keyboard reachable). Below 640px each row becomes a labelled card (CSS uses data-label), so there is a
 * single accessible DOM. With `empty`, `error` or `loading` the region shows exactly one state: an error replaces the rows and
 * the empty message, first-load skeletons replace an empty table, and the pager hides when everything fits.
 */
export function ListTable<Row>({ columns, rows, getKey, onOpen, openLabel, caption, density = "comfortable", selectedKey, disabled = false, loading = false, error, empty, pagination }: {
  columns: ListColumn<Row>[]; rows: Row[]; getKey: (row: Row) => string; caption: string;
  onOpen?: (row: Row) => void; openLabel?: (row: Row) => string; density?: "compact" | "comfortable"; selectedKey?: string | null;
  /** Locks every row action (e.g. while a command is in flight or the session is being renewed). */
  disabled?: boolean;
  loading?: boolean; error?: { title?: string; message: ReactNode; detail?: ReactNode; onRetry?: () => void };
  empty?: { title: string; message?: ReactNode; action?: ReactNode }; pagination?: TablePagination;
}) {
  const state = regionState({ loading, error: !!error, empty: rows.length === 0 && (!!empty || loading) });
  if (state === "error") return <StateView kind="error" title={error!.title ?? caption + " belum dapat dimuat"} message={error!.message} detail={error!.detail}
    action={error!.onRetry ? <Button onClick={error!.onRetry}>Coba lagi</Button> : undefined} />;
  if (state === "loading") return <StateView kind="loading" title={"Memuat " + caption.toLowerCase()} rows={3} />;
  if (state === "empty" && empty) return <StateView kind="empty" title={empty.title} message={empty.message} action={empty.action} />;
  return <div className="list-table-wrap" data-kit="">
    <table className="list-table" data-density={density}>
      <caption className="list-table-caption">{caption}</caption>
      <thead><tr>{columns.map(column => <th key={column.key} scope="col" data-align={column.align ?? "start"} style={column.width ? { width: column.width } : undefined}>{column.header}</th>)}{onOpen && <th scope="col"><span className="list-table-sr">Tindakan</span></th>}</tr></thead>
      <tbody>{rows.map(row => { const key = getKey(row); return <tr key={key} aria-current={selectedKey === key ? "true" : undefined}>
        {columns.map(column => column.rowHeader ? <th key={column.key} scope="row" data-label={column.header} data-align={column.align ?? "start"}>{column.render(row)}</th>
          : <td key={column.key} data-label={column.header} data-align={column.align ?? "start"} className={column.align === "end" ? "numeric" : undefined}>{column.render(row)}</td>)}
        {onOpen && <td data-label=""><button type="button" className="list-table-open" disabled={disabled} onClick={() => onOpen(row)}>{openLabel ? openLabel(row) : "Buka"}</button></td>}
      </tr>; })}</tbody>
    </table>
    {pagination && rows.length > 0 && <Pagination label={pagination.label ?? "Halaman " + caption.toLowerCase()} compact={density === "compact"} {...pagination} />}
  </div>;
}
