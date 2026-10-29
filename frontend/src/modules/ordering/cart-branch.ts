import { orderContextKey, type OrderContext, type OrderContextOption } from './order-quote-client';

type Store = Pick<Storage, 'getItem' | 'setItem'>;
const browserStore = (): Store | undefined => { try { return globalThis.localStorage; } catch { return undefined; } };
const key = (actorId: string) => 'unit-toko.cart-branch.' + actorId;

/** The branch the cart belongs to: the one the buyer last chose on this device, if they may still order for it. The browser
 *  never picks a branch by itself, not even the only one allowed. Only the branch reference is remembered, never cart contents. */
export function cartBranch(actorId: string, options: readonly OrderContextOption[], store: Store | undefined = browserStore()): OrderContextOption | null {
  let remembered: string | null = null;
  try { remembered = store?.getItem(key(actorId)) ?? null; } catch { remembered = null; }
  return options.find(option => orderContextKey(option.context) === remembered) ?? null;
}

/** The branch whose keranjang /cart opens (U-026): the buyer's last explicit choice, else the only branch they may order for, so a
 *  one-branch buyer never meets an empty page behind a select. Viewing records nothing: adding to the cart still needs the buyer to
 *  name the branch (cartBranch above), and only that choice is remembered. With several branches the buyer still chooses. */
export function cartViewBranch(actorId: string, options: readonly OrderContextOption[], store: Store | undefined = browserStore()): OrderContextOption | null {
  return cartBranch(actorId, options, store) ?? (options.length === 1 ? options[0] : null);
}

/** The branch the buyer last chose on this device, read without asking the server for the branch list (the header's cart count).
 *  Null before a first choice or when the stored reference is not a well-formed branch. The server still checks access on every read. */
export function rememberedCartContext(actorId: string, store: Store | undefined = browserStore()): OrderContext | null {
  let value: string | null = null;
  try { value = store?.getItem(key(actorId)) ?? null; } catch { value = null; }
  const parts = value?.split(':') ?? [], id = /^[1-9][0-9]{0,18}$/;
  if (parts.length !== 3 || !id.test(parts[0]) || !id.test(parts[1]) || parts[2] !== '' && !id.test(parts[2])) return null;
  return { organizationId: parts[0], branchId: parts[1], divisionId: parts[2] === '' ? null : parts[2] };
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
