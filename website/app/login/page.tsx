import {Login} from "@/components/login";
import {headers} from "next/headers";
import {redirect} from "next/navigation";
import {currentActor} from "@/lib/server/http";
import {safeLoginDestination} from "@/lib/domain/navigation";
import {DomainError} from "@/lib/domain/model";

export default async function Page({searchParams}: {searchParams: Promise<{next?: string | string[]}>}) {
  const requestHeaders = await headers();
  const query = await searchParams;
  let destination: string | undefined;
  let initialError = "";
  if (requestHeaders.get("cookie")?.includes("toko_session=")) {
    try {
      const actor = await currentActor(new Request("https://unit-toko.internal/login", {headers: requestHeaders}));
      destination = safeLoginDestination(typeof query.next === "string" ? query.next : null, actor.role);
    } catch (error) {
      if (!(error instanceof DomainError && [401, 403].includes(error.status))) {
        initialError = "Sesi belum dapat diperiksa. Coba masuk kembali saat layanan tersedia.";
      }
    }
  }
  if (destination) redirect(destination);
  return <Login initialError={initialError}/>;
}
