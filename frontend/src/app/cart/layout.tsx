import { StoreFrame } from "@/components/store/store-frame";

/** The keranjang is a buyer page: it sits in the storefront header, not the staff workspace. */
export default function Layout({ children }: { children: React.ReactNode }) { return <StoreFrame>{children}</StoreFrame>; }
