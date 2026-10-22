import type { Metadata } from "next";
import { AccessHistory } from "../../../modules/administration/AccessHistory";

export const metadata: Metadata = { title: "Riwayat perubahan | Unit Toko", robots: { index: false, follow: false } };
export default function AccessHistoryPage() { return <AccessHistory />; }
