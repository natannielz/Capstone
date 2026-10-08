import type { Metadata } from 'next';
import { OrderCommercialPolicyWorkspace } from '../../../modules/ordering/OrderCommercialPolicyWorkspace';

export const metadata: Metadata = {
  title: 'Aturan komersial | Unit Toko',
  description: 'Ajukan dan periksa biaya, pajak, kewajiban dan syarat tagihan cabang untuk kanal toko yang disetujui.',
  robots: { index: false, follow: false },
};

export default function CommercialPoliciesPage() { return <OrderCommercialPolicyWorkspace />; }
