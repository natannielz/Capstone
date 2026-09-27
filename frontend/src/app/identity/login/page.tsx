import type { Metadata } from "next";
import { IdentityLogin } from "../../../modules/identity/IdentityLogin";

export const metadata: Metadata = { title: "Masuk | Unit Toko", description: "Akses akun Unit Toko untuk cabang BNI dan mitra usaha yang disetujui.", robots: { index: false, follow: false }, referrer: "same-origin" };
export default function IdentityLoginPage() { return <IdentityLogin />; }
