import type { Metadata } from 'next';
import { PurchaseOrderWorkspace } from '../../../modules/procurement/PurchaseOrderWorkspace';

export const metadata: Metadata = { title: 'Pesanan pembelian | Unit Toko', description: 'Usulan pembelian berdasarkan permintaan, pemasok dan rincian komersial yang ditinjau.', robots: { index: false, follow: false } };
export default function PurchaseOrdersPage() { return <PurchaseOrderWorkspace />; }
