import { redirect } from "next/navigation";
import { isPilotRelease } from "@/lib/release-scope";

export const metadata = { title: "Keranjang | Unit Toko" };
export default function CartPage() {
  redirect(isPilotRelease ? "/ordering/requests" : "/ordering/drafts");
}
