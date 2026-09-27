import type { Metadata } from "next";
import { IdentityAccount } from "../../../modules/identity/IdentityAccount";

export const metadata: Metadata = { title: "Akun saya | Unit Toko", description: "Sesi terverifikasi dan akses kerja akun Unit Toko.", robots: { index: false, follow: false }, referrer: "same-origin" };
export default function IdentityAccountPage() { return <IdentityAccount />; }
