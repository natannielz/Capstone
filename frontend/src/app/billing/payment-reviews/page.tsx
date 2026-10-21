import type { Metadata } from 'next';
import { PaymentReviewWorkspace } from '../../../modules/billing/PaymentReviewWorkspace';
export const metadata: Metadata = { title: 'Pemeriksaan pembayaran | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <PaymentReviewWorkspace />; }
