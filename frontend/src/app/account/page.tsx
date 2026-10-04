import {redirect} from "next/navigation";

export const metadata = {title: "Akun saya | Unit Toko"};
export default function Page() { redirect("/identity/account"); }
