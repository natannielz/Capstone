import type { Metadata } from 'next';
import { AccountingBookWorkspace } from '../../../modules/accounting/AccountingBookWorkspace';

export const metadata: Metadata = {
  title: 'Buku akuntansi | Unit Toko',
  description: 'Pengajuan dan pemeriksaan pemetaan akun serta periode pertama buku akuntansi.',
  robots: { index: false, follow: false },
};

export default function AccountingBooksPage() { return <AccountingBookWorkspace />; }
