import type { Metadata } from 'next';
import { ReceivingAccountWorkspace } from '../../../modules/billing/ReceivingAccountWorkspace';
export const metadata: Metadata = { title: 'Rekening penerimaan | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ReceivingAccountWorkspace />; }
