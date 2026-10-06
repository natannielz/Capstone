import type { Metadata } from 'next';
import { StockReleaseWorkspace } from '../../../modules/inventory/StockReleaseWorkspace';

export const metadata: Metadata = { title: 'Pelepasan stok ditahan | Unit Toko',
  description: 'Ajukan dan tinjau pelepasan kemasan dari stok ditahan berdasarkan mutu dan nilai yang disetujui.', robots: { index: false, follow: false } };
export default function StockReleasePage() { return <StockReleaseWorkspace />; }
