/** Who is looking at the storefront. Display only: every API authorises independently. */
export type StoreViewer = { kind: 'loading' } | { kind: 'guest' } | { kind: 'member'; name: string; staff: boolean; approver: boolean };

// Everything a branch buyer or branch approver may hold. Any other permission belongs to the cooperative's back office.
const buyerActions = new Set(['catalog.read', 'order.request.read', 'order.request.submit', 'branch.pic', 'order.submit', 'order.read',
  'order.spend.approve', 'invoice.read', 'payment.evidence', 'shipment.read', 'case.read', 'case.request']);

export type StoreMember = Extract<StoreViewer, { kind: 'member' }>;
export function storeMember(name: string, actions: readonly string[]): StoreMember {
  return { kind: 'member', name, staff: actions.some(action => !buyerActions.has(action)), approver: actions.includes('order.spend.approve') };
}

export function storeSignInHref(returnPath: string) { return '/identity/login?returnPath=' + encodeURIComponent(returnPath); }

export type StoreLinks = { home: string; catalog: string; signIn: string; cart: string; orders: string; account: string; staffDoor: string | null };

/** Where the storefront's header and tabs lead. A guest's cart, orders and account go through sign-in and come back. */
export function storeLinks(viewer: StoreViewer, pilot: boolean, currentPath: string): StoreLinks {
  const cart = pilot ? '/ordering/requests' : '/cart', orders = pilot ? '/ordering/requests' : '/ordering/orders';
  const member = viewer.kind === 'member';
  return {
    home: '/', catalog: '/shop', signIn: storeSignInHref(currentPath),
    cart: member ? cart : storeSignInHref(cart), orders: member ? orders : storeSignInHref(orders),
    account: member ? '/identity/account' : storeSignInHref('/identity/account'),
    staffDoor: viewer.kind === 'member' ? viewer.staff ? '/workspace' : null : storeSignInHref('/workspace'),
  };
}
