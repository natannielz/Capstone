import { headers } from "next/headers";
import { redirect } from "next/navigation";
import type { Actor } from "@/lib/domain/accounts";
import { DomainError } from "@/lib/domain/model";
import { currentActor } from "./http";

export async function pageActor(destination: string): Promise<Actor> {
  let actor: Actor | undefined;
  try {
    actor = await currentActor(new Request("https://unit-toko.internal", {headers: await headers()}));
  } catch (error) {
    if (!(error instanceof DomainError && [401, 403].includes(error.status))) throw error;
  }
  if (!actor) redirect(`/login?next=${encodeURIComponent(destination)}`);
  return actor;
}

export async function customerPageActor(destination: string): Promise<Actor> {
  const actor = await pageActor(destination);
  if (actor.role !== "customer") redirect("/workspace");
  return actor;
}
