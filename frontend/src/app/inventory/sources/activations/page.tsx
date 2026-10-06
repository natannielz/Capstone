import type { Metadata } from 'next';
import { StockSourceActivationWorkspace } from '../../../../modules/inventory/StockSourceActivationWorkspace';
export const metadata: Metadata = { title: 'Aktivasi sumber stok | Unit Toko',
  description: 'Ajukan dan tinjau serah terima sumber stok kosong sesuai penugasan.', robots: { index: false, follow: false } };
export default function StockSourceActivationPage() { return <StockSourceActivationWorkspace />; }
