import { redirect } from "next/navigation";
import { canonicalRedirect, searchParamsOf } from "@/modules/identity/route-canonical";

type Search = Promise<Record<string, string | string[] | undefined>>;
export const metadata = { title: "Pesanan saya | Unit Toko" };
/** An old order link opens that order on the order page (G-009); the proxy normally redirects before this page runs. */
export default async function Page({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Search }) {
  const { id } = await params;
  redirect(canonicalRedirect("/account/orders/" + encodeURIComponent(id), searchParamsOf(await searchParams))!);
}
