import {Suspense} from "react";
import {CustomerAccount} from "@/components/customer-account";
import {customerPageActor} from "@/lib/server/page-session";
export const metadata = {title: "Profil & alamat | Unit Toko"};
export default async function Page() {
  const actor = await customerPageActor("/account");
  return <Suspense fallback={<p>Memuat akun…</p>}><CustomerAccount initialActor={actor} view="profile"/></Suspense>;
}
