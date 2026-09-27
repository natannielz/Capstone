import type { Metadata } from "next";
import { AccessAdministration } from "../../../modules/administration/AccessAdministration";

export const metadata: Metadata = {
  title: "Kelola akses | Unit Toko",
  description: "Pengajuan dan persetujuan akses Unit Toko berdasarkan keanggotaan yang disetujui.",
  robots: { index: false, follow: false },
};

export default function AccessAdministrationPage() {
  return <AccessAdministration />;
}
