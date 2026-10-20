import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { isPilotRelease } from "@/lib/release-scope";
import { CartWorkspace } from "@/modules/ordering/CartWorkspace";

export const metadata: Metadata = { title: "Keranjang | Unit Toko", robots: { index: false, follow: false } };
/** The first release keeps buyers on branch requests; the full store has a real keranjang. */
export default function CartPage() {
  if (isPilotRelease) redirect("/ordering/requests");
  return <CartWorkspace />;
}
