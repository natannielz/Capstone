export type CatalogHistoryQuery = { search: string; offset: number };

export function normalizeCatalogHistorySearch(value: string): string {
  if (typeof value !== 'string' || /[\u0000-\u001f\u007f-\u009f\uFFFD\uD800-\uDFFF]/u.test(value)) return '';
  const normalized = value.trim().normalize('NFC');
  return normalized.length <= 128 ? normalized : '';
}

export function readCatalogHistoryQuery(rawSearch: string): CatalogHistoryQuery {
  if (typeof rawSearch !== 'string' || rawSearch.length > 2048 || rawSearch.includes('#')) return { search: '', offset: 0 };
  const parameters = new URLSearchParams(rawSearch), searches = parameters.getAll('q'), pages = parameters.getAll('page');
  const search = searches.length === 1 ? normalizeCatalogHistorySearch(searches[0]) : '';
  const page = pages.length === 1 && /^[1-9][0-9]{0,2}$(?![\s\S])/.test(pages[0]) ? Number(pages[0]) : 1;
  return { search, offset: page <= 401 ? (page - 1) * 25 : 0 };
}

export function catalogHistoryUrl(path: '/shop' | '/catalog/browse', query: CatalogHistoryQuery, hash = ''): string {
  if (path !== '/shop' && path !== '/catalog/browse') throw new TypeError('catalog_history_path_invalid');
  const parameters = new URLSearchParams(), search = normalizeCatalogHistorySearch(query.search);
  const offset = Number.isSafeInteger(query.offset) && query.offset >= 0 && query.offset <= 10000 && query.offset % 25 === 0 ? query.offset : 0;
  if (search) parameters.set('q', search);
  if (offset) parameters.set('page', String(offset / 25 + 1));
  let fragment = '';
  if (typeof hash === 'string' && hash.length <= 128) {
    try {
      const anchor = decodeURIComponent(hash.startsWith('#') ? hash.slice(1) : hash);
      if (anchor === 'catalog-main' || anchor === 'catalog-list') fragment = '#' + anchor;
    } catch { /* Malformed fragments do not belong in catalog history. */ }
  }
  const searchParameters = parameters.toString();
  return path + (searchParameters ? '?' + searchParameters : '') + fragment;
}
