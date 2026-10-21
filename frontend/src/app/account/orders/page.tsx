import { redirect } from "next/navigation";
import { canonicalRedirect, searchParamsOf } from "@/modules/identity/route-canonical";

type Search = Promise<Record<string, string | string[] | undefined>>;
export const metadata = { title: "Pesanan saya | Unit Toko" };
/** A retained address: the proxy normally redirects before this page runs; it applies the same mapping (D2). */
export default async function Page({ searchParams }: { searchParams: Search }) {
  redirect(canonicalRedirect("/account/orders", searchParamsOf(await searchParams))!);
}
