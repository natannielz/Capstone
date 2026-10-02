import type { Metadata } from 'next';
import { SupplierBankWorkspace } from '../../../modules/procurement/SupplierBankWorkspace';

export const metadata: Metadata = { title: 'Rekening pemasok | Unit Toko', description: 'Pengajuan dan pemeriksaan independen rekening pemasok melalui kebijakan bank yang disetujui.', robots: { index: false, follow: false } };
export default function SupplierBankPage() { return <SupplierBankWorkspace />; }
