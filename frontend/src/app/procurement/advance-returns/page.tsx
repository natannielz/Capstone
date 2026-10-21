import type { Metadata } from 'next';
import { ProcurementAdvanceReturnWorkspace } from '../../../modules/procurement/ProcurementAdvanceReturnWorkspace';

export const metadata: Metadata = { title: 'Pengembalian uang muka | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ProcurementAdvanceReturnWorkspace />; }
