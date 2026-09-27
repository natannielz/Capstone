import { StorefrontCheckout } from "@/components/shop/checkout-page";
import { storefrontPageActor } from "@/lib/server/page-session";
export const metadata = { title: "Checkout | Unit Toko" };
export default async function CheckoutPage() {
  await storefrontPageActor();
  return <StorefrontCheckout />;
}
