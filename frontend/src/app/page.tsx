import { PilotLanding } from "@/components/pilot-landing";
import { PublicLanding } from "@/components/public-landing";
import { isPilotRelease } from "@/lib/release-scope";
export const metadata = {
  title: "Unit Toko | Permintaan kebutuhan cabang",
  description:
    "Katalog kemasan dan permintaan kebutuhan untuk cabang BNI serta akun usaha yang telah disetujui.",
};
export default function Home() {
  return isPilotRelease ? <PilotLanding /> : <PublicLanding />;
}
