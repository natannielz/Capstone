import type { Metadata } from 'next';
import { OrderHistoryWorkspace } from '../../../modules/ordering/OrderHistoryWorkspace';

export const metadata: Metadata = { title: 'Pesanan saya | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <OrderHistoryWorkspace />; }
