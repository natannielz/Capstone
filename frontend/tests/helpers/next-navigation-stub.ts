// A minimal stand-in for next/navigation when rendering frames outside the Next app router (tests only).
export const usePathname = () => '/billing/invoices';
export const useRouter = () => ({ push() {}, replace() {}, refresh() {}, back() {}, forward() {}, prefetch() {} });
export const useSearchParams = () => new URLSearchParams();
