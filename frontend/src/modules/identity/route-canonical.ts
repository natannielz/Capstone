import { isPilotRelease } from '../../lib/release-scope';
import { orderDetailHref } from '../ordering/order-links';
import { HOME_PATH, isAllowedReturnPath } from './identity-workflow';

/**
 * One address per page (D2). The proxy (src/proxy.ts) applies these before any page renders; the legacy page files call the
 * same functions in case a request reaches them directly.
 */

/** The application session cookie. Its presence only decides whether to show a sign-in page first; the API alone decides access. */
export const SESSION_COOKIE = '__Host-ut_session';

const signInDoors = new Set(['/login', '/customer/login', '/staff/login']);

/** Where a legacy page lives today, or null when the path is not a legacy page. */
function legacyTarget(path: string): string | null {
  if (path === '/account') return '/identity/account';
  if (path === '/catalog/browse') return '/shop';
  if (path === '/checkout') return isPilotRelease ? '/ordering/requests' : '/ordering/checkout';
  if (path === '/account/orders') return isPilotRelease ? '/ordering/requests' : '/ordering/orders';
  const order = /^\/account\/orders\/([^/]+)$/.exec(path);
  if (order) return isPilotRelease ? '/ordering/requests' : /^[1-9][0-9]{0,18}$/.test(order[1]) && (order[1].length < 19 || order[1] <= '9223372036854775807') ? orderDetailHref(order[1]) : '/ordering/orders';
  return null;
}

/** Adds the request's query to a target that may already carry its own; the target's own parameters win. */
function withQuery(target: string, extra: URLSearchParams): string {
  const mark = target.indexOf('?'), path = mark < 0 ? target : target.slice(0, mark);
  const params = new URLSearchParams(mark < 0 ? '' : target.slice(mark + 1));
  extra.forEach((value, key) => { if (!params.has(key)) params.append(key, value); });
  const query = params.toString();
  return query ? path + '?' + query : path;
}

/** A sign-in destination as written in an old link (`?next=` or `?returnPath=`), mapped to today's page; null when not allowed. */
function destinationOf(raw: string | null): string | null {
  if (raw === null || raw.length > 2048 || /[\\\u0000-\u001f\u007f]/.test(raw)) return null;
  const clean = raw.split('#', 1)[0], mark = clean.indexOf('?');
  const path = mark < 0 ? clean : clean.slice(0, mark);
  const mapped = legacyTarget(path) ?? (path === '/cart' && isPilotRelease ? '/ordering/requests' : path);
  const full = mark < 0 ? mapped : withQuery(mapped, new URLSearchParams(clean.slice(mark + 1)));
  return isAllowedReturnPath(full) ? full : isAllowedReturnPath(mapped) ? mapped : null;
}

/** The canonical address for a legacy page or sign-in door, keeping its query and destination; null for every other path. */
export function canonicalRedirect(pathname: string, search: URLSearchParams): string | null {
  if (signInDoors.has(pathname)) {
    const rest = new URLSearchParams(search), output = new URLSearchParams();
    // The staff door always leads to the staff home unless a page asked for another destination.
    // A repeated destination is ambiguous and counts as none.
    const one = (key: string) => { const values = rest.getAll(key); return values.length === 1 ? values[0] : null; };
    const destination = destinationOf(one('returnPath') ?? one('next')) ?? (pathname === '/staff/login' ? HOME_PATH : null);
    rest.delete('returnPath'); rest.delete('next');
    if (destination) output.set('returnPath', destination);
    rest.forEach((value, key) => output.append(key, value));
    const query = output.toString();
    return '/identity/login' + (query ? '?' + query : '');
  }
  const target = legacyTarget(pathname);
  return target === null ? null : withQuery(target, search);
}

// Pages that need a signed-in person: the staff workspace areas and the buyer's own pages. The guest catalog (/katalog, /shop
// for its guest hand-off), the landing page, sign-in, assets and the API are not listed and stay open.
const signedInAreas = ['/workspace', '/identity/account', '/cart', '/ordering', '/billing', '/fulfillment', '/inventory', '/procurement',
  '/administration', '/accounting', '/reporting', '/operations', '/catalog'];
export const requiresSession = (pathname: string) => signedInAreas.some(area => pathname === area || pathname.startsWith(area + '/'));

/**
 * For a visitor without a session cookie on a signed-in page: the sign-in page that returns to this page and query (G-012).
 * Null when the page is open to guests or a session cookie is present (the page and the API then check the session itself).
 */
export function signInRedirect(pathname: string, search: string, hasSession: boolean): string | null {
  if (hasSession || !requiresSession(pathname)) return null;
  const target = pathname + search;
  const returnPath = isAllowedReturnPath(target) ? target : isAllowedReturnPath(pathname) ? pathname : null;
  return '/identity/login' + (returnPath ? '?returnPath=' + encodeURIComponent(returnPath) : '');
}

/** A page's `searchParams` record as URLSearchParams (repeated keys kept in order). */
export function searchParamsOf(record: Record<string, string | string[] | undefined>): URLSearchParams {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(record)) for (const item of Array.isArray(value) ? value : value === undefined ? [] : [value]) params.append(key, item);
  return params;
}
