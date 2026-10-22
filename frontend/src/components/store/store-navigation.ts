import { buyerTasks, isStaffActor } from '../../modules/identity/identity-workflow';

/** Who is looking at the storefront. Display only: every API authorises independently. */
export type StoreViewer = { kind: 'loading' } | { kind: 'guest' } | { kind: 'member'; name: string; staff: boolean; approver: boolean; orders: boolean; tasks: { href: string; label: string }[] };

export type StoreMember = Extract<StoreViewer, { kind: 'member' }>;
/** Staff are told apart by the back-office pages their grants open (G-017, F-002), never by a permission missing from a buyer list.
 *  `tasks` are the buyer's own pages outside the storefront (receipt, invoices, payments, complaints, approval) for the account menu. */
export function storeMember(name: string, actions: readonly string[]): StoreMember {
  return { kind: 'member', name, staff: isStaffActor(actions), approver: actions.includes('order.spend.approve'),
    orders: actions.includes('order.submit'), tasks: buyerTasks(actions) };
}

export function storeSignInHref(returnPath: string) { return '/identity/login?returnPath=' + encodeURIComponent(returnPath); }

export type StoreLinks = { home: string; catalog: string; signIn: string; cart: string; orders: string | null; account: string; staffDoor: string | null };

/**
 * Where the storefront's header, tabs and footer lead. A guest's cart, orders and account go through sign-in and come back.
 * "Pesanan saya" (/ordering/orders) is offered only where it can show something: not in the first release, whose branch requests
 * are the cart (M2), and not to an account that never places orders (F-010).
 */
export function storeLinks(viewer: StoreViewer, pilot: boolean, currentPath: string): StoreLinks {
  const cart = pilot ? '/ordering/requests' : '/cart';
  const member = viewer.kind === 'member';
  return {
    home: '/', catalog: member ? '/shop' : '/katalog', signIn: storeSignInHref(currentPath),
    cart: member ? cart : storeSignInHref(cart),
    orders: pilot ? null : viewer.kind === 'member' ? viewer.orders ? '/ordering/orders' : null : storeSignInHref('/ordering/orders'),
    account: member ? '/identity/account' : storeSignInHref('/identity/account'),
    staffDoor: viewer.kind === 'member' ? viewer.staff ? '/workspace' : null : storeSignInHref('/workspace'),
  };
}

/** The storefront tab "Katalog" is current on the member catalog and on the guest catalog alike (G-025). */
export const catalogTabCurrent = (path: string) => /^\/(shop|katalog)(\/|$)/.test(path);
