import type { Metadata } from 'next';
import { CaseWorkspace } from '../../../modules/fulfillment/CaseWorkspace';
export const metadata: Metadata = { title: 'Keluhan barang | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <CaseWorkspace />; }
