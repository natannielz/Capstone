import { StorefrontProduct } from "@/components/shop/shopping-pages";
export const metadata = { title: "Detail produk | Unit Toko" };
export default async function ProductPage({
  params,
}: {
  params: Promise<{ product: string }>;
}) {
  const { product } = await params;
  return <StorefrontProduct productId={product} />;
}
