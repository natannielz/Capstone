import { OrderSubstitutionWorkspace } from '../../../modules/ordering/OrderSubstitutionWorkspace';

export default async function Page({ searchParams }: { searchParams: Promise<{ order?: string | string[] }> }) {
  const { order } = await searchParams;
  const orderId = typeof order === 'string' && /^[1-9][0-9]{0,18}$/.test(order) && BigInt(order) <= BigInt('9223372036854775807') ? order : null;
  return <OrderSubstitutionWorkspace orderId={orderId} />;
}
