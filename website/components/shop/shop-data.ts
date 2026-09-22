export type PublicProduct = {
  id: string;
  familyId: string;
  sku: string;
  name: string;
  unit: string;
  price: number;
  active: boolean;
  category: string;
  image: string;
  description: string;
  packaging: string;
  available: number;
};
export type PublicFamily = {
  id: string;
  name: string;
  products: PublicProduct[];
};
export const SHOP_CATEGORIES = [
  "Semua",
  "Minuman",
  "Pantry & konsumsi",
  "Kebutuhan kantor",
  "Merchandise",
];
export const SHOP_COLLECTIONS = [
  {
    id: "pantry",
    label: "Pantry harian",
    description: "Kopi, teh, dan persediaan meja kerja.",
    image: "coffee",
    products: ["kopi", "teh", "biskuit", "gula", "air", "galon", "tisu"],
  },
  {
    id: "rapat",
    label: "Kebutuhan rapat",
    description: "Konsumsi dan perlengkapan pertemuan.",
    image: "snack",
    products: ["snack", "air", "cup", "tisu", "kopi", "teh"],
  },
  {
    id: "merchandise",
    label: "Merchandise",
    description: "Pilihan untuk kegiatan dan hadiah.",
    image: "tumbler",
    products: ["tumbler", "tas", "kaos"],
  },
];
export const rupiah = (value: number) =>
  new Intl.NumberFormat("id-ID", {
    style: "currency",
    currency: "IDR",
    maximumFractionDigits: 0,
  }).format(value);
export function shopCategory(product: PublicProduct) {
  if (["air", "teh", "kopi", "galon"].includes(product.familyId))
    return "Minuman";
  if (["gula", "biskuit", "snack"].includes(product.familyId))
    return "Pantry & konsumsi";
  if (["tisu", "cup"].includes(product.familyId)) return "Kebutuhan kantor";
  return "Merchandise";
}
export function shopFamilies(products: PublicProduct[]): PublicFamily[] {
  const groups = new Map<string, PublicFamily>();
  for (const product of products.filter((item) => item.active)) {
    const id = product.familyId || product.id.split("--")[0];
    const group = groups.get(id) || {
      id,
      name: product.name.replace(/ · paket \d+ .+$/, ""),
      products: [],
    };
    group.products.push(product);
    groups.set(id, group);
  }
  return [...groups.values()];
}
export function familyProduct(family: PublicFamily, search = "") {
  const query = search.trim().toLocaleLowerCase("id-ID");
  return (
    family.products.find(
      (product) => query && product.sku.toLocaleLowerCase("id-ID") === query,
    ) ||
    family.products.find((product) =>
      `${product.name} ${product.sku}`
        .toLocaleLowerCase("id-ID")
        .includes(query),
    ) ||
    family.products[0]
  );
}
export function safeShopBack(value: string | null) {
  if (!value) return "/shop";
  try {
    const url = new URL(value, "https://unit-toko.invalid");
    if (
      url.origin === "https://unit-toko.invalid" &&
      ["/shop", "/"].includes(url.pathname)
    )
      return url.pathname + url.search;
  } catch {
    /* Fall back to the product list. */
  }
  return "/shop";
}
