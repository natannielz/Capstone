import type { Metadata } from 'next';
import { BankStatementWorkspace } from '../../../modules/billing/BankStatementWorkspace';
export const metadata: Metadata = { title: 'Mutasi bank | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <BankStatementWorkspace />; }
