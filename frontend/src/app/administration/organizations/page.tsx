import type { Metadata } from "next";
import { OrganizationAdministration } from "../../../modules/organizations/OrganizationAdministration";

export const metadata: Metadata = {
  title: "Organisasi dan unit | Unit Toko",
  description: "Pengajuan dan pemeriksaan perubahan organisasi serta unit Unit Toko.",
  robots: { index: false, follow: false },
};

export default function OrganizationAdministrationPage() { return <OrganizationAdministration />; }
