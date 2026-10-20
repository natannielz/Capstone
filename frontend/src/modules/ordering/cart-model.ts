import { AccessFailure } from '../administration/workflow';
import { orderQuantity, type OrderSelection } from './order-quote-client';
import type { OrderDraft } from './order-draft-client';

/** The keranjang is not a new record: it is the buyer's saved draft for one branch, changed most recently.
 *  Saving it keeps every draft rule (owner and branch scope, revision fence, price check), so a cart is never looser than a draft. */
export const CART_LINE_LIMIT = 100;
export type CartRule = { minimum: number; increment: number };
export type CurrentPackage = { skuId: string; definitionId: string; definitionVersion: string; priceId: string | null; priceVersion: string | null };
export type CartLineState = 'ready' | 'price-changed' | 'package-changed' | 'unavailable';
export const cartReasons = { add: 'Tambah barang ke keranjang', quantity: 'Ubah jumlah barang di keranjang', remove: 'Hapus barang dari keranjang',
  refresh: 'Setujui harga terbaru di keranjang' } as const;

export function pickCart<T extends Pick<OrderDraft, 'id' | 'updatedAt'>>(drafts: readonly T[]): T | null {
  let chosen: T | null = null;
  for (const draft of drafts) {
    const newer = chosen === null || Date.parse(draft.updatedAt) > Date.parse(chosen.updatedAt) ||
      Date.parse(draft.updatedAt) === Date.parse(chosen.updatedAt) && BigInt(draft.id) > BigInt(chosen.id);
    if (newer) chosen = draft;
  }
  return chosen;
}

const checked = (packages: number, rule: CartRule) => orderQuantity(String(packages), rule.minimum, rule.increment);

/** Same package again: quantities add up and the line takes the package and price the buyer has just seen. */
export function addToCart(selections: readonly OrderSelection[], choice: OrderSelection, rule: CartRule): OrderSelection[] {
  const index = selections.findIndex(item => item.skuId === choice.skuId);
  if (index < 0) {
    if (selections.length >= CART_LINE_LIMIT) throw new AccessFailure(400, 'cart_full');
    return [...selections, { ...choice, packages: checked(choice.packages, rule) }];
  }
  const packages = checked(selections[index].packages + choice.packages, rule);
  return selections.map((item, position) => position === index ? { ...choice, packages } : item);
}

export function setCartQuantity(selections: readonly OrderSelection[], skuId: string, packages: number, rule: CartRule): OrderSelection[] {
  const quantity = checked(packages, rule);
  return selections.map(item => item.skuId === skuId ? { ...item, packages: quantity } : item);
}

export function removeFromCart(selections: readonly OrderSelection[], skuId: string): OrderSelection[] {
  return selections.filter(item => item.skuId !== skuId);
}

/** Whether a saved line can go to checkout as it is. A changed price is shown to the buyer to accept, never swapped in silently. */
export function cartLineState(saved: OrderSelection, current: CurrentPackage | null): CartLineState {
  if (current === null || current.priceId === null || current.priceVersion === null) return 'unavailable';
  if (saved.definitionId !== current.definitionId || saved.definitionVersion !== current.definitionVersion) return 'package-changed';
  if (saved.priceId !== current.priceId || saved.priceVersion !== current.priceVersion) return 'price-changed';
  return 'ready';
}

const cents = (amount: string) => BigInt(amount.replace('.', ''));
const decimal = (value: bigint) => { const text = value.toString().padStart(3, '0'); return text.slice(0, -2) + '.' + text.slice(-2); };
/** Unit price × packages as an exact two-decimal string. */
export function lineAmount(unitPrice: string, packages: number) { return decimal(cents(unitPrice) * BigInt(packages)); }
export function cartAmount(amounts: readonly string[]) { return decimal(amounts.reduce((sum, amount) => sum + cents(amount), BigInt(0))); }
