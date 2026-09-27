import { StorefrontCatalog } from "@/components/shop/storefront";
import { storefrontPageActor } from "@/lib/server/page-session";
export const metadata = { title: "Belanja | Unit Toko" };
export default async function ShopPage() {
  await storefrontPageActor();
  return <StorefrontCatalog />;
}
