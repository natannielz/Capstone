import type { Metadata } from 'next';
import { OrderSubstitutionWorkspace } from '../../../modules/ordering/OrderSubstitutionWorkspace';

export const metadata: Metadata = { title: 'Penggantian barang | Unit Toko', robots: { index: false, follow: false } };
export default async function Page({ searchParams }: { searchParams: Promise<{ order?: string | string[] }> }) {
  const { order } = await searchParams;
  const orderId = typeof order === 'string' && /^[1-9][0-9]{0,18}$/.test(order) && BigInt(order) <= BigInt('9223372036854775807') ? order : null;
  return <OrderSubstitutionWorkspace orderId={orderId} />;
}
