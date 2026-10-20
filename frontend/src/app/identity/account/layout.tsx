import { StoreFrame } from "@/components/store/store-frame";

/** "Akun saya" for buyers sits in the storefront; staff have the same hub as their back-office home at /workspace. */
export default function Layout({ children }: { children: React.ReactNode }) { return <StoreFrame>{children}</StoreFrame>; }
