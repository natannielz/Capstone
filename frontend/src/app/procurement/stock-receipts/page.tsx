import type { Metadata } from 'next';
import { ProcurementStockReceiptWorkspace } from '../../../modules/procurement/ProcurementStockReceiptWorkspace';

export const metadata: Metadata = { title: 'Penerimaan stok ditahan | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ProcurementStockReceiptWorkspace />; }
