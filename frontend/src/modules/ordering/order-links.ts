const orderId = (value: string | null) => value !== null && /^[1-9][0-9]{0,18}$/.test(value) && (value.length < 19 || value <= '9223372036854775807');

/** The order page with one order opened (the target of an old /account/orders/<id> link, G-009). */
export function orderDetailHref(id: string): string { return '/ordering/orders?order=' + id; }

/** The order an address asks to open (`?order=41`), or null when it names none or a malformed one. */
export function requestedOrderId(search: string): string | null {
  const value = new URLSearchParams(search).get('order');
  return orderId(value) ? value : null;
}

/** The draft an address asks the checkout to reopen (`?draft=7`), or null. */
export function requestedDraftId(search: string): string | null { return new URLSearchParams(search).get('draft'); }
