import type { Metadata } from 'next';
import { StockSourceReconciliationWorkspace } from '../../../modules/inventory/StockSourceReconciliationWorkspace';

export const metadata: Metadata = { title: 'Rekonsiliasi jumlah fisik | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <StockSourceReconciliationWorkspace />; }
