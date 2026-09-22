import { headers } from "next/headers";
import { redirect } from "next/navigation";
import type { Actor } from "@/lib/domain/accounts";
import { DomainError } from "@/lib/domain/model";
import { currentActor } from "./http";
import { loginForDestination, safeLoginDestination } from "@/lib/domain/navigation";

export async function optionalPageActor(): Promise<Actor | undefined> {
  let actor: Actor | undefined;
  try {
    actor = await currentActor(new Request("https://unit-toko.internal", {headers: await headers()}));
  } catch (error) {
    if (!(error instanceof DomainError && [401, 403].includes(error.status))) throw error;
  }
  return actor;
}

export async function pageActor(destination: string): Promise<Actor> {
  const actor = await optionalPageActor();
  if (!actor) redirect(loginForDestination(destination));
  return actor;
}

/** Public shopping is available to guests and customers; internal roles use their workspace. */
export async function storefrontPageActor(): Promise<Actor | undefined> {
  const actor = await optionalPageActor();
  if (actor && actor.role !== "customer") redirect("/workspace");
  return actor;
}

export async function loginPageState(next: string | null): Promise<{initialError: string}> {
  let actor: Actor | undefined;
  let initialError = "";
  try {
    actor = await optionalPageActor();
  } catch {
    initialError = "Sesi belum dapat diperiksa. Coba masuk kembali saat layanan tersedia.";
  }
  if (actor) redirect(safeLoginDestination(next, actor.role));
  return {initialError};
}

export async function customerPageActor(destination: string): Promise<Actor> {
  const actor = await pageActor(destination);
  if (actor.role !== "customer") redirect("/workspace");
  return actor;
}
