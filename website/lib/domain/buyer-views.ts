import type {State} from "./model";
import {buyerKey, buyerLabel, type BuyerRef} from "./buyers";

/** Retains division filter URLs from v6 while namespacing customer identities. */
export function buyerFilterValue(ref: BuyerRef): string {
  return buyerKey(ref) ? ref.customerId ? `customer:${ref.customerId}` : ref.divisionId! : "";
}
export function buyerOptions(s: State) {
  const options = new Map<string, {value: string; label: string; ref: BuyerRef}>();
  for (const division of s.divisions) options.set(division.id, {value: division.id, label: division.name, ref: {divisionId: division.id}});
  for (const ref of [...s.orders, ...s.invoices, ...s.payments]) {
    if (!ref.customerId || !buyerKey(ref)) continue;
    const value = buyerFilterValue(ref);
    options.set(value, {value, label: `Pelanggan · ${buyerLabel(s, ref)}`, ref: {divisionId: null, customerId: ref.customerId}});
  }
  return [...options.values()];
}
