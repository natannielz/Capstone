/**
 * DataTable is the existing ListTable (extended with loading / error / empty states, column widths and pagination),
 * exported under the standard's name so pages use one table. See README.md.
 */
export { ListTable as DataTable, type ListColumn as DataColumn, type TablePagination } from "./list-table";
export { Pagination } from "./pagination";
