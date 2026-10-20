import type { Metadata } from "next";
import { PublicCatalog } from "@/modules/catalog/PublicCatalog";

export const metadata: Metadata = { title: "Katalog | Unit Toko", description: "Kebutuhan kantor, pantry dan kegiatan untuk cabang BNI. Masuk untuk melihat harga dan memesan." };
export default function PublicCatalogPage() { return <PublicCatalog />; }
