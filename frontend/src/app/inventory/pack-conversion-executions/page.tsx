import type { Metadata } from 'next';
import { PackConversionExecutionWorkspace } from '../../../modules/inventory/PackConversionExecutionWorkspace';

export const metadata: Metadata = {
  title: 'Pelaksanaan buka kemasan | Unit Toko',
  description: 'Pratinjau dan catat konversi lot kemasan tertutup yang tersedia memakai aturan disetujui.',
  robots: { index: false, follow: false },
};
export default function PackConversionExecutionPage() { return <PackConversionExecutionWorkspace />; }
