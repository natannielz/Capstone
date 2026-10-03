import type { Metadata } from 'next';
import { ProcurementInspectionWorkspace } from '../../../modules/procurement/ProcurementInspectionWorkspace';

export const metadata: Metadata = { title: 'Pemeriksaan barang pengadaan | Unit Toko', description: 'Pengamatan barang dan video asli pembukaan koli, dengan hasil tetap dalam karantina.', robots: { index: false, follow: false } };
export default function ProcurementInspectionPage() { return <ProcurementInspectionWorkspace />; }
