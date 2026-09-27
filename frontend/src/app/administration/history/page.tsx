import type { Metadata } from "next";
import { AccessHistory } from "../../../modules/administration/AccessHistory";

export const metadata: Metadata = { title: "Riwayat akses | Unit Toko", robots: { index: false, follow: false } };
export default function AccessHistoryPage() { return <AccessHistory />; }
