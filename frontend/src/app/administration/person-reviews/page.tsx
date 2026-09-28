import type { Metadata } from "next";
import { PersonReviewAdministration } from "../../../modules/identity/PersonReviewAdministration";

export const metadata: Metadata = {
  title: "Peninjauan keterkaitan akun | Unit Toko",
  description: "Peninjauan pengajuan penangguhan keterkaitan akun dalam lingkup yang disetujui.",
  robots: { index: false, follow: false },
};
export default function PersonReviewsPage() { return <PersonReviewAdministration />; }