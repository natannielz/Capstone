import type {Payment, Product, State} from "./model";
import {relatedOrderInvoices, substitutionNeedsDecision} from "./order-views";
import {paymentAvailable, sum} from "./selectors";

export function customerOrderSectionId(kind: "substitution" | "shipment" | "invoice" | "payment", id: string) {
  return `customer-${kind}-${id}`;
}

export type CustomerOrderAction = {id: string; kind: "substitution" | "receipt" | "proof" | "payment"; title: string; detail: string; href: string};

/** Use the same real customer tasks in the list filter and the detail summary. */
export function customerOrderActions(s: State, orderId: string): CustomerOrderAction[] {
  if (!s.orders.some(order => order.id === orderId)) return [];
  const actions: CustomerOrderAction[] = customerSubstitutionViews(s, orderId)
    .filter(view => view.needsDecision)
    .map(view => ({id: view.substitution.id, kind: "substitution", title: "Tinjau barang pengganti",
      detail: view.replacementProduct?.name || "Toko menunggu keputusan Anda.",
      href: `#${encodeURIComponent(customerOrderSectionId("substitution", view.substitution.id))}`}));
  for (const shipment of s.shipments.filter(item => item.orderId === orderId && item.status === "dispatched")) {
    actions.push({id: shipment.id, kind: "receipt", title: "Konfirmasi barang diterima", detail: `${shipment.number} · Lakukan setelah barang tiba.`,
      href: `#${encodeURIComponent(customerOrderSectionId("shipment", shipment.id))}`});
  }
  for (const {invoice, balance} of relatedOrderInvoices(s, orderId)) {
    const payments = s.payments.filter(payment => payment.invoiceId === invoice.id);
    const pending = payments.filter(payment => payment.status === "recorded");
    for (const payment of pending) {
      if (!s.attachments.some(attachment => attachment.scope === "payment" && attachment.targetId === payment.id)) {
        actions.push({id: payment.id, kind: "proof", title: "Lengkapi bukti pembayaran", detail: `${invoice.number} · Unggah bukti simulasi.`,
          href: `#${encodeURIComponent(customerOrderSectionId("payment", payment.id))}`});
      }
    }
    // Pending verification/allocation is work for the store, not a request to pay twice.
    const awaitingAllocation = payments.some(payment => payment.status === "verified" && paymentAvailable(s, payment.id) > 0);
    if (balance > 0 && !pending.length && !awaitingAllocation) {
      actions.push({id: invoice.id, kind: "payment", title: "Tinjau tagihan & pembayaran", detail: `${invoice.number} · Masih ada sisa tagihan.`,
        href: `#${encodeURIComponent(customerOrderSectionId("invoice", invoice.id))}`});
    }
  }
  return actions;
}

/** Accept only list context, never an arbitrary return URL or extra parameters. */
export function safeCustomerOrderListHref(value: string | null | undefined) {
  const fallback = "/account/orders";
  if (!value || value.length > 2000 || !value.startsWith("/") || value.startsWith("//") || /[\\\u0000-\u001f]/.test(value)) return fallback;
  try {
    const url = new URL(value, "https://unit-toko.local");
    if (url.origin !== "https://unit-toko.local" || url.pathname !== fallback) return fallback;
    const result = new URLSearchParams();
    const q = url.searchParams.get("q")?.trim().slice(0, 200);
    const status = url.searchParams.get("status");
    const page = Number(url.searchParams.get("page"));
    if (q) result.set("q", q);
    if (status && ["all", "preparation", "in-transit", "needs-pic", "fulfilled", "closed"].includes(status)) result.set("status", status);
    if (Number.isSafeInteger(page) && page > 1) result.set("page", String(page));
    return `${fallback}${result.size ? `?${result}` : ""}`;
  } catch { return fallback; }
}

/** Variant IDs are the product route contract; a family link would select the base pack. */
export function customerProductHref(product: Pick<Product, "id">) {
  return `/shop/${encodeURIComponent(product.id)}`;
}

export function customerSubstitutionViews(s: State, orderId: string) {
  const lines = s.orderLines.filter(line => line.orderId === orderId);
  return s.substitutions.flatMap(substitution => {
    const originalLine = lines.find(line => line.id === substitution.orderLineId);
    if (!originalLine) return [];
    const originalProduct = s.products.find(product => product.id === originalLine.productId);
    const replacementProduct = s.products.find(product => product.id === substitution.productId);
    const replacementLine = lines.find(line => line.id === substitution.replacementLineId);
    const quantity = substitution.qty ?? replacementLine?.qty ?? originalLine.qty;
    const needsDecision = substitutionNeedsDecision(s, substitution);
    const status = substitution.status === "cancelled" || (substitution.status === "pending" && !needsDecision)
      ? "Tidak berlaku" : needsDecision ? "Menunggu keputusan" : substitution.status === "approved" ? "Disetujui" : "Ditolak";
    return [{substitution, originalLine, originalProduct, replacementProduct, quantity,
      originalTotal: quantity * originalLine.price, total: quantity * substitution.price, needsDecision, status}];
  });
}

export function customerPaymentLabel(s: State, payment: Payment) {
  if (payment.status === "rejected") return "Ditolak";
  if (payment.status === "recorded") return "Menunggu verifikasi";
  const allocated = sum(s.allocations.filter(allocation => allocation.paymentId === payment.id).map(allocation => allocation.amount));
  return allocated > 0 ? "Dialokasikan" : "Terverifikasi";
}
