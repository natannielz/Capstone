import {Suspense} from "react";
import {CustomerAccount} from "@/components/customer-account";
import {customerPageActor} from "@/lib/server/page-session";
export const metadata = {title: "Detail pesanan | Unit Toko"};
export default async function Page({params}: {params: Promise<{id: string}>}) {
  const {id} = await params;
  const actor = await customerPageActor(`/account/orders/${encodeURIComponent(id)}`);
  return <Suspense fallback={<p>Memuat pesanan…</p>}><CustomerAccount initialActor={actor} view="orders" orderId={id}/></Suspense>;
}
