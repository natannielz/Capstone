import { RequestNotSentError } from '../../api/client';
import { AccessFailure } from '../administration/workflow';
import { normalizeCatalogCategory } from './catalog-history';

/** One package of a guest product, as published now. There is no price, stock or internal identity in it. */
export type PublicCatalogPackage = {
  publicId: string; code: string; packageLabel: string; packageKind: string; baseUnitCode: string; baseUnitsPerPackage: number;
  attributes: { key: string; value: string }[]; minimumOrderPackages: number; orderIncrementPackages: number;
};
/** A sample product as anyone may see it before signing in, with every package it publishes now. */
export type PublicCatalogItem = {
  publicId: string; name: string; description: string; categoryCode: string; categoryName: string; packages: PublicCatalogPackage[];
};
export type PublicCatalogCategory = { code: string; name: string; count: number };
export type PublicCatalogPage = { total: number; items: PublicCatalogItem[]; categories: PublicCatalogCategory[] };
export type PublicCatalogQuery = { search?: string; category?: string; sort?: 'name'; offset?: number };
export const publicCatalogPageSize = 24;
/** The same limit as the store search box; the API accepts it too. */
export const publicCatalogSearchLimit = 128;

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/;
const invalid = (): never => { throw new AccessFailure(502, 'public_catalog_response_invalid'); };
const record = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : invalid();
const text = (value: unknown, limit: number, empty = false) =>
  typeof value === 'string' && value.length <= limit && (empty || value.trim().length > 0) && !/[\u0000-\u001f\u007f]/.test(value) ? value : invalid();
const count = (value: unknown, minimum: number) => {
  const number = typeof value === 'number' ? value : typeof value === 'string' && /^[0-9]{1,15}$(?![\s\S])/.test(value) ? Number(value) : NaN;
  return Number.isSafeInteger(number) && number >= minimum ? number : invalid();
};

const publicIdOf = (value: unknown) => typeof value === 'string' && uuid.test(value) ? value : invalid();

function parsePackage(value: unknown): PublicCatalogPackage {
  const row = record(value);
  const attributes = Array.isArray(row.attributes) && row.attributes.length <= 32 ? row.attributes.map(entry => {
    const pair = record(entry); return { key: text(pair.key, 80), value: text(pair.value, 160) };
  }) : invalid();
  return { publicId: publicIdOf(row.publicId), code: text(row.code, 64), packageLabel: text(row.packageLabel, 160),
    packageKind: text(row.packageKind, 32), baseUnitCode: text(row.baseUnitCode, 32), baseUnitsPerPackage: count(row.baseUnitsPerPackage, 1), attributes,
    minimumOrderPackages: count(row.minimumOrderPackages, 1), orderIncrementPackages: count(row.orderIncrementPackages, 1) };
}

/** A guest product always has at least one published package; an empty one is never shown. */
export function parsePublicCatalogItem(value: unknown): PublicCatalogItem {
  const row = record(value);
  const packages = Array.isArray(row.packages) && row.packages.length >= 1 && row.packages.length <= 64 ? row.packages.map(parsePackage) : invalid();
  return { publicId: publicIdOf(row.publicId), name: text(row.name, 160), description: text(row.description, 2000, true),
    categoryCode: text(row.categoryCode, 64), categoryName: text(row.categoryName, 160), packages };
}

export function parsePublicCatalogPage(value: unknown): PublicCatalogPage {
  const row = record(value);
  if (!Array.isArray(row.items) || row.items.length > 48 || !Array.isArray(row.categories) || row.categories.length > 200) return invalid();
  const categories = row.categories.map(entry => {
    const item = record(entry), code = text(item.code, 64);
    if (normalizeCatalogCategory(code) !== code) return invalid();
    return { code, name: text(item.name, 160), count: count(item.count, 1) };
  });
  return { total: count(row.total, 0), items: row.items.map(parsePublicCatalogItem), categories };
}

/** The address of a public catalog view: search, category, order and page only. */
export function publicCatalogUrl(query: PublicCatalogQuery): string {
  const parameters = new URLSearchParams();
  const search = query.search?.trim();
  if (search) parameters.set('q', search);
  if (query.category) parameters.set('kategori', query.category);
  if (query.sort === 'name') parameters.set('urut', 'nama');
  if (query.offset) parameters.set('halaman', String(Math.floor(query.offset / publicCatalogPageSize) + 1));
  const suffix = parameters.toString();
  return '/katalog' + (suffix ? '?' + suffix : '');
}

export function readPublicCatalogQuery(search: string): PublicCatalogQuery {
  const parameters = new URLSearchParams(search), result: PublicCatalogQuery = {};
  const term = parameters.get('q')?.trim().slice(0, publicCatalogSearchLimit); if (term) result.search = term;
  const category = parameters.get('kategori'); if (category && normalizeCatalogCategory(category) === category) result.category = category;
  if (parameters.get('urut') === 'nama') result.sort = 'name';
  const page = Number(parameters.get('halaman')); if (Number.isSafeInteger(page) && page > 1 && page <= 200) result.offset = (page - 1) * publicCatalogPageSize;
  return result;
}

/** Anonymous reads: a plain GET without cookies, so the answer is the same for everyone and safe to cache. */
export function createPublicCatalogGateway(options: { fetch?: typeof globalThis.fetch; origin?: string } = {}) {
  const fetcher = options.fetch ?? globalThis.fetch;
  const url = (path: string) => new URL(path, options.origin ?? window.location.origin);
  async function read(target: URL, signal?: AbortSignal) {
    return fetcher(new Request(target, { method: 'GET', credentials: 'omit', redirect: 'error', signal, headers: { Accept: 'application/json' } }));
  }
  return {
    async query(query: PublicCatalogQuery, signal?: AbortSignal, limit = publicCatalogPageSize): Promise<PublicCatalogPage> {
      const target = url('/api/v1/public/catalog'); const search = query.search?.trim();
      if (search) target.searchParams.set('q', search.slice(0, publicCatalogSearchLimit));
      if (query.category) target.searchParams.set('kategori', query.category);
      if (query.sort === 'name') target.searchParams.set('urut', 'nama');
      target.searchParams.set('offset', String(query.offset ?? 0)); target.searchParams.set('limit', String(limit));
      const response = await read(target, signal);
      if (!response.ok) throw new AccessFailure(response.status, 'public_catalog_unavailable');
      return parsePublicCatalogPage(await response.json());
    },
    async item(publicId: string, signal?: AbortSignal): Promise<PublicCatalogItem | null> {
      if (!uuid.test(publicId)) throw new RequestNotSentError(400, 'public_catalog_target_invalid');
      const response = await read(url('/api/v1/public/catalog/' + publicId), signal);
      if (response.status === 404) return null;
      if (!response.ok) throw new AccessFailure(response.status, 'public_catalog_unavailable');
      return parsePublicCatalogItem(await response.json());
    },
  };
}
