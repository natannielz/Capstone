import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { PublicProduct } from "@/modules/catalog/PublicCatalog";

export const metadata: Metadata = { title: "Rincian produk | Unit Toko" };
export default async function PublicProductPage({ params }: { params: Promise<{ publicId: string }> }) {
  const { publicId } = await params;
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$(?![\s\S])/.test(publicId)) notFound();
  return <PublicProduct key={publicId} publicId={publicId} />;
}
