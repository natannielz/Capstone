import type { Metadata } from 'next';
import { InvoiceWorkspace } from '../../../modules/billing/InvoiceWorkspace';
export const metadata: Metadata = { title: 'Tagihan | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <InvoiceWorkspace />; }
