import type { Metadata } from 'next';
import { OrderReviewWorkspace } from '../../../modules/ordering/OrderReviewWorkspace';

export const metadata: Metadata = { title: 'Peninjauan pesanan | Unit Toko', robots: { index: false, follow: false } };
export default function Page() { return <OrderReviewWorkspace />; }
