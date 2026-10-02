import type { Metadata } from 'next';
import { SupplierWorkspace } from '../../../modules/procurement/SupplierWorkspace';

export const metadata: Metadata = { title: 'Pemasok dan kode sumber | Unit Toko', description: 'Pengajuan, verifikasi independen dan pemetaan kode barang pemasok.', robots: { index: false, follow: false } };
export default function SuppliersPage() { return <SupplierWorkspace />; }
