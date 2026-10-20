import { OrderingFrame } from "@/components/store/ordering-frame";

/** Buyer ordering pages sit in the storefront; the rest of this area sits in the shared workspace frame. */
export default function Layout({ children }: { children: React.ReactNode }) { return <OrderingFrame>{children}</OrderingFrame>; }
