import type { Batch, Product, State } from "./model";
import { today } from "./selectors";

export type InventoryTab = "products" | "batches" | "returns" | "stocktakes";
export type InventoryCondition = "all" | "low" | "expired" | "expiring" | "held";
export type InventoryQuery = { tab: InventoryTab; search: string; source: string; condition: InventoryCondition; page: number; productId: string };
export type InventoryProduct = { product: Product; physical: number; held: number; reserved: number; available: number; expired: number; expiring: number; batchCount: number };
export type InventoryBatch = { batch: Batch; product: Product; reserved: number; available: number; expired: boolean; expiring: boolean };

export function readInventoryQuery(query: URLSearchParams): InventoryQuery {
  const tab = query.get("stockTab") || "products", condition = query.get("stockCondition") || "all";
  const source = query.get("stockSource") || "all";
  return {
    tab: (["products", "batches", "returns", "stocktakes"].includes(tab) ? tab : "products") as InventoryTab,
    search: query.get("stockSearch") || "", source: ["OMI", "Smart"].includes(source) ? source : "all",
    condition: (["all", "low", "expired", "expiring", "held"].includes(condition) ? condition : "all") as InventoryCondition,
    page: Math.max(1, Math.floor(Number(query.get("stockPage")) || 1)), productId: query.get("stockProduct") || "",
  };
}

/** Clamp month shifts to month-end so a January 31 reminder does not skip February. */
function reminderDate(date: string, months: number) {
  const [year, month, day] = date.split("-").map(Number);
  const target = new Date(Date.UTC(year, month - 1 + months, 1));
  const lastDay = new Date(Date.UTC(target.getUTCFullYear(), target.getUTCMonth() + 1, 0)).getUTCDate();
  target.setUTCDate(Math.min(day, lastDay));
  return target.toISOString().slice(0, 10);
}

export function inventoryView(s: State, date = today()) {
  const products = new Map(s.products.map(product => [product.id, { product, physical: 0, held: 0, reserved: 0, available: 0, expired: 0, expiring: 0, batchCount: 0 } satisfies InventoryProduct]));
  const reserved = new Map<string, number>();
  for (const r of s.reservations) reserved.set(r.batchId, (reserved.get(r.batchId) || 0) + r.qty);
  const batches: InventoryBatch[] = [];
  for (const batch of s.batches) {
    const row = products.get(batch.productId);
    if (!row) continue;
    const expired = !!batch.expiry && batch.expiry <= date;
    const expiring = !!batch.expiry && !expired && batch.expiry <= reminderDate(date, row.product.returnMonths);
    const quantityReserved = reserved.get(batch.id) || 0;
    const available = expired ? 0 : batch.qty - batch.held - quantityReserved;
    batches.push({ batch, product: row.product, reserved: quantityReserved, available, expired, expiring });
    row.physical += batch.qty; row.held += batch.held; row.reserved += quantityReserved; row.available += available; row.batchCount++;
    if (expired) row.expired += batch.qty;
    if (expiring) row.expiring += batch.qty;
  }
  return { products: [...products.values()], batches };
}

function matchesProduct(product: Product, query: InventoryQuery, extra = "") {
  const needle = query.search.trim().toLocaleLowerCase("id-ID");
  return (!query.productId || product.id === query.productId) && (query.source === "all" || product.category === query.source)
    && (!needle || `${product.name} ${product.sku} ${extra}`.toLocaleLowerCase("id-ID").includes(needle));
}
export function matchesInventoryProduct(row: InventoryProduct, query: InventoryQuery) {
  if (!matchesProduct(row.product, query)) return false;
  return query.condition === "all" || (query.condition === "low" && row.available <= row.product.minimum)
    || (query.condition === "expired" && row.expired > 0) || (query.condition === "expiring" && row.expiring > 0) || (query.condition === "held" && row.held > 0);
}
export function matchesInventoryBatch(row: InventoryBatch, query: InventoryQuery, products: InventoryProduct[]) {
  if (!matchesProduct(row.product, query, `${row.batch.code} ${row.batch.location}`)) return false;
  return query.condition === "all" || (query.condition === "low" && products.some(p => p.product.id === row.product.id && p.available <= p.product.minimum))
    || (query.condition === "expired" && row.expired && row.batch.qty > 0) || (query.condition === "expiring" && row.expiring && row.batch.qty > 0) || (query.condition === "held" && row.batch.held > 0);
}

export function validReportDate(value: string | null | undefined, fallback: string) {
  return value && /^\d{4}-\d{2}-\d{2}$/.test(value) && Number.isFinite(Date.parse(value)) && new Date(value).toISOString().slice(0, 10) === value ? value : fallback;
}

export type SalesFilter = { start: string; end: string; category: string; division: string };
export function salesRows(s: State, filter: SalesFilter) {
  const shipments = new Map(s.shipments.map(x => [x.id, x])), orders = new Map(s.orders.map(x => [x.id, x]));
  const products = new Map(s.products.map(x => [x.id, x])), orderLines = new Map(s.orderLines.map(x => [x.id, x]));
  return s.shipmentLines.filter(line => line.finalized && line.accepted > 0).flatMap(line => {
    const shipment = shipments.get(line.shipmentId), order = shipment && orders.get(shipment.orderId), product = products.get(orderLines.get(line.orderLineId)?.productId || "");
    if (!shipment || !order || !product) return [];
    const date = line.finalizedDate || shipment.receivedDate || shipment.date;
    if (date < filter.start || date > filter.end || (filter.category !== "all" && product.category !== filter.category) || (filter.division !== "all" && order.divisionId !== filter.division)) return [];
    return [{ line, shipment, order, product, date }];
  }).sort((a, b) => b.date.localeCompare(a.date) || a.shipment.number.localeCompare(b.shipment.number));
}

/** Spreadsheet-safe CSV; export shares the exact filtered rows shown by the register. */
export function salesCSV(s: State, filter: SalesFilter) {
  const rows = salesRows(s, filter), divisions = new Map(s.divisions.map(d => [d.id, d.name]));
  const cells: (string | number)[][] = [
    ["Penjualan final Unit Toko — data simulasi"], ["Tanggal finalisasi", filter.start, filter.end],
    ["Sumber", filter.category === "all" ? "OMI & Smart" : filter.category], ["Divisi", filter.division === "all" ? "Semua divisi" : divisions.get(filter.division) || "Tidak ditemukan"],
    ["Nilai sebelum nota kredit; tidak termasuk pajak invoice"],
    ["Tanggal finalisasi", "Surat Jalan", "Divisi", "SKU", "Barang", "Sumber", "Jumlah final", "Satuan", "Nilai sebelum nota kredit"],
    ...rows.map(({ line, shipment, order, product, date }) => [date, shipment.number, divisions.get(order.divisionId) || "", product.sku, product.name, product.category, line.accepted, product.unit, line.accepted * line.price]),
    ["Total", "", "", "", "", "", "", "", rows.reduce((total, { line }) => total + line.accepted * line.price, 0)],
  ];
  return "\uFEFF" + cells.map(row => row.map(value => {
    const text = typeof value === "string" && /^[=+@\-\t\r]/.test(value) ? "'" + value : String(value);
    return `"${text.replace(/"/g, '""')}"`;
  }).join(",")).join("\r\n");
}
