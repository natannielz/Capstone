import {redirect} from "next/navigation";
import {loginForDestination} from "@/lib/domain/navigation";
import {loginPageState} from "@/lib/server/page-session";

export default async function Page({searchParams}: {searchParams: Promise<{next?: string | string[]}>}) {
  const query = await searchParams;
  const next = typeof query.next === "string" ? query.next : null;
  await loginPageState(next);
  redirect(loginForDestination(next));
}
