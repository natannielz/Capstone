import Link from "next/link";
import { ArrowRight, ClipboardList, ShoppingBag, ShoppingCart } from "lucide-react";

export function ShoppingProgress({
  stage,
  productHref,
}: {
  stage: "cart" | "checkout";
  productHref?: string;
}) {
  const first = productHref
    ? { label: "Pilihan produk", href: productHref, icon: ShoppingBag }
    : { label: "Keranjang", href: "/cart", icon: ShoppingCart };
  const FirstIcon = first.icon;
  return (
    <nav className="shopping-progress" aria-label="Tahapan belanja">
      <ol>
        <li aria-current={stage === "cart" ? "step" : undefined}>
          <Link href={first.href}><FirstIcon size={17} aria-hidden="true" /><span>{first.label}</span></Link>
        </li>
        <li aria-hidden="true" className="shopping-progress-arrow"><ArrowRight size={15} /></li>
        <li aria-current={stage === "checkout" ? "step" : undefined}>
          <span className="shopping-progress-stage"><ClipboardList size={17} aria-hidden="true" /><span>Periksa pesanan</span></span>
        </li>
      </ol>
      <Link className="shopping-progress-history" href="/account/orders">Pesanan saya <ArrowRight size={15} aria-hidden="true" /></Link>
    </nav>
  );
}
