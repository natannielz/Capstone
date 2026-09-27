export const CATALOG_PAGE_SIZE = 24;

export function catalogPage(value: string | null) {
  return value && /^[1-9]\d{0,5}$/.test(value) ? Number(value) : 1;
}

/** Clamp stale links after filtering or products becoming inactive. */
export function paginateCatalog<T>(items: T[], requested: string | null) {
  const pages = Math.max(1, Math.ceil(items.length / CATALOG_PAGE_SIZE));
  const page = Math.min(catalogPage(requested), pages);
  const start = (page - 1) * CATALOG_PAGE_SIZE;
  return {page, pages, total: items.length, start: items.length ? start + 1 : 0,
    end: Math.min(start + CATALOG_PAGE_SIZE, items.length),
    items: items.slice(start, start + CATALOG_PAGE_SIZE)};
}
