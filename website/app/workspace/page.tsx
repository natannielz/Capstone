import { Workspace } from "@/components/workspace";
import { redirect } from "next/navigation";
import { pageActor } from "@/lib/server/page-session";
export default async function Page({searchParams}: {searchParams: Promise<Record<string, string | string[] | undefined>>}){
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(await searchParams)) {
    for (const item of Array.isArray(value) ? value : value === undefined ? [] : [value]) query.append(key, item);
  }
  const actor = await pageActor(`/workspace${query.size ? `?${query}` : ""}`);
  if (actor.role === "customer") redirect("/account/orders");
  return <Workspace/>;
}
