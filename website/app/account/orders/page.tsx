import {Suspense} from "react";
import {CustomerAccount} from "@/components/customer-account";
import {customerPageActor} from "@/lib/server/page-session";
export const metadata = {title: "Pesanan saya | Unit Toko"};
export default async function Page() {
  const actor = await customerPageActor("/account/orders");
  return <Suspense fallback={<p>Memuat pesanan…</p>}><CustomerAccount initialActor={actor} view="orders"/></Suspense>;
}
