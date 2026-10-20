import type { Metadata } from "next";
import { IdentityAccount } from "../../modules/identity/IdentityAccount";

export const metadata: Metadata = { title: "Pekerjaan saya | Unit Toko", description: "Ruang kerja petugas Unit Toko.", robots: { index: false, follow: false }, referrer: "same-origin" };
export default function WorkspacePage() { return <IdentityAccount />; }
