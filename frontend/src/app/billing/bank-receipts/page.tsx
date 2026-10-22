import type { Metadata } from 'next';
import { BankReceiptWorkspace } from '../../../modules/billing/BankReceiptWorkspace';
export const metadata: Metadata = { title: 'Penerimaan bank | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <BankReceiptWorkspace />; }
