"use client";
import { usePathname, useSearchParams } from "next/navigation";

export function useShopLocation() {
  const pathname = usePathname();
  const search = useSearchParams().toString();
  return `${pathname}${search ? `?${search}` : ""}`;
}
