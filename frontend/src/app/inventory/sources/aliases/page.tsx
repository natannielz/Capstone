import type { Metadata } from "next";
import { StockSourceAliasWorkspace } from "../../../../modules/inventory/StockSourceAliasWorkspace";
export const metadata: Metadata = {
  title: "Pemetaan kanal | Unit Toko",
  description: "Ajukan dan tinjau penggunaan satu alokasi persediaan oleh kanal lain.",
  robots: { index: false, follow: false },
};
export default function StockSourceAliasPage() { return <StockSourceAliasWorkspace />; }
