import type { Metadata } from 'next';
import { PublicShowcaseWorkspace } from '../../../modules/catalog/PublicShowcaseWorkspace';
export const metadata: Metadata = { title: 'Katalog publik | Unit Toko', description: 'Pilih katalog yang boleh dilihat pengunjung tanpa harga.', robots: { index: false, follow: false } };
export default function CatalogPublicPage() { return <PublicShowcaseWorkspace />; }
