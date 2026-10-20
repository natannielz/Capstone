import { StoreFrame } from "@/components/store/store-frame";

/** The catalog sits in the storefront frame: one header, search, cart and account for every buyer page. */
export default function Layout({ children }: { children: React.ReactNode }) { return <StoreFrame>{children}</StoreFrame>; }
