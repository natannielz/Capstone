import { StorefrontProduct } from "@/components/shop/shopping-pages";
import { storefrontPageActor } from "@/lib/server/page-session";
export const metadata = { title: "Detail produk | Unit Toko" };
export default async function ProductPage({
  params,
}: {
  params: Promise<{ product: string }>;
}) {
  await storefrontPageActor();
  const { product } = await params;
  return <StorefrontProduct productId={product} />;
}
