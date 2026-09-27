import type { Metadata } from "next";
import { IdentityAdministration } from "../../../modules/identity/IdentityAdministration";

export const metadata: Metadata = {
  title: "Akun dan undangan | Unit Toko",
  description: "Pengelolaan undangan dan akun terverifikasi dalam lingkup yang disetujui.",
  robots: { index: false, follow: false },
};
export default function AccountsAdministrationPage() { return <IdentityAdministration />; }