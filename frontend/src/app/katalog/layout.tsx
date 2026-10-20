import { StoreFrame } from "@/components/store/store-frame";

/** The signed-out catalog sits in the same storefront frame as the priced one. */
export default function Layout({ children }: { children: React.ReactNode }) { return <StoreFrame>{children}</StoreFrame>; }
