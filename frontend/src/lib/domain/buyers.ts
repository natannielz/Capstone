import type {State} from "./model";

export type BuyerRef = {divisionId: string | null; customerId?: string | null};

/** Empty or ambiguous references are never a matching buyer. Legacy division records remain valid. */
export function buyerKey(ref: BuyerRef): string | null {
  if (ref.divisionId && ref.customerId) return null;
  if (typeof ref.divisionId === "string" && ref.divisionId.trim()) return `division:${ref.divisionId}`;
  if (typeof ref.customerId === "string" && ref.customerId.trim()) return `customer:${ref.customerId}`;
  return null;
}

export function sameBuyer(left: BuyerRef, right: BuyerRef): boolean {
  const key = buyerKey(left);
  return key !== null && key === buyerKey(right);
}

export function buyerLabel(state: Pick<State, "users" | "divisions"> & Partial<Pick<State, "orders">>, ref: BuyerRef & {buyerName?:string}): string {
  if (!buyerKey(ref)) return ref.divisionId || ref.customerId ? "Pembeli tidak valid" : "Belum diidentifikasi";
  return ref.customerId
    ? ref.buyerName || state.orders?.find(order => order.customerId === ref.customerId && order.buyerName)?.buyerName || state.users.find(user => user.id === ref.customerId)?.name || "Pelanggan"
    : state.divisions.find(division => division.id === ref.divisionId)?.name || "Divisi";
}
