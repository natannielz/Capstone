import {Login} from "@/components/login";
import {loginPageState} from "@/lib/server/page-session";

export const metadata = {title: "Masuk pelanggan | Unit Toko"};
export default async function Page({searchParams}: {searchParams: Promise<{next?: string | string[]}>}) {
  const query = await searchParams;
  const state = await loginPageState(typeof query.next === "string" ? query.next : null);
  return <Login portal="customer" initialError={state.initialError}/>;
}
