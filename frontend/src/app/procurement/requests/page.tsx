import type { Metadata } from 'next';
import { ProcurementRequestWorkspace } from '../../../modules/procurement/ProcurementRequestWorkspace';

export const metadata: Metadata = { title: 'Permintaan pengadaan | Unit Toko', description: 'Permintaan barang dan pemeriksaan pengadaan berdasarkan kemasan katalog.', robots: { index: false, follow: false } };
export default function ProcurementRequestsPage() { return <ProcurementRequestWorkspace />; }
