import type { Metadata } from 'next';
import { ProcurementStockValuationWorkspace } from '../../../modules/procurement/ProcurementStockValuationWorkspace';

export const metadata: Metadata = { title: 'Penilaian stok ditahan | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ProcurementStockValuationWorkspace />; }
