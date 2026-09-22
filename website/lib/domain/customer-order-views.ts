import type {Payment, Product, State} from "./model";
import {substitutionNeedsDecision} from "./order-views";
import {sum} from "./selectors";

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
