import type { Metadata } from 'next';
import { OrderSpendingPolicyWorkspace } from '../../../modules/ordering/OrderSpendingPolicyWorkspace';

export const metadata: Metadata = {
  title: 'Aturan persetujuan belanja | Unit Toko',
  description: 'Ajukan dan tinjau aturan persetujuan belanja untuk cabang dan divisi.',
  robots: { index: false, follow: false },
};

export default function SpendingPoliciesPage() { return <OrderSpendingPolicyWorkspace />; }
