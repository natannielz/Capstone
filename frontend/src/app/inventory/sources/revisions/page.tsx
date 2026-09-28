import type { Metadata } from "next";
import { StockSourceRevisionWorkspace } from "../../../../modules/inventory/StockSourceRevisionWorkspace";
export const metadata: Metadata = {
  title: "Perubahan sumber stok | Unit Toko",
  description: "Bandingkan, ajukan, dan tinjau perubahan pengaturan sumber persediaan.",
  robots: { index: false, follow: false },
};
export default function StockSourceRevisionPage() { return <StockSourceRevisionWorkspace />; }
