import {isRole, type Actor} from "./accounts";
import {DomainError, type State} from "./model";

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

export function buyerRefForActor(actor: Actor, requested: BuyerRef = {divisionId: null}): BuyerRef {
  if (!actor.active || !isRole(actor.role)) throw new DomainError("Akun tidak memiliki akses.", 403);
  if (actor.role === "customer") return {divisionId: null, customerId: actor.id};
  if (actor.role === "pic") {
    if (!actor.divisionId) throw new DomainError("Akun PIC belum memiliki divisi.", 403);
    return {divisionId: actor.divisionId};
  }
  if (requested.divisionId && requested.customerId) throw new DomainError("Pilih satu pembeli: divisi atau pelanggan.");
  return requested.customerId ? {divisionId: null, customerId: requested.customerId} : {divisionId: requested.divisionId || null};
}

/** Business commands still apply their separate role capability checks. */
export function assertBuyerAccess(actor: Actor, ref: BuyerRef): void {
  if (!actor.active || !isRole(actor.role)) throw new DomainError("Akun tidak memiliki akses.", 403);
  if ((actor.role === "pic" || actor.role === "customer") && !sameBuyer(buyerRefForActor(actor), ref)) {
    throw new DomainError(actor.role === "pic" && ref.divisionId ? "Data divisi lain tidak dapat diakses." : "Transaksi pembeli lain tidak dapat diakses.", 403);
  }
}
