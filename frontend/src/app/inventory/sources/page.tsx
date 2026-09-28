import type { Metadata } from "next";
import { StockSourceWorkspace } from "../../../modules/inventory/StockSourceWorkspace";
export const metadata: Metadata = {
  title: "Sumber stok | Unit Toko",
  description: "Ajukan dan tinjau pengaturan sumber persediaan berdasarkan gudang dan kanal.",
  robots: { index: false, follow: false },
};
export default function StockSourcePage() { return <StockSourceWorkspace />; }
