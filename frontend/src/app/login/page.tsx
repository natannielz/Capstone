import {redirect} from "next/navigation";
import {identityReturnPath} from "@/modules/identity/identity-workflow";
import {isPilotRelease} from "@/lib/release-scope";

function maintainedDestination(raw: string | string[] | undefined): string {
  if (typeof raw !== "string" || raw.length > 2048 || /[\\\u0000-\u001f\u007f]/.test(raw)) return "/identity/account";
  // Retained links identify a screen, never a record in the new data store.
  // Discard old query selectors and fragments before applying the maintained allowlist.
  const path = raw.split(/[?#]/, 1)[0];
  if (path === "/account" || path === "/workspace") return "/identity/account";
  if (path === "/cart") return isPilotRelease ? "/ordering/requests" : "/cart";
  if (path === "/checkout") return isPilotRelease ? "/ordering/requests" : "/ordering/checkout";
  if (path === "/account/orders" || /^\/account\/orders\/[A-Za-z0-9_-]+$/.test(path)) {
    return isPilotRelease ? "/ordering/requests" : "/ordering/orders";
  }
  if (/^\/shop\/[A-Za-z0-9_-]+$/.test(path)) return "/shop";
  return identityReturnPath(path);
}

export default async function Page({searchParams}: {searchParams: Promise<{next?: string | string[]}>}) {
  const query = await searchParams;
  redirect(`/identity/login?returnPath=${encodeURIComponent(maintainedDestination(query.next))}`);
}
