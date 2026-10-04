import {fetchCsrfToken, RequestNotSentError} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import type {BuyerVariant} from './catalog-buyer-client';

export const buyerPhotoMaximumBytes = 10 * 1024 * 1024;
export type BuyerPhotoTarget = Readonly<{publicId: string; skuId: string; definitionId: string; definitionVersion: string}>;
const invalid = (): never => { throw new AccessFailure(502, 'buyer_photo_response_invalid'); };
const long = (value: string) => /^[1-9][0-9]{0,18}$(?![\s\S])/u.test(value) && BigInt(value) <= BigInt('9223372036854775807');
export function buyerPhotoTarget(item: Pick<BuyerVariant, 'publicId' | 'skuId' | 'definition'>): BuyerPhotoTarget {
  return Object.freeze({publicId: item.publicId, skuId: item.skuId, definitionId: item.definition.id, definitionVersion: item.definition.version});
}
export function buyerPhotoIdentity(actorId: string, target: BuyerPhotoTarget): string {
  if (!long(actorId) || !long(target.skuId) || !long(target.definitionId) || !long(target.definitionVersion) ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/u.test(target.publicId))
    throw new RequestNotSentError(400, 'buyer_photo_target_invalid');
  return [actorId, target.publicId, target.skuId, target.definitionId, target.definitionVersion].join(':');
}
const current = (valid: () => boolean, signal?: AbortSignal) => {
  if (signal?.aborted || !valid()) throw new RequestNotSentError(409, 'buyer_photo_view_changed');
};

/** Authenticated current photo only. No public URL, arbitrary evidence reference, Range, or retry. */
export function createBuyerPhotoGateway(options: {fetch?: typeof globalThis.fetch; origin?: string} = {}) {
  const fetcher = options.fetch ?? globalThis.fetch;
  return {async read(actorId: string, target: BuyerPhotoTarget, valid: () => boolean, signal?: AbortSignal): Promise<Blob | null> {
    buyerPhotoIdentity(actorId, target); current(valid, signal);
    const origin = options.origin ?? window.location.origin;
    if (typeof window !== 'undefined' && new URL(origin).origin !== window.location.origin)
      throw new RequestNotSentError(400, 'invalid_origin');
    const url = new URL(`/api/v1/catalog/variants/${target.publicId}/primary-photo/query`, origin);
    const token = await fetchCsrfToken(new URL('/api/v1/identity/csrf', origin).href, fetcher, signal);
    current(valid, signal);
    const response = await fetcher(new Request(url, {method: 'POST', credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal,
      headers: {'Content-Type': 'application/json', 'X-CSRF-Token': token, 'X-Expected-Actor-Id': actorId}, body: JSON.stringify({purpose: 'catalog-photo'})}));
    try { current(valid, signal); } catch (error) { await response.body?.cancel(); throw error; }
    if (response.status === 204) return null;
    if (!response.ok) { await response.body?.cancel(); throw new AccessFailure(response.status, 'buyer_photo_unavailable'); }
    const mime = response.headers.get('Content-Type'), length = response.headers.get('Content-Length'), hash = response.headers.get('X-Content-Sha256');
    if (response.status !== 200 || !['image/png', 'image/jpeg'].includes(mime ?? '') || length === null ||
        !/^[1-9][0-9]{0,7}$(?![\s\S])/u.test(length) || Number(length) > buyerPhotoMaximumBytes ||
        hash === null || !/^[0-9a-f]{64}$(?![\s\S])/u.test(hash) || response.headers.has('Content-Range') || !response.body) {
      await response.body?.cancel(); return invalid();
    }
    const reader = response.body.getReader(), parts: Uint8Array<ArrayBuffer>[] = []; let size = 0;
    const abort = () => { void reader.cancel().catch(() => undefined); };
    signal?.addEventListener('abort', abort, {once: true});
    try {
      while (true) {
        current(valid, signal); const next = await reader.read(); current(valid, signal);
        if (next.done) break;
        size += next.value.byteLength;
        if (size > Number(length) || size > buyerPhotoMaximumBytes) return invalid();
        parts.push(new Uint8Array(next.value));
      }
    } finally { signal?.removeEventListener('abort', abort); await reader.cancel().catch(() => undefined); reader.releaseLock(); }
    if (size !== Number(length)) return invalid();
    const bytes = new Uint8Array(size); let offset = 0;
    for (const part of parts) { bytes.set(part, offset); offset += part.length; }
    const digest = await crypto.subtle.digest('SHA-256', bytes); current(valid, signal);
    if (Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('') !== hash) return invalid();
    return new Blob([bytes], {type: mime!});
  }};
}
