import path from "node:path";
import type { NextConfig } from "next";

// The target must be the approved private/trusted API ingress described in identity-hosting.md.
// Local pilot's HTTPS edge routes API traffic directly and does not use this rewrite.
const apiOrigin = process.env.UNIT_TOKO_API_ORIGIN;
if (apiOrigin) {
  const parsed = new URL(apiOrigin);
  if (parsed.protocol !== "https:" || parsed.origin !== apiOrigin || parsed.username || parsed.password)
    throw new Error("UNIT_TOKO_API_ORIGIN must be an HTTPS origin without credentials or path.");
}

const config: NextConfig = {
  typescript: {tsconfigPath: "tsconfig.app.json"},
  // Full-workflow rehearsals must not replace the persistent Release 1 pilot build.
  distDir: process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE === "full" ? ".next-full" : ".next",
  async rewrites() {
    return { beforeFiles: apiOrigin ? [
      { source: "/api/v1/:path*", destination: `${apiOrigin}/api/v1/:path*` },
      { source: "/auth/:path*", destination: `${apiOrigin}/auth/:path*` },
    ] : [], afterFiles: [], fallback: [] };
  },
  allowedDevOrigins: ["127.0.0.1"],
  outputFileTracingRoot: path.resolve(process.cwd(), ".."),
  images: { formats: ["image/webp", "image/avif"] },
};
export default config;
