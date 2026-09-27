import { StorefrontCart } from "@/components/shop/shopping-pages";
import { storefrontPageActor } from "@/lib/server/page-session";
export const metadata = { title: "Keranjang | Unit Toko" };
export default async function CartPage() {
  await storefrontPageActor();
  return <StorefrontCart />;
}
