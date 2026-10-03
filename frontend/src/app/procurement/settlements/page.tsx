import type { Metadata } from 'next';
import { ProcurementSettlementWorkspace } from '../../../modules/procurement/ProcurementSettlementWorkspace';

export const metadata: Metadata = { title: 'Verifikasi debit pemasok | Unit Toko', description: 'Pencocokan independen bukti uang muka dengan debit bank dan jurnal akuntansi.', robots: { index: false, follow: false } };
export default function ProcurementSettlementPage() { return <ProcurementSettlementWorkspace />; }
