import type { Metadata } from "next";
import { StockSourceSuspensionWorkspace } from "../../../../modules/inventory/StockSourceSuspensionWorkspace";
export const metadata: Metadata = {
  title: "Penghentian sumber stok | Unit Toko",
  description: "Tinjau seluruh kanal sebelum menghentikan penggunaan sumber stok.",
  robots: { index: false, follow: false },
};
export default function StockSourceSuspensionPage() { return <StockSourceSuspensionWorkspace />; }
