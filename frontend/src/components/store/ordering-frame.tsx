'use client';

import { usePathname } from 'next/navigation';
import { StoreFrame } from './store-frame';
import { TaskFrame } from './task-frame';

/** The buyer's own ordering pages (cart, checkout, orders, branch requests) belong to the storefront. Reviews, substitutions and
 *  spending rules are a buyer's task for some people and back-office work for others (TaskFrame, G-037); the rest is back office. */
export const buyerOrderingPath = (path: string) => ['/ordering/drafts', '/ordering/checkout', '/ordering/orders', '/ordering/requests'].includes(path);

export function OrderingFrame({ children }: { children: React.ReactNode }) {
  const pathname = usePathname() ?? '/';
  return buyerOrderingPath(pathname) ? <StoreFrame>{children}</StoreFrame> : <TaskFrame>{children}</TaskFrame>;
}
