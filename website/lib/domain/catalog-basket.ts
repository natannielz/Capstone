import type {Product} from "./model";
import {validCatalogQuantity} from "./catalog";

export type CatalogBasketLine = {product: Product; qty: number};
export type UnavailableCatalogLine = {id: string; product?: Product; qty: number};

/** Availability changes never silently remove a buyer's persisted selections. */
export function catalogBasket(products: Product[], items: Record<string, number>) {
  const byId = new Map(products.map(product => [product.id, product]));
  const lines: CatalogBasketLine[] = [], unavailable: UnavailableCatalogLine[] = [];
  for (const [id, qty] of Object.entries(items)) {
    const product = byId.get(id);
    if (!product?.active || !validCatalogQuantity(qty)) unavailable.push({id, product, qty});
    else lines.push({product, qty});
  }
  return {lines, unavailable, canCheckout: lines.length > 0 && unavailable.length === 0};
}
