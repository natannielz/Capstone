'use client';

import { usePathname } from 'next/navigation';
import { WorkspaceFrame } from '@/components/workspace-kit/workspace-frame';
import { StoreFrame } from './store-frame';

/** The buyer's own ordering pages (cart, checkout, orders, branch requests) belong to the storefront; reviews, incoming requests
 *  and policy pages belong to the back office. */
export const buyerOrderingPath = (path: string) => ['/ordering/drafts', '/ordering/checkout', '/ordering/orders', '/ordering/requests'].includes(path);

export function OrderingFrame({ children }: { children: React.ReactNode }) {
  const pathname = usePathname() ?? '/';
  return buyerOrderingPath(pathname) ? <StoreFrame>{children}</StoreFrame> : <WorkspaceFrame>{children}</WorkspaceFrame>;
}
