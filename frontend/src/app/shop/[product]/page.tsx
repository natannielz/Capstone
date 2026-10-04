import { notFound } from 'next/navigation';
import { BuyerCatalog } from '../../../modules/catalog/BuyerCatalog';
import { isPilotRelease } from '../../../lib/release-scope';

export const metadata = { title: 'Rincian kemasan | Unit Toko' };
export default async function ProductPage({ params }: { params: Promise<{ product: string }> }) {
  const { product } = await params;
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/.test(product)) notFound();
  return <BuyerCatalog key={product} publicId={product} returnPath="/shop" requestOnly={isPilotRelease} />;
}
