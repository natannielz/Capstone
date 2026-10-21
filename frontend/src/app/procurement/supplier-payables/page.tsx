import type { Metadata } from 'next';
import { ProcurementSupplierPayableWorkspace } from '../../../modules/procurement/ProcurementSupplierPayableWorkspace';

export const metadata: Metadata = { title: 'Utang pemasok | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ProcurementSupplierPayableWorkspace />; }
