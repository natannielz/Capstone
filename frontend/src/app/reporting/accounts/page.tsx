import type {Metadata} from 'next';
import {AccountingReportWorkspace} from '../../../modules/reporting/AccountingReportWorkspace';

export const metadata: Metadata = {
  title: 'Laporan akun dan jurnal | Unit Toko',
  description: 'Saldo kumulatif dan rincian jurnal tersimpan pada cakupan buku operasional yang disetujui.',
  robots: {index: false, follow: false},
};

export default function AccountingReportsPage() {return <AccountingReportWorkspace />;}
