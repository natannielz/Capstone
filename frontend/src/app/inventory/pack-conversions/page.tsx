import type { Metadata } from "next";
import { PackConversionRuleWorkspace } from "../../../modules/inventory/PackConversionRuleWorkspace";

export const metadata: Metadata = {
  title: "Aturan buka kemasan | Unit Toko",
  description: "Ajukan dan periksa aturan kemasan tertutup menjadi satuan. Persetujuan aturan belum memindahkan stok.",
  robots: { index: false, follow: false },
};
export default function PackConversionRulePage() { return <PackConversionRuleWorkspace />; }
