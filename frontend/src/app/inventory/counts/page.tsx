import type { Metadata } from "next";
import { StockCountWorkspace } from "../../../modules/inventory/StockCountWorkspace";
export const metadata: Metadata = {
  title: "Hitung barang | Unit Toko",
  description: "Catat pengamatan fisik barang berdasarkan gudang dan kemasan yang disetujui.",
  robots: { index: false, follow: false },
};
export default function StockCountPage() { return <StockCountWorkspace />; }
