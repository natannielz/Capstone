import type { Metadata } from 'next';
import { PublicSampleWorkspace } from '../../../modules/catalog/PublicSampleWorkspace';
export const metadata: Metadata = { title: 'Contoh katalog untuk tamu | Unit Toko', description: 'Pilih barang yang boleh dilihat tamu sebagai contoh, tanpa harga.', robots: { index: false, follow: false } };
export default function CatalogPublicPage() { return <PublicSampleWorkspace />; }
