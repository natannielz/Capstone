import type { Metadata } from 'next';
import { CatalogPriceWorkspace } from '../../../modules/catalog/CatalogPriceWorkspace';
export const metadata: Metadata = { title: 'Harga kemasan | Unit Toko', description: 'Ajukan dan tinjau harga setiap kemasan sesuai penugasan.', robots: { index: false, follow: false } };
export default function CatalogPricesPage() { return <CatalogPriceWorkspace />; }
