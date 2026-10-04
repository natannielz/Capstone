import { redirect } from "next/navigation";
import { isPilotRelease } from "@/lib/release-scope";

export const metadata = { title: "Checkout | Unit Toko" };
export default function CheckoutPage() {
  redirect(isPilotRelease ? "/ordering/requests" : "/ordering/checkout");
}
