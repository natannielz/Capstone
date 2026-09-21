import type {Order, State, Substitution} from "./model";
import {invoiceBalance, invoiceTotal, lineProgress, sum} from "./selectors";

export const ORDER_QUEUES = {
  all: "Semua pesanan",
  review: "Menunggu tinjauan",
  preparation: "Perlu disiapkan",
  "needs-pic": "Perlu tindakan PIC",
  "in-transit": "Dalam pengiriman",
  fulfilled: "Terpenuhi",
  closed: "Ditolak atau dibatalkan",
} as const;

export type OrderQueue = keyof typeof ORDER_QUEUES;
export type OrderListQuery = {queue: OrderQueue; q: string; sort: "newest" | "needed"; page: number};

export function readOrderQuery(query: URLSearchParams): OrderListQuery {
  const queue = query.get("queue") || "all";
  const page = Number(query.get("orderPage") || 1);
  return {
    queue: Object.hasOwn(ORDER_QUEUES, queue) ? queue as OrderQueue : "all",
    q: query.get("q") || "",
    sort: query.get("sort") === "needed" ? "needed" : "newest",
    page: Number.isSafeInteger(page) && page > 0 ? page : 1,
  };
}

export function orderFulfillmentLabel(s: State, order: Order) {
  if (order.status === "submitted") return "Menunggu tinjauan";
  if (order.status === "rejected") return "Ditolak";
  if (order.status === "cancelled") return "Dibatalkan";
  const lines = s.orderLines.filter(line => line.orderId === order.id);
  const desired = sum(lines.map(line => line.qty - line.cancelled));
  const accepted = sum(lines.map(line => lineProgress(s, line).accepted));
  if (desired === 0 && lines.some(line => line.cancelled > 0)) return "Dibatalkan";
  if (desired > 0 && accepted >= desired) return "Terpenuhi";
  if (accepted > 0) return "Terpenuhi sebagian";
  return s.shipments.some(shipment => shipment.orderId === order.id && shipment.status === "dispatched")
    ? "Dalam pengiriman" : "Diproses";
}

export function substitutionNeedsDecision(s: State, substitution: Substitution) {
  const line = s.orderLines.find(item => item.id === substitution.orderLineId);
  const order = line && s.orders.find(item => item.id === line.orderId);
  return substitution.status === "pending" && Boolean(line && order?.status === "approved" && lineProgress(s, line).remaining > 0);
}

export function matchesOrderQueue(s: State, order: Order, queue: OrderQueue | string) {
  const lines = s.orderLines.filter(line => line.orderId === order.id);
  switch (queue) {
    case "review": return order.status === "submitted";
    case "preparation": return order.status === "approved" && lines.some(line => {
      const progress = lineProgress(s, line);
      return progress.remaining > progress.staged;
    });
    case "needs-pic": return s.substitutions.some(sub => substitutionNeedsDecision(s, sub) && lines.some(line => line.id === sub.orderLineId))
      || s.shipments.some(shipment => shipment.orderId === order.id && shipment.status === "dispatched");
    case "in-transit": return s.shipments.some(shipment => shipment.orderId === order.id && shipment.status === "dispatched");
    case "fulfilled": return order.status === "approved" && orderFulfillmentLabel(s, order) === "Terpenuhi";
    case "closed": return ["Ditolak", "Dibatalkan"].includes(orderFulfillmentLabel(s, order));
    default: return true;
  }
}

export function orderValue(s: State, orderId: string) {
  return sum(s.orderLines.filter(line => line.orderId === orderId).map(line => (line.qty - line.cancelled) * line.price));
}

export function relatedOrderInvoices(s: State, orderId: string) {
  const orderLines = new Set(s.orderLines.filter(line => line.orderId === orderId).map(line => line.id));
  const shipmentLines = new Set(s.shipmentLines.filter(line => orderLines.has(line.orderLineId)).map(line => line.id));
  return s.invoices.flatMap(invoice => {
    const related = s.invoiceLines.filter(line => line.invoiceId === invoice.id && shipmentLines.has(line.shipmentLineId));
    if (!related.length) return [];
    return [{
      invoice,
      orderAmount: sum(related.map(line => line.subtotal + line.tax)),
      total: invoiceTotal(s, invoice.id),
      balance: invoiceBalance(s, invoice.id),
      allocated: sum(s.allocations.filter(allocation => allocation.invoiceId === invoice.id).map(allocation => allocation.amount)),
      credited: sum(s.credits.filter(credit => credit.invoiceId === invoice.id && credit.status === "approved").map(credit => credit.amount)),
      shared: s.invoiceLines.some(line => line.invoiceId === invoice.id && !shipmentLines.has(line.shipmentLineId)),
    }];
  });
}
