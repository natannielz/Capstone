import { NextRequest, NextResponse } from "next/server";
import { isMaintainedEntryAlias, isPilotApiPathAllowed, isPilotPathAllowed, isPilotRelease } from "./lib/release-scope";
import { canonicalRedirect, SESSION_COOKIE, signInRedirect } from "./modules/identity/route-canonical";

/** A redirect that depends on the visitor's cookies or the old address must never be stored by a cache. */
function redirect(request: NextRequest, target: string) {
  const response = NextResponse.redirect(new URL(target, request.url), 307);
  response.headers.set("Cache-Control", "no-store");
  return response;
}

export function proxy(request: NextRequest) {
  const path = request.nextUrl.pathname;
  if (isPilotRelease) {
    const api = path === "/api" || path.startsWith("/api/");
    const auth = ["/auth/login", "/auth/logout", "/auth/step-up", "/auth/callback", "/auth/signed-out"].includes(path);
    const asset = path.startsWith("/_next/") || path.startsWith("/images/") || path.startsWith("/fonts/") || path === "/favicon.svg";
    if (api && !isPilotApiPathAllowed(path) || !api && !auth && !asset && !isPilotPathAllowed(path) && !isMaintainedEntryAlias(path)) {
      return new NextResponse(api ? JSON.stringify({ code: "release_unavailable", title: "Fitur belum tersedia pada pilot ini.", status: 404 }) :
        '<!doctype html><html lang="id"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Halaman belum tersedia</title><main style="font-family:system-ui;max-width:40rem;margin:15vh auto;padding:24px"><h1>Halaman belum tersedia pada pilot ini</h1><p>Gunakan katalog dan permintaan barang untuk cabang Anda.</p><a href="/">Kembali ke Unit Toko</a></main></html>',
        { status: 404, headers: { "Content-Type": api ? "application/problem+json" : "text/html; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" } });
    }
    if ((api || auth) && !process.env.UNIT_TOKO_API_ORIGIN) {
      return NextResponse.json({ code: "pilot_service_unavailable", title: "Layanan pilot belum terhubung. Hubungi pengelola.", status: 503 },
        { status: 503, headers: { "Cache-Control": "no-store" } });
    }
  }
  // One address per page (D2): old pages and sign-in doors move to the canonical page, keeping the query and destination.
  const canonical = canonicalRedirect(path, request.nextUrl.searchParams);
  if (canonical) return redirect(request, canonical);
  // Guests never see the shell of a signed-in page (G-012). Only the cookie's presence is read, never its content:
  // with a cookie the page renders and the API decides; an expired cookie still meets the page's own sign-in state.
  const signIn = signInRedirect(path, request.nextUrl.search, request.cookies.has(SESSION_COOKIE));
  if (signIn) return redirect(request, signIn);
  return NextResponse.next();
}

export const config = { matcher: ["/((?!_next/static|_next/image|favicon.ico|robots.txt|sitemap.xml).*)"] };
