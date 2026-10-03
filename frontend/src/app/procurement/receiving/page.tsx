import type { Metadata } from 'next';
import { ProcurementReceivingWorkspace } from '../../../modules/procurement/ProcurementReceivingWorkspace';

export const metadata: Metadata = { title: 'Penerimaan pengadaan | Unit Toko', description: 'Pemberitahuan pemasok dan penerimaan fisik koli sebelum pemeriksaan barang.', robots: { index: false, follow: false } };
export default function ProcurementReceivingPage() { return <ProcurementReceivingWorkspace />; }
