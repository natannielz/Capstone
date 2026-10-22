import type { Metadata } from 'next';
import { ReceiptWorkspace } from '../../../modules/fulfillment/ReceiptWorkspace';
export const metadata: Metadata = { title: 'Penerimaan kiriman | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <ReceiptWorkspace />; }
