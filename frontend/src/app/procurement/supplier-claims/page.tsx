import type { Metadata } from 'next';
import { SupplierClaimWorkspace } from '../../../modules/procurement/SupplierClaimWorkspace';

export const metadata: Metadata = { title: 'Klaim pemasok | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <SupplierClaimWorkspace />; }
