import type { Metadata } from "next";
import { StockSourcePolicyWorkspace } from "../../../../modules/inventory/StockSourcePolicyWorkspace";
export const metadata: Metadata = {
  title: "Kebijakan operasional stok | Unit Toko",
  description: "Ajukan dan tinjau pencatat stok serta prosedur pengoperasian sesuai penugasan.",
  robots: { index: false, follow: false },
};
export default function StockSourcePolicyPage() { return <StockSourcePolicyWorkspace />; }