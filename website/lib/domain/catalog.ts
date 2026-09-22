import type { Product } from "./model";

const photos: Record<string, string> = {
  air: "water",
  teh: "tea",
  kopi: "coffee",
  gula: "sugar",
  tisu: "tissue",
  biskuit: "biscuits",
  cup: "cups",
  galon: "gallon",
  tumbler: "tumbler",
  tas: "tote",
  kaos: "polo",
  snack: "snack",
};

export const CATALOG_GROUPS = [
  "Semua",
  "Minuman",
  "Pantry & konsumsi",
  "Kebutuhan kantor",
  "Merchandise",
] as const;

export const CATALOG_COLLECTIONS = {
  pantry: {
    label: "Pantry",
    products: ["kopi", "teh", "biskuit", "gula", "air", "galon", "tisu"],
  },
  rapat: {
    label: "Rapat",
    products: ["snack", "air", "cup", "tisu", "kopi", "teh"],
  },
  merchandise: { label: "Merchandise", products: ["tumbler", "tas", "kaos"] },
} as const;

export type CatalogCollection = keyof typeof CATALOG_COLLECTIONS;
export type ProductFamily = { id: string; name: string; products: Product[] };

export function productFamilyId(product: Product) {
  return product.id.split("--")[0];
}

export function productImage(product: Product) {
  return `/images/products/${photos[productFamilyId(product)] || "tote"}.png`;
}

export function productGroup(product: Product) {
  const id = productFamilyId(product);
  if (["air", "teh", "kopi", "galon"].includes(id)) return "Minuman";
  if (["gula", "biskuit", "snack"].includes(id)) return "Pantry & konsumsi";
  if (["tisu", "cup"].includes(id)) return "Kebutuhan kantor";
  return "Merchandise";
}

/** Packaging is read from the existing SKU name, never inferred from its photo. */
export function productPackaging(product: Product) {
  const packaging = product.name.match(/ · paket (\d+) (.+)$/);
  return packaging
    ? `Paket ${packaging[1]} ${packaging[2]}`
    : `1 ${product.unit}`;
}

export function productDescription(product: Product) {
  const family = productFamilyId(product);
  const missingContents =
    family === "air"
      ? "Jumlah botol per dus belum dicantumkan."
      : family === "cup"
        ? "Jumlah gelas per pak belum dicantumkan."
        : "";
  return [`Pemesanan dihitung per ${product.unit}.`, missingContents]
    .filter(Boolean)
    .join(" ");
}

export function productFamilies(products: Product[]): ProductFamily[] {
  const families = new Map<string, ProductFamily>();
  for (const product of products.filter((item) => item.active)) {
    const id = productFamilyId(product);
    const base = products.find((item) => item.id === id);
    const family = families.get(id) || {
      id,
      name: base?.name || product.name.replace(/ · paket \d+ .+$/, ""),
      products: [],
    };
    family.products.push(product);
    families.set(id, family);
  }
  return [...families.values()];
}

export function chooseCatalogProduct(
  family: ProductFamily,
  selectedId: string | undefined,
  query: string,
) {
  const search = query.trim().toLocaleLowerCase("id-ID");
  const matches = (product: Product) =>
    `${product.name} ${product.sku}`
      .toLocaleLowerCase("id-ID")
      .includes(search);
  return (
    family.products.find(
      (product) => search && product.sku.toLocaleLowerCase("id-ID") === search,
    ) ||
    family.products.find(
      (product) => product.id === selectedId && (!search || matches(product)),
    ) ||
    family.products.find(matches) ||
    family.products[0]
  );
}

export function resolveCatalogProduct(
  products: Product[],
  value: string | null,
) {
  if (!value) return undefined;
  return products.find(
    (product) =>
      product.active && (product.id === value || product.sku === value),
  );
}

export function isCatalogCollection(
  value: string | null,
): value is CatalogCollection {
  return value !== null && Object.hasOwn(CATALOG_COLLECTIONS, value);
}

export function catalogLoginHref(
  intent: { collection?: CatalogCollection; product?: string } = {},
) {
  const query = new URLSearchParams({ view: "catalog" });
  if (intent.collection) query.set("collection", intent.collection);
  if (intent.product) query.set("product", intent.product);
  return `/staff/login?${new URLSearchParams({ next: `/workspace?${query.toString()}` })}`;
}

export function validCatalogQuantity(value: string | number) {
  return (
    /^\d+$/.test(String(value)) &&
    Number.isInteger(Number(value)) &&
    Number(value) >= 1 &&
    Number(value) <= 10000
  );
}
