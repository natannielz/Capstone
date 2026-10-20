import { orderContextKey, type OrderContext, type OrderContextOption } from './order-quote-client';

type Store = Pick<Storage, 'getItem' | 'setItem'>;
const browserStore = (): Store | undefined => { try { return globalThis.localStorage; } catch { return undefined; } };
const key = (actorId: string) => 'unit-toko.cart-branch.' + actorId;

/** The branch the cart belongs to: the only one the buyer may order for, or the one they last chose on this device.
 *  Only the branch reference is remembered, never cart contents. Null means the buyer has to choose. */
export function cartBranch(actorId: string, options: readonly OrderContextOption[], store: Store | undefined = browserStore()): OrderContextOption | null {
  if (options.length === 1) return options[0];
  let remembered: string | null = null;
  try { remembered = store?.getItem(key(actorId)) ?? null; } catch { remembered = null; }
  return options.find(option => orderContextKey(option.context) === remembered) ?? null;
}

export function rememberCartBranch(actorId: string, context: OrderContext, store: Store | undefined = browserStore()) {
  try { store?.setItem(key(actorId), orderContextKey(context)); } catch { /* a convenience only */ }
}

export function branchLabel(option: OrderContextOption) {
  return (option.organizationName ?? 'Organisasi ' + option.context.organizationId) + ' · ' + (option.branchName ?? 'Cabang ' + option.context.branchId) +
    (option.context.divisionId ? ' · ' + (option.divisionName ?? 'Divisi ' + option.context.divisionId) : '');
}

/** The header's cart count follows saves made anywhere on the page. */
export const CART_CHANGED_EVENT = 'unit-toko:cart-changed';
export function announceCart(lines: number) {
  try { window.dispatchEvent(new CustomEvent<number>(CART_CHANGED_EVENT, { detail: lines })); } catch { /* not in a browser */ }
}
