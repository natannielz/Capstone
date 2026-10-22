import type { Metadata } from 'next';
import { ProcurementQualityAcceptanceWorkspace } from '../../../modules/procurement/ProcurementQualityAcceptanceWorkspace';

export const metadata: Metadata = { title: 'Penerimaan mutu barang | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ProcurementQualityAcceptanceWorkspace />; }
