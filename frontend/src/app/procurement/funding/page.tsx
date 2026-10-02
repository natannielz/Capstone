import type { Metadata } from 'next';
import { ProcurementFundingWorkspace } from '../../../modules/procurement/ProcurementFundingWorkspace';

export const metadata: Metadata = { title: 'Otorisasi uang muka | Unit Toko', description: 'Permintaan dan otorisasi internal uang muka berdasarkan pesanan serta kebijakan keuangan.', robots: { index: false, follow: false } };
export default function ProcurementFundingPage() { return <ProcurementFundingWorkspace />; }
