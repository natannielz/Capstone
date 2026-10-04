import {redirect} from "next/navigation";
import {isPilotRelease} from "@/lib/release-scope";

export const metadata = {title: "Pesanan saya | Unit Toko"};
export default function Page() {
  redirect(isPilotRelease ? "/ordering/requests" : "/ordering/orders");
}
