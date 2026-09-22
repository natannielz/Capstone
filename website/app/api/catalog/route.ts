import { productCollections, productDescription, productFamilyId, productGroup, productImage, productPackaging } from "@/lib/domain/catalog";
import { productAvailable } from "@/lib/domain/selectors";
import { errorResponse, json } from "@/lib/server/http";
import { loadState } from "@/lib/server/repository";

/** Public merchandise data only. Operational product and stock records stay private. */
export async function GET() {
  try {
    const state = await loadState();
    const products = state.products.filter(product => product.active).map(product => ({
      id: product.id,
      familyId: productFamilyId(product),
      sku: product.sku,
      name: product.name,
      unit: product.unit,
      price: product.price,
      active: product.active,
      category: product.category,
      group: productGroup(product),
      collections: productCollections(product),
      image: productImage(product),
      description: productDescription(product),
      packaging: productPackaging(product),
      available: Math.max(0, productAvailable(state, product.id)),
    }));
    return json({ products, revision: state.revision });
  } catch (error) {
    return errorResponse(error);
  }
}
