import type { Metadata } from 'next';
import { PaymentWorkspace } from '../../../modules/billing/PaymentWorkspace';
export const metadata: Metadata = { title: 'Bukti pembayaran | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <PaymentWorkspace />; }
