import type { ReactNode } from "react";

export type ListColumn<Row> = { key: string; header: string; align?: "start" | "end"; render: (row: Row) => ReactNode };

/**
 * Operational list: sticky header, right-aligned tabular numbers, one open action per row (keyboard reachable).
 * Below 640px each row becomes a labelled card (CSS uses data-label), so there is a single accessible DOM.
 */
export function ListTable<Row>({ columns, rows, getKey, onOpen, openLabel, caption, density = "comfortable", selectedKey, disabled = false }: {
  columns: ListColumn<Row>[]; rows: Row[]; getKey: (row: Row) => string; caption: string;
  onOpen?: (row: Row) => void; openLabel?: (row: Row) => string; density?: "compact" | "comfortable"; selectedKey?: string | null;
  /** Locks every row action (e.g. while a command is in flight or the session is being renewed). */
  disabled?: boolean;
}) {
  return <div className="list-table-wrap">
    <table className="list-table" data-density={density}>
      <caption className="list-table-caption">{caption}</caption>
      <thead><tr>{columns.map(column => <th key={column.key} scope="col" data-align={column.align ?? "start"}>{column.header}</th>)}{onOpen && <th scope="col"><span className="list-table-sr">Tindakan</span></th>}</tr></thead>
      <tbody>{rows.map(row => { const key = getKey(row); return <tr key={key} aria-current={selectedKey === key ? "true" : undefined}>
        {columns.map(column => <td key={column.key} data-label={column.header} data-align={column.align ?? "start"} className={column.align === "end" ? "numeric" : undefined}>{column.render(row)}</td>)}
        {onOpen && <td data-label=""><button type="button" className="list-table-open" disabled={disabled} onClick={() => onOpen(row)}>{openLabel ? openLabel(row) : "Buka"}</button></td>}
      </tr>; })}</tbody>
    </table>
  </div>;
}
